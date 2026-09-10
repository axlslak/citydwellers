using System;
using System.Collections.Generic;
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
    /// Safety watchdog for startup write-front reconciliation.
    ///
    /// StorageWriteFrontReconciliationAgent may temporarily stage a persisted bank bag in
    /// normal inventory so it can open and inspect the live contents. A failed safety check
    /// or timeout must never strand that bank bag there, because subsequent write-front
    /// retries search the bank for persisted bank bags and would otherwise deadlock startup.
    ///
    /// This agent never moves symbiants, never clears queue state, and never acts after the
    /// all-bankers readiness barrier opens. On storage workers it only returns a persisted
    /// bank bag that has remained staged in normal inventory longer than the normal
    /// write-front move/open timeout envelope. On Central it reports which workers are still
    /// missing fresh write-front readiness so startup stalls are visible rather than silent.
    /// </summary>
    public class StartupWriteFrontWatchdogAgent : ClientlessPluginEntry
    {
        private const int PollMilliseconds = 500;
        private const int StagedBagRescueSeconds = 8;
        private const int FirstCentralReportSeconds = 12;
        private const int RepeatCentralReportSeconds = 30;

        private static readonly string[] StorageRoles =
        {
            "artillery",
            "infantry",
            "control",
            "support",
            "extermination",
            "spirit",
            "dyna",
            "phatz"
        };

        private string _settingsDir;
        private Dictionary<string, string> _characters;
        private string _role;
        private bool _isCentral;
        private bool _enabled;
        private DateTime _startedUtc;
        private DateTime _nextPollUtc;
        private DateTime _nextCentralReportUtc;
        private string _lastMissingSignature;

        private readonly Dictionary<string, DateTime> _stagedSeenUtc =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _rescueRequestedUtc =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public override void Init(string pluginDir)
        {
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            if (!TryLoadCharacters(out _characters))
                return;

            _role = ResolveCurrentRole();
            if (string.IsNullOrWhiteSpace(_role))
                return;

            _isCentral = string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase);
            _enabled = true;
            _startedUtc = DateTime.UtcNow;
            _nextPollUtc = DateTime.UtcNow;
            _nextCentralReportUtc = DateTime.UtcNow.AddSeconds(FirstCentralReportSeconds);

            Client.OnUpdate += Tick;

            Logger.Information(
                "[CityBankers] STARTUP WRITE-FRONT WATCHDOG initialized character=" +
                Client.CharacterName + " role=" + _role + ".");
        }

        public override void Teardown()
        {
            if (!_enabled)
                return;

            Client.OnUpdate -= Tick;
            _stagedSeenUtc.Clear();
            _rescueRequestedUtc.Clear();
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!_enabled || !Client.InPlay || DateTime.UtcNow < _nextPollUtc)
                return;

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);

            try
            {
                if (TrustedOperators.IsAllBankersReady())
                    return;

                if (_isCentral)
                    TickCentralVisibility();
                else
                    TickWorkerRescue();
            }
            catch (Exception ex)
            {
                Logger.Error(
                    "[CityBankers] STARTUP WRITE-FRONT WATCHDOG failed character=" +
                    Client.CharacterName + ": " + ex);
            }
        }

        private void TickCentralVisibility()
        {
            if (DateTime.UtcNow < _nextCentralReportUtc)
                return;

            var missing = new List<string>();
            foreach (string role in StorageRoles)
            {
                string character;
                if (!_characters.TryGetValue(role, out character) ||
                    string.IsNullOrWhiteSpace(character))
                {
                    missing.Add(role + "=<unconfigured>");
                    continue;
                }

                string path = StorageWriteFrontReconciliationAgent.GetReadyPath(
                    _settingsDir,
                    character);
                if (!IsFreshWriteFrontMarker(path, role, character))
                    missing.Add(role + "=" + character);
            }

            if (missing.Count == 0)
            {
                _nextCentralReportUtc = DateTime.UtcNow.AddSeconds(RepeatCentralReportSeconds);
                return;
            }

            string signature = string.Join("|", missing);
            bool changed = !string.Equals(
                signature,
                _lastMissingSignature,
                StringComparison.Ordinal);

            if (changed || DateTime.UtcNow >= _nextCentralReportUtc)
            {
                string message =
                    "Startup write-front still waiting on " +
                    string.Join(", ", missing) +
                    ". Dispatch and donations remain held until each worker proves a live " +
                    "writable bag.";
                Logger.Warning("[CityBankers] " + message);
                TellKavem(message);
                _lastMissingSignature = signature;
            }

            _nextCentralReportUtc = DateTime.UtcNow.AddSeconds(RepeatCentralReportSeconds);
        }

        private bool IsFreshWriteFrontMarker(
            string path,
            string expectedRole,
            string expectedCharacter)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            try
            {
                JObject marker = JObject.Parse(File.ReadAllText(path));
                string role = marker.GetValue(
                    "Role",
                    StringComparison.OrdinalIgnoreCase)?.ToString();
                string character = marker.GetValue(
                    "Character",
                    StringComparison.OrdinalIgnoreCase)?.ToString();
                JToken readyToken = marker.GetValue(
                    "ReadyUtc",
                    StringComparison.OrdinalIgnoreCase);
                DateTime readyUtc;

                return string.Equals(role, expectedRole, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        character,
                        expectedCharacter,
                        StringComparison.OrdinalIgnoreCase) &&
                    RuntimeStateStore.TryReadUtc(readyToken, out readyUtc) &&
                    readyUtc >= _startedUtc;
            }
            catch
            {
                return false;
            }
        }

        private void TickWorkerRescue()
        {
            if (Trade.IsTrading || Inventory.Items == null)
                return;

            string ownReady = StorageWriteFrontReconciliationAgent.GetReadyPath(
                _settingsDir,
                Client.CharacterName);
            if (File.Exists(ownReady))
                return;

            StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
            StorageWorkerState worker = (state?.Workers ?? new List<StorageWorkerState>())
                .FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.Role, _role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        candidate.Character,
                        Client.CharacterName,
                        StringComparison.OrdinalIgnoreCase));
            if (worker == null)
                return;

            var currentlyStaged = new HashSet<string>(StringComparer.Ordinal);
            foreach (StorageBagState persisted in worker.Bags ?? new List<StorageBagState>())
            {
                if (persisted == null ||
                    !string.Equals(
                        persisted.Source,
                        "bank",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(persisted.LastUniqueIdentity))
                {
                    continue;
                }

                Item staged = Inventory.Items.FirstOrDefault(item =>
                    item != null &&
                    item.Slot.Type == IdentityType.Inventory &&
                    item.UniqueIdentity.Type == IdentityType.Container &&
                    string.Equals(
                        item.UniqueIdentity.ToString(),
                        persisted.LastUniqueIdentity,
                        StringComparison.Ordinal));
                if (staged == null)
                    continue;

                string identity = persisted.LastUniqueIdentity;
                currentlyStaged.Add(identity);

                DateTime firstSeen;
                if (!_stagedSeenUtc.TryGetValue(identity, out firstSeen))
                {
                    _stagedSeenUtc[identity] = DateTime.UtcNow;
                    continue;
                }

                if ((DateTime.UtcNow - firstSeen).TotalSeconds < StagedBagRescueSeconds)
                    continue;

                DateTime lastRequest;
                if (_rescueRequestedUtc.TryGetValue(identity, out lastRequest) &&
                    (DateTime.UtcNow - lastRequest).TotalSeconds < StagedBagRescueSeconds)
                {
                    continue;
                }

                _rescueRequestedUtc[identity] = DateTime.UtcNow;
                string notice =
                    "STARTUP WRITE-FRONT RESCUE " + Client.CharacterName +
                    ": persisted bank bag " + identity +
                    " remained staged in normal inventory beyond the preflight timeout; " +
                    "returning the bag to bank so write-front reconciliation can retry. " +
                    "No symbiant is moved or queue state changed.";
                Logger.Warning("[CityBankers] " + notice);
                TellKavem(notice);
                staged.MoveToBank();
                return;
            }

            foreach (string identity in _stagedSeenUtc.Keys.ToList())
            {
                if (!currentlyStaged.Contains(identity))
                {
                    _stagedSeenUtc.Remove(identity);
                    _rescueRequestedUtc.Remove(identity);
                }
            }
        }

        private bool TryLoadCharacters(out Dictionary<string, string> characters)
        {
            characters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                JObject root = SettingsPaths.ReadBankersSettings(_settingsDir);
                JObject roles = root.GetValue(
                    "Roles",
                    StringComparison.OrdinalIgnoreCase) as JObject;
                if (roles == null)
                    return false;

                foreach (JProperty property in roles.Properties())
                {
                    JObject role = property.Value as JObject;
                    string character = role?.GetValue(
                        "Character",
                        StringComparison.OrdinalIgnoreCase)?.ToString();
                    if (!string.IsNullOrWhiteSpace(character))
                        characters[property.Name] = character.Trim();
                }

                return characters.ContainsKey("central");
            }
            catch
            {
                return false;
            }
        }

        private string ResolveCurrentRole()
        {
            foreach (KeyValuePair<string, string> pair in _characters)
            {
                if (string.Equals(
                        pair.Value,
                        Client.CharacterName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Key;
                }
            }
            return null;
        }

        private void TellKavem(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            TellQueueClient.Enqueue(
                _settingsDir,
                Client.CharacterName,
                TrustedOperators.BootstrapAdmin,
                message);

            RuntimeStateStore.AppendActivity(
                _settingsDir,
                Client.CharacterName,
                _role,
                "TELL -> " + TrustedOperators.BootstrapAdmin + ": " + message);
        }
    }
}
