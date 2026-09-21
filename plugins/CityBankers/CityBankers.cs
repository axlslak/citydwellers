using File = CityDwellers.Shared.DiskFiles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityBankers
{
    public class CityBankers : ClientlessPluginEntry
    {
        private static readonly TimeSpan BankOpenTimeout =
            TimeSpan.FromSeconds(8);

        // The city office building exposes this real terminal to the game UI,
        // but AOSharp currently omits it from DynelManager.AllDynels.
        private const int CityOfficePlayfieldModelId = 6152312;
        private const int DefaultCityOfficeBankTerminalInstance = SettingsPaths.InitialBankTerminalInstance;
        private int _cityOfficeBankTerminalInstance = DefaultCityOfficeBankTerminalInstance;
        private bool _usedCityOfficeBankFallback;
        private bool _portableAttemptPending;
        private DateTime _portableRetryUtc = DateTime.MaxValue;
        private const int PortableBankTerminalId = BankerPersonalItems.PortableBankTerminalId;
        private bool _bankNeedsId;
        private bool _bankWasOpen;
        private string _bankTerminalRevision;
        private readonly Stopwatch _bankConfigPoll = Stopwatch.StartNew();
        private const float CityOfficeBankX = 185f;
        private const float CityOfficeBankY = 6.02f;
        private const float CityOfficeBankZ = 173f;
        private const float CityOfficeBankMaxDistance = 8f;

        private string _settingsDir;
        private string _resultPath;
        private string _tempPath;
        private string _reportCommandPath;
        private string _reportAckPath;
        private string _dataDir;
        private bool _inPlay;
        private bool _diagnosticStarted;
        private bool _legacyDiagnosticActive;
        private bool _snapshotWritten;
        private DateTime _snapshotDueUtc = DateTime.MaxValue;
        private DateTime _bankDeadlineUtc = DateTime.MaxValue;
        private DateTime _nextHealthUtc = DateTime.MinValue;
        private DiagnosticResult _pendingResult;

        public override void Init(string pluginDir)
        {
            CityDwellers.Shared.BuildIdentity.Register();
            Logger.Information("BUILD " + CityDwellers.Shared.BuildIdentity.Label +
                " | revision=" + CityDwellers.Shared.BuildIdentity.Revision);
            string settingsDir;
            string settingsError;
            if (!SettingsPaths.TryEnsureDirectory(out settingsDir, out settingsError))
                throw new InvalidOperationException(settingsError);

            _settingsDir = settingsDir;
            BagOriginTrace.Install(settingsDir);
            SharedBagRecovery.Install(settingsDir);
            StackableItems.Install();
            try
            {
                CityDwellers.Shared.ServiceEvents.Start(settingsDir, Client.CharacterName,
                    () => Client.LocalDynelId, message => Logger.Warning(message));
            }
            catch (Exception ex) { Logger.Warning("Event reporting disabled: " + ex.Message); }
            var bankTerminal = SettingsPaths.ReadBankTerminal(settingsDir);
            _cityOfficeBankTerminalInstance = (int)bankTerminal["Instance"];
            _bankTerminalRevision = (string)bankTerminal["Revision"];

            pluginDir = RuntimeStateStore.GetDataDirectory(settingsDir);
            _dataDir = pluginDir;
            string token = SafeFileToken(Client.CharacterName);

            _resultPath = Path.Combine(
                pluginDir,
                $"citybankers-diagnostic-{token}.json");
            _tempPath = _resultPath + ".tmp";
            _reportCommandPath = Path.Combine(
                pluginDir,
                $"citybankers-report-command-{token}.json");
            _reportAckPath = Path.Combine(
                pluginDir,
                $"citybankers-report-ack-{token}.json");

            DeleteIfExists(_resultPath);
            DeleteIfExists(_tempPath);
            DeleteIfExists(_reportCommandPath);
            DeleteIfExists(_reportCommandPath + ".tmp");
            DeleteIfExists(_reportAckPath);
            DeleteIfExists(_reportAckPath + ".tmp");
            CityDwellers.Shared.ManagerMemory.Current.ClearBankerHealth(Client.CharacterName);

            Logger.Information(
                $"CityBankers diagnostic plugin initialized. Runtime state root='{pluginDir}'.");

            Client.Config.AutoReconnect = true;
            ClientlessSessionGuard.Install();
            Client.MessageReceived += MessageReceived;
            Logger.Information("TRADE CACHE remote offer removal guard installed.");
            Client.OnUpdate += Tick;
            Client.Disconnected += Disconnected;

            Logger.Information(
                $"CityBankers AutoReconnect={Client.Config.AutoReconnect}; " +
                "waiting for CharInPlay.");
        }

        public override void Teardown()
        {
            BagOriginTrace.Stop();
            SharedBagRecovery.Stop();
            ClientlessSessionGuard.Stop();
            CityDwellers.Shared.ServiceEvents.Stop();
            Client.MessageReceived -= MessageReceived;
            Client.OnUpdate -= Tick;
            Client.Disconnected -= Disconnected;
            CityDwellers.Shared.ManagerMemory.Current.ClearBankerHealth(Client.CharacterName);
            Logger.Information("CityBankers diagnostic plugin teardown.");
        }

        private void MessageReceived(object sender, Message e)
        {
            try
            {
                if (e?.Body == null || e.Body.PacketType != PacketType.N3Message)
                    return;

                var n3Message = (N3Message)e.Body;
                if (n3Message.N3MessageType == N3MessageType.Trade)
                {
                    BankerActivityGovernor.Wake();
                    CorrectRemoteOfferRemoval((TradeMessage)e.Body);
                    return;
                }
                if (n3Message.N3MessageType != N3MessageType.CharInPlay)
                    return;

                var charInPlay = (CharInPlayMessage)e.Body;
                if (charInPlay.Identity.Instance != Client.LocalDynelId)
                    return;

                BankerActivityGovernor.Wake();
                _inPlay = true;
                _diagnosticStarted = false;
                _usedCityOfficeBankFallback = false;
                _portableAttemptPending = false;
                _portableRetryUtc = DateTime.MaxValue;
                _snapshotWritten = false;
                _pendingResult = null;
                _snapshotDueUtc = DateTime.UtcNow;
                _bankDeadlineUtc = DateTime.MaxValue;

                Logger.Information(
                    $"CityBankers ready: {Client.CharacterName} reached InPlay. " +
                    "Opening bank on the next update; no fixed world-settling delay.");
            }
            catch (Exception ex)
            {
                Logger.Error($"CityBankers incoming-message handling failed: {ex}");
            }
        }

        private static void CorrectRemoteOfferRemoval(TradeMessage message)
        {
            // Clientless 1.0.16 dispatches MessageReceived before its built-in
            // trade callback. Its RemoveItemAction returns items from BOTH offer
            // windows to local Inventory. Remove only the remote window entry
            // first, so that callback finds nothing to return. Local removals,
            // cancellation and completed-trade receipt processing remain native.
            if (message.Action != TradeAction.RemoveItem || DynelManager.LocalPlayer == null ||
                message.Identity != DynelManager.LocalPlayer.Identity ||
                message.Param2 == DynelManager.LocalPlayer.Identity.Instance ||
                Trade.CurrentTarget == Identity.None || message.Param2 != Trade.CurrentTarget.Instance)
                return;

            var offered = Trade.TargetWindowCache?.Items;
            var item = offered?.FirstOrDefault(value => value != null && value.Slot.Instance == message.Param4);
            if (item == null) return; // Unknown/duplicate removal: no item may be invented.
            offered.Remove(item);
            Logger.Information("TRADE CACHE remote offer removed: " + item.Name + " QL" + item.Ql +
                " AOID=" + item.Id + "; offer slot=" + message.Param4 +
                "; local inventory unchanged.");
        }

        private long _governedTickStamp;

        private void Tick(object sender, double deltaTime)
        {
            _inPlay = Client.InPlay;

            if (!_inPlay)
                return;

            bool startupWork = !_snapshotWritten || _pendingResult != null || _diagnosticStarted;
            if (!BankerActivityGovernor.Due(
                    ref _governedTickStamp,
                    startupWork,
                    BankerActivityGovernor.VegetativeUtilityMilliseconds))
                return;

            PollBankTerminal();
            PublishHealthHeartbeat();

            ProcessReportCommand();

            if (_snapshotWritten)
                return;

            if (!_diagnosticStarted)
            {
                if (DateTime.UtcNow >= _snapshotDueUtc)
                    BeginDiagnostic();

                return;
            }

            if (_pendingResult == null || !_pendingResult.BankAttempted)
                return;

            if (Inventory.Bank.IsOpen)
            {
                CompleteBankOpen();
                return;
            }

            if (DateTime.UtcNow >= _bankDeadlineUtc)
            {
                if (_portableAttemptPending)
                {
                    _portableAttemptPending = false;
                    Logger.Warning("BANK portable terminal did not open bank; trying retained real-terminal fallback.");
                    BeginTerminalDiagnostic();
                    return;
                }
                CompleteDiagnostic(
                    $"Bank did not report open within {BankOpenTimeout.TotalSeconds:F0}s after Use()." +
                    (_usedCityOfficeBankFallback && FindPortableBank() == null
                        ? $" Office bank target instance={_cityOfficeBankTerminalInstance}. " +
                          "Compare with Info Manager: the terminal instance can change. " +
                          "Need new bankid: use #bankid <Instance shown in game>."
                        : string.Empty));
            }
        }

        private void Disconnected()
        {
            BankerActivityGovernor.Wake();
            _inPlay = false;
            _diagnosticStarted = false;
            _usedCityOfficeBankFallback = false;
            _portableAttemptPending = false;
            _portableRetryUtc = DateTime.MaxValue;
            _snapshotWritten = false;
            _pendingResult = null;
            _snapshotDueUtc = DateTime.MaxValue;
            _legacyDiagnosticActive = false;
            _bankDeadlineUtc = DateTime.MaxValue;
            CityDwellers.Shared.ManagerMemory.Current.ClearBankerHealth(Client.CharacterName);
            Logger.Warning(
                $"CityBankers observed {Client.CharacterName} disconnect; " +
                "AutoReconnect remains enabled.");
        }

        private void PollBankTerminal()
        {
            bool open = Inventory.Bank != null && Inventory.Bank.IsOpen;
            bool closedSinceOpen = _bankWasOpen && !open;
            _bankWasOpen = open;
            if (open && _pendingResult != null && !_pendingResult.BankOpened)
            {
                // A late server response must replace the failed startup snapshot,
                // otherwise enrollment can keep waiting despite an open bank.
                _snapshotWritten = false;
                CompleteBankOpen();
            }
            if (open) _bankNeedsId = false;
            if (!closedSinceOpen && _bankConfigPoll.ElapsedMilliseconds < 1000) return;
            _bankConfigPoll.Restart();
            try
            {
                var state = SettingsPaths.ReadBankTerminal(_settingsDir);
                string revision = (string)state["Revision"];
                int instance = (int)state["Instance"];
                bool changed = revision != _bankTerminalRevision || instance != _cityOfficeBankTerminalInstance;
                _bankTerminalRevision = revision;
                _cityOfficeBankTerminalInstance = instance;
                if (!open && (changed || closedSinceOpen ||
                    (DateTime.UtcNow >= _portableRetryUtc && FindPortableBank() != null)))
                {
                    _snapshotWritten = false;
                    _diagnosticStarted = false;
                    _usedCityOfficeBankFallback = false;
                    _portableAttemptPending = false;
                    _portableRetryUtc = DateTime.MaxValue;
                    _bankNeedsId = false;
                    _pendingResult = null;
                    _snapshotDueUtc = DateTime.UtcNow;
                    _nextHealthUtc = DateTime.MinValue;
                    Logger.Information("BANK runtime retry requested; terminal instance=" + instance);
                }
            }
            catch (Exception ex) { Logger.Warning("BANK runtime setting could not be read: " + ex.Message); }
        }

        private void PublishHealthHeartbeat()
        {
            if (DateTime.UtcNow < _nextHealthUtc || string.IsNullOrWhiteSpace(_settingsDir))
                return;

            _nextHealthUtc = DateTime.UtcNow.AddSeconds(5);
            CityDwellers.Shared.ManagerMemory.Current.ReportBankerHealth(new BankerHealthHeartbeat
            {
                ProcessId = Process.GetCurrentProcess().Id,
                ObservedUtc = DateTime.UtcNow,
                Character = Client.CharacterName,
                InPlay = Client.InPlay,
                BankOpen = Inventory.Bank != null && Inventory.Bank.IsOpen,
                BankNeedsId = _bankNeedsId,
                BankTerminalInstance = _cityOfficeBankTerminalInstance,
                InventoryFreeSlots = Inventory.NumFreeSlots,
                InventoryItems = (Inventory.Items ?? new List<Item>())
                    .Where(item => item != null && item.Slot.Type == IdentityType.Inventory)
                    .OrderBy(item => item.Slot.Instance)
                    .Select(item => new InventoryItemSnapshot
                    {
                        Slot = item.Slot.Instance,
                        UniqueIdentity = item.UniqueIdentity.ToString(),
                        AoId = item.Id,
                        HighId = item.HighId,
                        Ql = item.Ql,
                        Name = item.Name ?? string.Empty,
                        IsStackable = StackableItems.StackableAttribute(item),
                        Quantity = StackableItems.ObservedQuantity(item),
                        IsContainer = item.UniqueIdentity.Type == IdentityType.Container
                    })
                    .ToList()
            });
        }

        private static Item FindPortableBank() => Inventory.Items?.FirstOrDefault(item =>
            StorageBagPolicy.IsNormalInventory(item) &&
            (item.Id == PortableBankTerminalId || item.HighId == PortableBankTerminalId));

        private void BeginDiagnostic()
        {
            if (Trade.IsTrading || DynelManager.LocalPlayer == null) return;
            _legacyDiagnosticActive = false;
            if (Inventory.Bank.IsOpen) { CompleteBankOpen(); return; }
            Item portable = FindPortableBank();
            if (portable != null)
            {
                _diagnosticStarted = true;
                _pendingResult = NewFallbackResult();
                _pendingResult.ObservedUtc = DateTime.UtcNow;
                _pendingResult.BankAttempted = true;
                _pendingResult.BankTargetName = portable.Name ?? "Portable Bank Terminal";
                _pendingResult.BankTargetIdentity = portable.Slot.ToString();
                _pendingResult.BankTargetTemplateId = PortableBankTerminalId;
                _bankNeedsId = false;
                _portableAttemptPending = true;
                _portableRetryUtc = DateTime.MaxValue;
                _bankDeadlineUtc = DateTime.UtcNow.Add(BankOpenTimeout);
                try
                {
                    Logger.Information("BANK opening with inventory Portable Bank Terminal (288762).Use(); no room bankid required.");
                    portable.Use();
                    return;
                }
                catch (Exception ex)
                {
                    _portableAttemptPending = false;
                    Logger.Warning("BANK portable Use failed: " + ex.Message + "; trying real-terminal fallback.");
                }
            }
            BeginTerminalDiagnostic();
        }

        // Retained bank discovery/office bankid path, used only after portable absence/failure.
        private void BeginTerminalDiagnostic()
        {
            _legacyDiagnosticActive = true;
            try
            {
                LocalPlayer localPlayer = DynelManager.LocalPlayer;
                if (localPlayer == null || localPlayer.Transform == null)
                {
                    Logger.Debug(
                        "CityBankers diagnostic is waiting for LocalPlayer transform.");
                    return;
                }

                _diagnosticStarted = true;

                Vector3 position = localPlayer.Transform.Position;
                List<Dynel> dynels = DynelManager.AllDynels
                    .Where(d => d != null)
                    .OrderBy(
                        d => d.Name ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var snapshots = new List<DynelSnapshot>();
                var bankCandidates = new List<DynelSnapshot>();

                Logger.Information("======================================");
                Logger.Information(" CityBankers co-location/bank diagnostic");
                Logger.Information("======================================");
                Logger.Information($"Character: {Client.CharacterName}");
                Logger.Information($"Playfield model: {(int)Playfield.ModelId}");
                Logger.Information(
                    $"Position: ({position.X:F3}, {position.Y:F3}, {position.Z:F3})");
                Logger.Information($"All dynels currently known: {dynels.Count}");

                foreach (Dynel dynel in dynels)
                {
                    float distance = TryDistance(localPlayer, dynel);
                    StaticDynel staticDynel = dynel as StaticDynel;

                    var snapshot = new DynelSnapshot
                    {
                        Name = dynel.Name ?? string.Empty,
                        Identity = dynel.Identity.ToString(),
                        Distance = distance,
                        Source = staticDynel != null ? "static-cache" : "server/dynamic",
                        TemplateId = staticDynel != null
                            ? (int?)staticDynel.TemplateId
                            : null
                    };

                    snapshots.Add(snapshot);

                    if (staticDynel != null &&
                        snapshot.Name.IndexOf(
                            "bank",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        bankCandidates.Add(snapshot);
                    }

                    Logger.Information(
                        $"DYNEL [{snapshot.Source}]: {snapshot.Name} | {snapshot.Identity} | " +
                        $"template={FormatNullable(snapshot.TemplateId)} | " +
                        $"distance={snapshot.Distance:F2}m");
                }

                List<PlayerSnapshot> livePlayers = DynelManager.Players
                    .Where(p => p != null)
                    .Select(p => new PlayerSnapshot
                    {
                        Name = p.Name ?? string.Empty,
                        Identity = p.Identity.ToString(),
                        Distance = TryDistance(localPlayer, p)
                    })
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Logger.Information(
                    $"SERVER PLAYER DYNELS (co-location evidence): {livePlayers.Count}");

                foreach (PlayerSnapshot player in livePlayers)
                {
                    Logger.Information(
                        $"PLAYER: {player.Name} | {player.Identity} | " +
                        $"distance={player.Distance:F2}m");
                }

                int staticDynelCount = snapshots.Count(s => s.Source == "static-cache");

                _pendingResult = new DiagnosticResult
                {
                    ObservedUtc = DateTime.UtcNow,
                    Character = Client.CharacterName,
                    PlayfieldModelId = (int)Playfield.ModelId,
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z,
                    DynelCount = snapshots.Count,
                    StaticDynelCount = staticDynelCount,
                    LivePlayerCount = livePlayers.Count,
                    LivePlayers = livePlayers,
                    BankCandidates = bankCandidates,
                    Dynels = snapshots,
                    BankAttempted = false,
                    BankOpened = false,
                    BankItemCount = -1,
                    BankFreeSlots = -1,
                    InventoryItemCount = -1,
                    InventoryFreeSlots = -1,
                    InventoryBagCount = -1,
                    BankBagCount = -1,
                    TotalBagCount = -1
                };

                Dynel bankTarget = FindNearestBankTarget(localPlayer);
                if (bankTarget == null)
                {
                    if (!TryUseCityOfficeBankTerminal(localPlayer, position))
                    {
                        CompleteDiagnostic(
                            "No nearby cached static dynel with 'bank' in its name was found, " +
                            "and the guarded city office terminal fallback did not match; " +
                            "no Use() was attempted.");
                    }

                    return;
                }

                StaticDynel bankStatic = bankTarget as StaticDynel;
                _pendingResult.BankAttempted = true;
                _pendingResult.BankTargetName = bankTarget.Name ?? string.Empty;
                _pendingResult.BankTargetIdentity = bankTarget.Identity.ToString();
                _pendingResult.BankTargetTemplateId = bankStatic != null
                    ? (int?)bankStatic.TemplateId
                    : null;

                Logger.Information(
                    $"BANK TEST: Use() -> {_pendingResult.BankTargetName} | " +
                    $"{_pendingResult.BankTargetIdentity} | " +
                    $"template={FormatNullable(_pendingResult.BankTargetTemplateId)} | " +
                    $"distance={TryDistance(localPlayer, bankTarget):F2}m");
                Logger.Information(
                    "BANK TEST is read-only: no item/bag move or delete operation will be issued.");

                _bankDeadlineUtc = DateTime.UtcNow.Add(BankOpenTimeout);
                bankTarget.Use();
            }
            catch (Exception ex)
            {
                Logger.Error($"CityBankers diagnostic start failed: {ex}");

                if (_pendingResult == null)
                {
                    _pendingResult = NewFallbackResult();
                }

                CompleteDiagnostic(ex.ToString());
            }
        }

        private Dynel FindNearestBankTarget(LocalPlayer localPlayer)
        {
            return DynelManager.AllDynels
                .Where(d => d is StaticDynel)
                .Where(d => !string.IsNullOrWhiteSpace(d.Name))
                .Where(d => d.Name.IndexOf(
                    "bank",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(d => TryDistance(localPlayer, d))
                .FirstOrDefault();
        }

        private bool TryUseCityOfficeBankTerminal(
            LocalPlayer localPlayer,
            Vector3 position)
        {
            if ((int)Playfield.ModelId != CityOfficePlayfieldModelId)
                return false;

            float dx = position.X - CityOfficeBankX;
            float dy = position.Y - CityOfficeBankY;
            float dz = position.Z - CityOfficeBankZ;
            float distance = (float)Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (distance > CityOfficeBankMaxDistance)
            {
                Logger.Warning(
                    $"BANK TEST: city office terminal fallback rejected at " +
                    $"distance={distance:F2}m (maximum {CityOfficeBankMaxDistance:F0}m).");
                return false;
            }

            var terminalIdentity = new Identity(
                IdentityType.Terminal,
                _cityOfficeBankTerminalInstance);
            _usedCityOfficeBankFallback = true;

            _pendingResult.BankAttempted = true;
            _pendingResult.BankTargetName = "Rubi-Ka Banking Service Terminal";
            _pendingResult.BankTargetIdentity = terminalIdentity.ToString();
            _pendingResult.BankTargetTemplateId = null;

            Logger.Information(
                $"BANK TEST: guarded city office fallback GenericCmd Use -> " +
                $"{_pendingResult.BankTargetName} | {terminalIdentity} | " +
                $"playfield={CityOfficePlayfieldModelId} | distance={distance:F2}m.");
            Logger.Information(
                "BANK TEST is read-only: no item/bag move or delete operation will be issued.");

            _bankDeadlineUtc = DateTime.UtcNow.Add(BankOpenTimeout);
            Client.Send(new GenericCmdMessage
            {
                Temp1 = 0,
                Count = 5,
                Action = GenericCmdAction.Use,
                Temp4 = 1,
                User = localPlayer.Identity,
                Target = terminalIdentity,
                Unknown = 1
            });

            return true;
        }

        private void CompleteBankOpen()
        {
            if (_legacyDiagnosticActive) { CompleteDiagnostic(null); return; }
            if (_snapshotWritten || !Inventory.Bank.IsOpen) return;
            // Portable/already-open path: no dynel/player/position diagnostic.
            // Keep the existing readiness file contract for startup and capacity consumers.
            if (_pendingResult == null) _pendingResult = NewFallbackResult();
            _pendingResult.ObservedUtc = DateTime.UtcNow;
            _pendingResult.BankOpenOnly = true;
            _pendingResult.BankOpened = true;
            _pendingResult.BankError = null;
            PopulateInventoryAndBagCounts(_pendingResult);
            _pendingResult.BankItemCount = Inventory.Bank.Items.Count;
            _pendingResult.BankFreeSlots = Inventory.Bank.NumFreeSlots;
            _pendingResult.BankBagCount = Inventory.Bank.Items.Count(StorageBagPolicy.IsStorageBag);
            _pendingResult.TotalBagCount = _pendingResult.InventoryBagCount + _pendingResult.BankBagCount;
            _bankNeedsId = false;
            _portableAttemptPending = false;
            _portableRetryUtc = DateTime.MaxValue;
            _nextHealthUtc = DateTime.MinValue;
            WriteAtomicJson(_pendingResult);
            _snapshotWritten = true;
            Logger.Information("BANK open verified; legacy diagnostics skipped.");
            CityDwellers.Shared.ServiceEvents.Report("bank.open", "info", "Bank opened without legacy diagnostics.",
                new { _pendingResult.TotalBagCount, _pendingResult.InventoryFreeSlots });
        }

        private void CompleteDiagnostic(string error)
        {
            if (_snapshotWritten)
                return;

            if (_pendingResult == null)
                _pendingResult = NewFallbackResult();

            PopulateInventoryAndBagCounts(_pendingResult);

            _pendingResult.BankOpened = Inventory.Bank.IsOpen;
            _bankNeedsId = !Inventory.Bank.IsOpen && FindPortableBank() == null;
            _portableAttemptPending = false;
            _portableRetryUtc = Inventory.Bank.IsOpen ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(30);
            _nextHealthUtc = DateTime.MinValue;

            if (Inventory.Bank.IsOpen)
            {
                _pendingResult.BankItemCount = Inventory.Bank.Items.Count;
                _pendingResult.BankFreeSlots = Inventory.Bank.NumFreeSlots;
                _pendingResult.BankBagCount = Inventory.Bank.Items.Count(
                    item => item != null &&
                        StorageBagPolicy.IsStorageBag(item));
                _pendingResult.TotalBagCount =
                    _pendingResult.InventoryBagCount + _pendingResult.BankBagCount;

                Logger.Information(
                    $"BANK TEST SUCCESS: IsOpen=True items={_pendingResult.BankItemCount} " +
                    $"freeSlots={_pendingResult.BankFreeSlots} " +
                    $"bags={_pendingResult.BankBagCount}.");
            }
            else
            {
                _pendingResult.BankItemCount = -1;
                _pendingResult.BankFreeSlots = -1;
                _pendingResult.BankBagCount = -1;
                _pendingResult.TotalBagCount = -1;
                Logger.Warning("BANK TEST RESULT: IsOpen=False.");
            }

            _pendingResult.BankError = error;
            if (!string.IsNullOrWhiteSpace(error))
                Logger.Warning($"BANK TEST NOTE: {error}");

            Logger.Information(
                $"INVENTORY TEST: items={_pendingResult.InventoryItemCount} " +
                $"freeSlots={_pendingResult.InventoryFreeSlots} " +
                $"bags={_pendingResult.InventoryBagCount}; " +
                $"bank+inventory bags={_pendingResult.TotalBagCount}.");

            CityDwellers.Shared.ServiceEvents.Report("bank.open", _pendingResult.BankOpened ? "info" : "warning",
                _pendingResult.BankOpened ? "Bank opened." : "Bank opening failed.",
                new { _pendingResult.BankError, _pendingResult.TotalBagCount, _pendingResult.InventoryFreeSlots });
            WriteAtomicJson(_pendingResult);
            _snapshotWritten = true;

            Logger.Information(
                $"CityBankers diagnostic snapshot published to '{_resultPath}'.");
        }

        private static DiagnosticResult NewFallbackResult()
        {
            return new DiagnosticResult
            {
                ObservedUtc = DateTime.UtcNow,
                Character = Client.CharacterName,
                BankItemCount = -1,
                BankFreeSlots = -1,
                InventoryItemCount = -1,
                InventoryFreeSlots = -1,
                InventoryBagCount = -1,
                BankBagCount = -1,
                TotalBagCount = -1
            };
        }

        private static void PopulateInventoryAndBagCounts(DiagnosticResult result)
        {
            if (Inventory.Items == null)
                return;

            result.InventoryItemCount = Inventory.Items.Count(
                item => item != null &&
                    item.Slot.Type == IdentityType.Inventory);
            result.InventoryFreeSlots = Inventory.NumFreeSlots;
            result.InventoryBagCount = Inventory.Items.Count(
                item => item != null &&
                    item.Slot.Type == IdentityType.Inventory &&
                    StorageBagPolicy.IsStorageBag(item));
        }

        private List<string> BuildCurrentDynelMessages()
        {
            var messages = new List<string>();
            var player = DynelManager.LocalPlayer;
            if (!Client.InPlay || player == null)
            {
                messages.Add(Client.CharacterName + " is not in play; no current dynel snapshot.");
                return messages;
            }
            var dynels = DynelManager.AllDynels.Where(d => d != null)
                .OrderBy(d => TryDistance(player, d)).ToList();
            var rows = new List<string>();
            foreach (var dynel in dynels)
            {
                var position = dynel.Transform.Position;
                var cached = dynel as StaticDynel;
                rows.Add(DynelMarkup(dynel.Name ?? "(unnamed)") + "\n" +
                    DynelMarkup(dynel.Identity.ToString()) + " | instance=" + dynel.Identity.Instance +
                    " | " + DynelMarkup(dynel.GetType().Name) +
                    (cached != null ? " | static-cache template=" + cached.TemplateId : " | live") +
                    $"\n({position.X:F2}, {position.Y:F2}, {position.Z:F2}) | {TryDistance(player, dynel):F2}m\n\n");
            }
            int pages = Math.Max(1, (rows.Count + 7) / 8);
            for (int page = 0; page < pages; page++)
            {
                string body = DynelMarkup(Client.CharacterName) + " current dynels: " + dynels.Count +
                    "\nPlayfield model: " + (int)Playfield.ModelId + "\n\n" +
                    string.Concat(rows.Skip(page * 8).Take(8));
                messages.Add("<a href=\"text://" + body + "\">Central dynels " +
                    (page + 1) + "/" + pages + "</a>");
            }
            return messages;
        }

        private static string DynelMarkup(string text)
        {
            return text.Replace("&", "&amp;").Replace("<", "&lt;")
                .Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private void ProcessReportCommand()
        {
            string request = CityDwellers.Shared.ManagerMemory.Current.ReadBankerReport(Client.CharacterName);
            bool manual = ServicePolicy.IsBagAuditMode();
            if (request == null && manual && File.Exists(_reportCommandPath)) request = File.ReadAllText(_reportCommandPath);
            if (request == null) return;

            ReportAck ack = new ReportAck
            {
                SentUtc = DateTime.UtcNow,
                Recipient = string.Empty,
                MessageCount = 0
            };

            try
            {
                ReportCommand command = JsonConvert.DeserializeObject<ReportCommand>(request);

                if (command == null)
                    throw new InvalidOperationException("Report command is empty.");

                if (string.IsNullOrWhiteSpace(command.Recipient))
                    throw new InvalidOperationException("Report recipient is empty.");

                if (string.Equals(command.Kind, "dynel", StringComparison.OrdinalIgnoreCase))
                {
                    if (!CentralCharacterGuard.IsCurrentCharacterCentral(
                        _settingsDir, Client.CharacterName))
                        throw new InvalidOperationException("Dynel diagnostic must run on Central.");
                    command.Messages = BuildCurrentDynelMessages();
                }

                if (command.Messages == null || command.Messages.Count == 0)
                    throw new InvalidOperationException("Report has no message lines.");

                if (Client.Chat == null)
                    throw new InvalidOperationException("AO chat client is not available.");

                ack.Recipient = command.Recipient;

                foreach (string message in command.Messages)
                {
                    if (string.IsNullOrWhiteSpace(message))
                        continue;

                    CityDwellers.Shared.TellQueue.Enqueue(
                        _dataDir,
                        Client.CharacterName,
                        command.Recipient,
                        null,
                        message);
                    ack.MessageCount++;
                }

                Logger.Information(
                    $"CityBankers capacity report queued: {ack.MessageCount} tell line(s) " +
                    $"to {ack.Recipient}.");
            }
            catch (Exception ex)
            {
                ack.Error = ex.ToString();
                Logger.Error($"CityBankers capacity report failed: {ex}");
            }
            finally
            {
                CityDwellers.Shared.ManagerMemory.Current.FinishBankerReport(Client.CharacterName);
                if (manual) { WriteAtomicJson(_reportAckPath, ack); DeleteIfExists(_reportCommandPath); }
            }
        }

        private static float TryDistance(LocalPlayer localPlayer, Dynel dynel)
        {
            try
            {
                if (localPlayer == null || dynel == null || dynel.Transform == null)
                    return -1f;

                return localPlayer.DistanceFrom(dynel);
            }
            catch
            {
                return -1f;
            }
        }

        private static string FormatNullable(int? value)
        {
            return value.HasValue ? value.Value.ToString() : "n/a";
        }

        private void WriteAtomicJson(DiagnosticResult result)
        {
            WriteAtomicJson(_resultPath, result);
        }

        private static void WriteAtomicJson(string path, object value)
        {
            RuntimeStateStore.WriteJsonAtomic(path, value);
        }

        private static string SafeFileToken(string value) =>
            CityDwellers.Shared.CharacterNames.FileToken(value);

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        public class DiagnosticResult
        {
            public string BuildRevision { get; set; } = CityDwellers.Shared.BuildIdentity.Revision;
            public DateTime ObservedUtc;
            public string Character;
            public int PlayfieldModelId;
            public float X;
            public float Y;
            public float Z;
            public int DynelCount;
            public int StaticDynelCount;
            public int LivePlayerCount;
            public List<PlayerSnapshot> LivePlayers;
            public List<DynelSnapshot> BankCandidates;
            public List<DynelSnapshot> Dynels;
            public bool BankAttempted;
            public bool BankOpened;
            public bool BankOpenOnly;
            public string BankTargetName;
            public string BankTargetIdentity;
            public int? BankTargetTemplateId;
            public int BankItemCount;
            public int BankFreeSlots;
            public int InventoryItemCount;
            public int InventoryFreeSlots;
            public int InventoryBagCount;
            public int BankBagCount;
            public int TotalBagCount;
            public string BankError;
        }

        public class PlayerSnapshot
        {
            public string Name;
            public string Identity;
            public float Distance;
        }

        public class DynelSnapshot
        {
            public string Name;
            public string Identity;
            public float Distance;
            public string Source;
            public int? TemplateId;
        }

        public class ReportCommand
        {
            public string Recipient;
            public string Kind;
            public List<string> Messages;
        }

        public class ReportAck
        {
            public DateTime SentUtc;
            public string Recipient;
            public int MessageCount;
            public string Error;
        }
    }
}
