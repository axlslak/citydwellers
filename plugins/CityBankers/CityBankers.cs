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
        private static readonly TimeSpan DiagnosticSettleDelay =
            TimeSpan.FromSeconds(5);
        private static readonly TimeSpan BankOpenTimeout =
            TimeSpan.FromSeconds(8);

        // The city office building exposes this real terminal to the game UI,
        // but AOSharp currently omits it from DynelManager.AllDynels.
        private const int CityOfficePlayfieldModelId = 6152312;
        private const int CityOfficeBankTerminalInstance = 1478048485;
        private const float CityOfficeBankX = 185f;
        private const float CityOfficeBankY = 6.02f;
        private const float CityOfficeBankZ = 173f;
        private const float CityOfficeBankMaxDistance = 8f;

        private string _resultPath;
        private string _tempPath;
        private string _reportCommandPath;
        private string _reportAckPath;
        private string _healthPath;
        private string _dataDir;
        private bool _inPlay;
        private bool _diagnosticStarted;
        private bool _snapshotWritten;
        private DateTime _snapshotDueUtc = DateTime.MaxValue;
        private DateTime _bankDeadlineUtc = DateTime.MaxValue;
        private DateTime _nextHealthUtc = DateTime.MinValue;
        private DiagnosticResult _pendingResult;

        public override void Init(string pluginDir)
        {
            StackableItems.Install();
            CityDwellers.Shared.BuildIdentity.Register();
            Logger.Information("BUILD " + CityDwellers.Shared.BuildIdentity.Label +
                " | revision=" + CityDwellers.Shared.BuildIdentity.Revision);
            string settingsDir;
            string settingsError;
            if (!SettingsPaths.TryEnsureDirectory(out settingsDir, out settingsError))
                throw new InvalidOperationException(settingsError);

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
            _healthPath = Path.Combine(pluginDir, $"citybankers-health-{token}.json");

            DeleteIfExists(_resultPath);
            DeleteIfExists(_tempPath);
            DeleteIfExists(_reportCommandPath);
            DeleteIfExists(_reportCommandPath + ".tmp");
            DeleteIfExists(_reportAckPath);
            DeleteIfExists(_reportAckPath + ".tmp");
            DeleteIfExists(_healthPath);
            DeleteIfExists(_healthPath + ".tmp");

            Logger.Information(
                $"CityBankers diagnostic plugin initialized. Runtime state root='{pluginDir}'.");

            Client.Config.AutoReconnect = true;
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
            Client.MessageReceived -= MessageReceived;
            Client.OnUpdate -= Tick;
            Client.Disconnected -= Disconnected;
            DeleteIfExists(_healthPath);
            DeleteIfExists(_healthPath + ".tmp");
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
                    CorrectRemoteOfferRemoval((TradeMessage)e.Body);
                    return;
                }
                if (n3Message.N3MessageType != N3MessageType.CharInPlay)
                    return;

                var charInPlay = (CharInPlayMessage)e.Body;
                if (charInPlay.Identity.Instance != Client.LocalDynelId)
                    return;

                _inPlay = true;
                _diagnosticStarted = false;
                _snapshotWritten = false;
                _pendingResult = null;
                _snapshotDueUtc = DateTime.UtcNow.Add(DiagnosticSettleDelay);
                _bankDeadlineUtc = DateTime.MaxValue;

                Logger.Information(
                    $"CityBankers ready: {Client.CharacterName} reached InPlay. " +
                    $"Waiting {DiagnosticSettleDelay.TotalSeconds:F0}s for peers/static dynels to settle.");
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

        private void Tick(object sender, double deltaTime)
        {
            _inPlay = Client.InPlay;

            if (!_inPlay)
                return;

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
                CompleteDiagnostic(null);
                return;
            }

            if (DateTime.UtcNow >= _bankDeadlineUtc)
            {
                CompleteDiagnostic(
                    $"Bank did not report open within {BankOpenTimeout.TotalSeconds:F0}s after Use().");
            }
        }

        private void Disconnected()
        {
            _inPlay = false;
            _diagnosticStarted = false;
            _snapshotWritten = false;
            _pendingResult = null;
            _snapshotDueUtc = DateTime.MaxValue;
            _bankDeadlineUtc = DateTime.MaxValue;
            DeleteIfExists(_healthPath);
            DeleteIfExists(_healthPath + ".tmp");
            Logger.Warning(
                $"CityBankers observed {Client.CharacterName} disconnect; " +
                "AutoReconnect remains enabled.");
        }

        private void PublishHealthHeartbeat()
        {
            if (DateTime.UtcNow < _nextHealthUtc || string.IsNullOrWhiteSpace(_healthPath))
                return;

            _nextHealthUtc = DateTime.UtcNow.AddSeconds(5);
            WriteAtomicJson(_healthPath, new BankerHealthHeartbeat
            {
                ProcessId = Process.GetCurrentProcess().Id,
                ObservedUtc = DateTime.UtcNow,
                Character = Client.CharacterName,
                InPlay = Client.InPlay,
                BankOpen = Inventory.Bank != null && Inventory.Bank.IsOpen,
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
                        IsContainer = item.UniqueIdentity.Type == IdentityType.Container
                    })
                    .ToList()
            });
        }

        private void BeginDiagnostic()
        {
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
                CityOfficeBankTerminalInstance);

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

        private void CompleteDiagnostic(string error)
        {
            if (_snapshotWritten)
                return;

            if (_pendingResult == null)
                _pendingResult = NewFallbackResult();

            PopulateInventoryAndBagCounts(_pendingResult);

            _pendingResult.BankOpened = Inventory.Bank.IsOpen;

            if (Inventory.Bank.IsOpen)
            {
                _pendingResult.BankItemCount = Inventory.Bank.Items.Count;
                _pendingResult.BankFreeSlots = Inventory.Bank.NumFreeSlots;
                _pendingResult.BankBagCount = Inventory.Bank.Items.Count(
                    item => item != null &&
                        item.UniqueIdentity.Type == IdentityType.Container);
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
                    item.UniqueIdentity.Type == IdentityType.Container);
        }

        private void ProcessReportCommand()
        {
            if (string.IsNullOrWhiteSpace(_reportCommandPath) ||
                !File.Exists(_reportCommandPath))
            {
                return;
            }

            ReportAck ack = new ReportAck
            {
                SentUtc = DateTime.UtcNow,
                Recipient = string.Empty,
                MessageCount = 0
            };

            try
            {
                ReportCommand command = JsonConvert.DeserializeObject<ReportCommand>(
                    File.ReadAllText(_reportCommandPath));

                if (command == null)
                    throw new InvalidOperationException("Report command is empty.");

                if (string.IsNullOrWhiteSpace(command.Recipient))
                    throw new InvalidOperationException("Report recipient is empty.");

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
                WriteAtomicJson(_reportAckPath, ack);
                DeleteIfExists(_reportCommandPath);
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
            string tempPath = path + ".tmp";
            string json = JsonConvert.SerializeObject(value, Formatting.Indented);
            File.WriteAllText(tempPath, json);

            if (File.Exists(path))
                File.Delete(path);

            File.Move(tempPath, path);
        }

        private static string SafeFileToken(string value)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                token = token.Replace(invalid, '_');

            return token;
        }

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

        public class BankerHealthHeartbeat
        {
            public int ProcessId;
            public DateTime ObservedUtc;
            public string Character;
            public bool InPlay;
            public bool BankOpen;
            public int InventoryFreeSlots;
            public List<InventoryItemSnapshot> InventoryItems;
        }

        public class InventoryItemSnapshot
        {
            public int Slot;
            public string UniqueIdentity;
            public int AoId;
            public int HighId;
            public int Ql;
            public string Name;
            public bool IsContainer;
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
