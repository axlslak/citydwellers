using System;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// Clientless compatibility bridge for player -> Central donation completion.
    ///
    /// Live AO proved three useful facts:
    /// 1. normal banker-to-banker Clientless trades work;
    /// 2. player -> Central completed successfully at least once;
    /// 3. a timer-driven Central Accept can leave the player trade stuck before Confirm,
    ///    even when a later player Accept is observed and Central reasserts Accept.
    ///
    /// Upstream Clientless Bankbot handles the remote-player Accept transition by sending
    /// Confirm and then Accept. CityBankers additionally waits for the real player's
    /// Confirm event before sending the final Accept. This keeps the AO confirmation modal
    /// stable instead of mutating the trade underneath the player's Yes/No dialog.
    ///
    /// BankingServiceAgent remains the sole authority for validation, AO Finished receipt,
    /// queue creation, storage, and ledger state. This bridge never records receipt or moves
    /// an item. It also stays completely quiet while unresolved storage work exists; the
    /// BankingService queue-priority path owns that single decline/retry message.
    /// </summary>
    public class PlayerDonationHandshakeBridge : ClientlessPluginEntry
    {
        private const int PollMilliseconds = 100;
        private const int AcceptAfterPlayerConfirmMilliseconds = 150;
        private const int HandshakeTimeoutSeconds = 60;

        private string _settingsDir;
        private bool _enabled;
        private bool _activeDonation;
        private bool _playerAccepted;
        private bool _confirmSent;
        private bool _playerConfirmed;
        private bool _acceptAfterConfirmSent;
        private Identity _partner = Identity.None;
        private string _partnerName;
        private DateTime _lastOfferChangeUtc;
        private DateTime _playerAcceptedUtc;
        private DateTime _confirmSentUtc;
        private DateTime _playerConfirmedUtc;
        private DateTime _deadlineUtc;
        private DateTime _nextPollUtc;
        private string _offerSignature = string.Empty;

        public override void Init(string pluginDir)
        {
            if (ServicePolicy.IsBagAuditMode())
            {
                return;
            }

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            if (!IsCurrentCharacterCentral())
                return;

            _enabled = true;
            Trade.TradeOpened += OnTradeOpened;
            Trade.TradeStatusChanged += OnTradeStatusChanged;
            Client.OnUpdate += Tick;

            Logger.Information(
                "[CityBankers] PLAYER DONATION HANDSHAKE initialized on Central.");
        }

        public override void Teardown()
        {
            if (!_enabled)
                return;

            Trade.TradeOpened -= OnTradeOpened;
            Trade.TradeStatusChanged -= OnTradeStatusChanged;
            Client.OnUpdate -= Tick;
            Reset();
        }

        private void OnTradeOpened(Identity target)
        {
            if (!_enabled || !Client.InPlay)
                return;

            string targetName = FindPlayerName(target);
            if (WithdrawalStore.LoadAll(_settingsDir).Any(row =>
                WithdrawalStore.OwnsCentralTrade(row) ||
                (WithdrawalStore.HasStatus(row, "central-ready") &&
                 WithdrawalStore.IsAllowedCollector(row, targetName) &&
                 row.PickupExpiresUtc.HasValue && row.PickupExpiresUtc.Value > DateTime.UtcNow)))
            {
                Reset();
                return;
            }

            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            if (queue != null && queue.Batches != null && queue.Batches.Count > 0)
            {
                // BankingServiceAgent owns queue-priority rejection and its one user-facing
                // retry message. Do not arm a donation handshake that cannot proceed.
                Reset();
                return;
            }

            if (!TrustedOperators.IsTrustedAdmin(targetName))
            {
                Reset();
                return;
            }

            _activeDonation = true;
            _partner = target;
            _partnerName = targetName;
            _playerAccepted = false;
            _confirmSent = false;
            _playerConfirmed = false;
            _acceptAfterConfirmSent = false;
            _offerSignature = BuildOfferSignature();
            _lastOfferChangeUtc = DateTime.UtcNow;
            _playerAcceptedUtc = DateTime.MinValue;
            _confirmSentUtc = DateTime.MinValue;
            _playerConfirmedUtc = DateTime.MinValue;
            _deadlineUtc = DateTime.MinValue;

            Logger.Information(
                "[CityBankers] PLAYER DONATION HANDSHAKE armed for player " +
                (targetName ?? target.ToString()) + ".");
        }

        private void OnTradeStatusChanged(Identity target, TradeStatus status)
        {
            if (!_enabled || !_activeDonation)
                return;

            // Clientless quirk: the Identity supplied with a received trade-status callback
            // is not reliable as the remote partner identity. Trade.CurrentTarget captured
            // at TradeOpened is the partner authority.
            if (!Trade.IsTrading || Trade.CurrentTarget != _partner)
                return;

            if (status == TradeStatus.Accept)
            {
                _playerAccepted = true;
                _playerAcceptedUtc = DateTime.UtcNow;

                string message =
                    "PLAYER DONATION HANDSHAKE observed player Accept; treating it as an explicit done-editing signal and preparing immediate Central Confirm.";
                Logger.Information("[CityBankers] " + message);
                RuntimeStateStore.AppendActivity(
                    _settingsDir,
                    Client.CharacterName,
                    "central",
                    message);
                return;
            }

            if (status == TradeStatus.Confirm)
            {
                if (_confirmSent)
                {
                    _playerConfirmed = true;
                    _playerConfirmedUtc = DateTime.UtcNow;

                    string message =
                        "PLAYER DONATION HANDSHAKE observed player Confirm; Central may now send the final Accept without changing the player's confirmation dialog underneath the cursor.";
                    Logger.Information("[CityBankers] " + message);
                    RuntimeStateStore.AppendActivity(
                        _settingsDir,
                        Client.CharacterName,
                        "central",
                        message);
                }
                return;
            }

            if (status == TradeStatus.Finished || status == TradeStatus.Declined)
                Reset();
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!_enabled || !_activeDonation || !Client.InPlay ||
                DateTime.UtcNow < _nextPollUtc)
            {
                return;
            }

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);

            try
            {
                if (!Trade.IsTrading || Trade.CurrentTarget != _partner)
                {
                    Reset();
                    return;
                }

                string signature = BuildOfferSignature();
                if (!string.Equals(signature, _offerSignature, StringComparison.Ordinal))
                {
                    _offerSignature = signature;
                    _lastOfferChangeUtc = DateTime.UtcNow;

                    // Any offer mutation invalidates prior player acceptance/confirmation.
                    _playerAccepted = false;
                    _playerAcceptedUtc = DateTime.MinValue;
                    _confirmSent = false;
                    _playerConfirmed = false;
                    _acceptAfterConfirmSent = false;
                    _confirmSentUtc = DateTime.MinValue;
                    _playerConfirmedUtc = DateTime.MinValue;
                    _deadlineUtc = DateTime.MinValue;
                    return;
                }

                if (_deadlineUtc != DateTime.MinValue &&
                    DateTime.UtcNow >= _deadlineUtc &&
                    Trade.IsTrading)
                {
                    string timeout =
                        "PLAYER DONATION HANDSHAKE timed out waiting for player Confirm/AO Finished; declining the incomplete donation safely.";
                    Logger.Error("[CityBankers] " + timeout);
                    TellPartner(timeout);
                    Trade.Decline();
                    Reset();
                    return;
                }

                if (!_playerAccepted)
                    return;

                int itemCount = Trade.TargetWindowCache?.Items?.Count ?? 0;
                if (itemCount <= 0 || itemCount > ServicePolicy.MaxTradeItems)
                    return;

                bool allManaged = Trade.TargetWindowCache.Items.All(item =>
                {
                    string role;
                    return item != null &&
                        SymbiantCatalog.TryGetDestinationRole(item.Id, out role);
                });
                if (!allManaged)
                    return;

                // Player Accept is itself the stable boundary. The 30-second policy is a
                // humane edit/inactivity window while the player is still deciding; it must
                // never become a post-Accept delay. If the offer mutates after Accept, the
                // signature branch above clears _playerAccepted and requires a new Accept.
                if (!_confirmSent)
                {
                    _confirmSent = true;
                    _confirmSentUtc = DateTime.UtcNow;
                    _deadlineUtc = DateTime.UtcNow.AddSeconds(HandshakeTimeoutSeconds);
                    Trade.Confirm();

                    string confirm =
                        "PLAYER DONATION HANDSHAKE observed your Accept and sent Central Confirm immediately; waiting for you to confirm normally.";
                    Logger.Information("[CityBankers] " + confirm);
                    TellPartner(confirm);
                    return;
                }

                // Do not mutate the trade while the player's confirmation modal is on screen.
                // Wait until AO tells Clientless that the player actually confirmed, then send
                // the final Accept shortly afterwards.
                if (!_playerConfirmed || _acceptAfterConfirmSent)
                    return;

                if ((DateTime.UtcNow - _playerConfirmedUtc).TotalMilliseconds <
                    AcceptAfterPlayerConfirmMilliseconds)
                {
                    return;
                }

                _acceptAfterConfirmSent = true;
                Trade.Accept();

                string accept =
                    "PLAYER DONATION HANDSHAKE sent Central final Accept after your Confirm; waiting for AO Finished.";
                Logger.Information("[CityBankers] " + accept);
                TellPartner(accept);
            }
            catch (Exception ex)
            {
                Logger.Error(
                    "PLAYER DONATION HANDSHAKE tick failed: " + ex);
                RuntimeStateStore.AppendActivity(
                    _settingsDir,
                    Client.CharacterName,
                    "central",
                    "PLAYER DONATION HANDSHAKE ERROR: " + ex);
            }
        }

        private string BuildOfferSignature()
        {
            var items = Trade.TargetWindowCache?.Items;
            if (items == null || items.Count == 0)
                return string.Empty;

            // Preserve multiplicity and trade-window order. This is a stability detector,
            // not an item identity key, so identical copies remain separate occurrences.
            return string.Join(
                "|",
                items.Select((item, index) =>
                    index + ":" +
                    (item?.Id ?? 0) + ":" +
                    (item?.HighId ?? 0) + ":" +
                    (item?.Ql ?? 0) + ":" +
                    (item?.Name ?? string.Empty)));
        }

        private string FindPlayerName(Identity identity)
        {
            PlayerChar player = DynelManager.Players.FirstOrDefault(p =>
                p != null && p.Identity == identity);
            return player?.Name;
        }

        private bool IsCurrentCharacterCentral()
        {
            try
            {
                JObject config = SettingsPaths.ReadBankersSettings(_settingsDir);
                JObject roles = config.GetValue("Roles", StringComparison.OrdinalIgnoreCase) as JObject;
                JObject central = roles?.Properties()
                    .FirstOrDefault(property => string.Equals(
                        property.Name,
                        "central",
                        StringComparison.OrdinalIgnoreCase))?.Value as JObject;
                string character = central?.GetValue(
                    "Character",
                    StringComparison.OrdinalIgnoreCase)?.ToString();
                return string.Equals(
                    character,
                    Client.CharacterName,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void TellPartner(string message)
        {
            if (string.IsNullOrWhiteSpace(_partnerName) || string.IsNullOrWhiteSpace(message))
                return;

            TellQueueClient.Enqueue(
                _settingsDir,
                Client.CharacterName,
                _partnerName,
                CityBankersChatPalette.WhiteBaseMarkup(message));

            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                "central",
                "TELL -> " + _partnerName + ": " + message);
        }

        private void Reset()
        {
            _activeDonation = false;
            _playerAccepted = false;
            _confirmSent = false;
            _playerConfirmed = false;
            _acceptAfterConfirmSent = false;
            _partner = Identity.None;
            _partnerName = null;
            _lastOfferChangeUtc = DateTime.MinValue;
            _playerAcceptedUtc = DateTime.MinValue;
            _confirmSentUtc = DateTime.MinValue;
            _playerConfirmedUtc = DateTime.MinValue;
            _deadlineUtc = DateTime.MinValue;
            _offerSignature = string.Empty;
        }
    }
}
