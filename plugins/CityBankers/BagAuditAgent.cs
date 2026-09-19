using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{
    public class BagAuditAgent : ClientlessPluginEntry
    {
        private static BagAuditAgent _owner;

        internal static void CancelForRecovery()
        {
            if (_owner == null) return;
            _owner.FinishWithFatalError("Census superseded or client disconnected; cached handles retired.");
            if (_owner._startupCommandPath != null) DeleteIfExists(_owner._startupCommandPath);
        }

        private const int DefaultBagOpenTimeoutMs = 15000;
        private const int DefaultBagMoveTimeoutMs = 3000;
        private const int ProgressInterval = 25;

        private string _manualCommandPath;
        private string _startupCommandPath;
        private string _startupResultPath;
        private string _manualResultPath;
        private string _enrollmentCommandPath;
        private string _enrollmentResultPath;
        private string _activeCommandPath;
        private string _activeResultPath;
        private BagAuditCommand _command;
        private List<BagTarget> _bags;
        private List<BagAuditEntry> _entries;
        private int _index;
        private BagTarget _current;
        private AuditPhase _phase;
        private readonly Stopwatch _phaseAge = new Stopwatch();
        private int _phaseTimeoutMs;
        private readonly Stopwatch _openAge = new Stopwatch();
        private int _currentPreOpenHandle;
        private Container _preOpenContainer;
        private int _currentMoveToInventoryElapsedMs;
        private BagAuditEntry _pendingEntry;
        private string _currentStagedInventorySlot;
        private string _currentStagedInventorySlotType;
        private int _currentStagedInventorySlotInstance;
        private bool _active;

        // A move is verified by how the client's records change, not by their
        // absolute count. One physical bag can be listed twice when an earlier
        // move lost its removal, and an absolute "one here, none there" can then
        // never be satisfied even though the move itself succeeded.
        private int _preMoveBankOccurrences;
        private int _preMoveInventoryOccurrences;
        private readonly HashSet<int> _preMoveBankSlots = new HashSet<int>();
        private readonly HashSet<int> _preMoveInventorySlots = new HashSet<int>();

        private enum AuditPhase
        {
            None,
            MovingBankBagToInventory,
            Opening,
            ReturningBankBag
        }

        public override void Init(string pluginDir)
        {
            string settingsDir;
            string settingsError;
            if (!SettingsPaths.TryEnsureDirectory(out settingsDir, out settingsError))
                throw new InvalidOperationException(settingsError);

            pluginDir = RuntimeStateStore.GetDataDirectory(settingsDir);
            string token = SafeFileToken(Client.CharacterName);
            _startupCommandPath = Path.Combine(StartupCensusGate.CensusDirectory(settingsDir),
                token + ".command.json");
            _startupResultPath = Path.Combine(StartupCensusGate.CensusDirectory(settingsDir),
                token + ".result.json");
            _manualCommandPath = Path.Combine(
                pluginDir,
                $"citybankers-bagaudit-command-{token}.json");
            _manualResultPath = Path.Combine(
                pluginDir,
                $"citybankers-bagaudit-result-{token}.json");
            _enrollmentCommandPath = Path.Combine(
                pluginDir,
                $"citybankers-enrollment-command-{token}.json");
            _enrollmentResultPath = Path.Combine(
                pluginDir,
                $"citybankers-enrollment-result-{token}.json");

            _owner = this;
            Client.OnUpdate += Tick;
            Logger.Information(
                $"CityBankers bag-audit agent initialized; runtime state root='{pluginDir}'; " +
                "idle until a manual audit or startup enrollment command is present.");
        }

        public override void Teardown()
        {
            Client.OnUpdate -= Tick;
            _preOpenContainer = null;
            if (_owner == this) _owner = null;
            Logger.Information("CityBankers bag-audit agent teardown.");
        }

        private void Tick(object sender, double deltaTime)
        {
            try
            {
                if (!Client.InPlay) { CancelForRecovery(); return; }
                if (!_active)
                {
                    TryStart();
                    return;
                }

                ProcessCurrent();
            }
            catch (Exception ex)
            {
                Logger.Error($"BAG AUDIT fatal error on {Client.CharacterName}: {ex}");
                FinishWithFatalError(ex.ToString());
            }
        }

        private void TryStart()
        {
            if (!Client.InPlay || !ClientlessSessionGuard.BankCacheTrusted ||
                !Inventory.Bank.IsOpen)
            {
                return;
            }

            if (!ServicePolicy.IsBagAuditMode() && File.Exists(_startupCommandPath))
            {
                _activeCommandPath = _startupCommandPath;
                _activeResultPath = _startupResultPath;
            }
            else if (File.Exists(_manualCommandPath) && ServicePolicy.IsBagAuditMode())
            {
                _activeCommandPath = _manualCommandPath;
                _activeResultPath = _manualResultPath;
            }
            else if (File.Exists(_enrollmentCommandPath) && StartupCensusGate.IsOpen)
            {
                _activeCommandPath = _enrollmentCommandPath;
                _activeResultPath = _enrollmentResultPath;
            }
            else
            {
                return;
            }

            BagAuditCommand command;
            try
            {
                command = JsonConvert.DeserializeObject<BagAuditCommand>(
                    File.ReadAllText(_activeCommandPath));
            }
            catch (Exception ex)
            {
                Logger.Error($"BAG AUDIT could not read command '{_activeCommandPath}': {ex}");
                return;
            }

            if (command == null || string.IsNullOrWhiteSpace(command.RunId))
            {
                Logger.Error("BAG AUDIT command is empty or missing RunId.");
                DeleteIfExists(_activeCommandPath);
                return;
            }

            _command = command;
            _bags = new List<BagTarget>();
            _entries = new List<BagAuditEntry>();
            _index = 0;
            _current = null;
            _pendingEntry = null;
            _phase = AuditPhase.None;
            _active = true;

            DeleteIfExists(_activeResultPath);
            DeleteIfExists(_activeResultPath + ".tmp");

            // Mark the run active first so invalid topology produces a failed
            // result instead of repeatedly re-reading the command. Never move
            // a bag while the cache lists one identity in multiple places.
            _bags = EnumerateBags();

            Logger.Information(
                $"BAG AUDIT START run={_command.RunId} character={Client.CharacterName} " +
                $"bags={_bags.Count} bank={_bags.Count(b => b.Source == "bank")} " +
                $"inventory={_bags.Count(b => b.Source == "inventory")} " +
                "mode=staged-read.");
            Logger.Information(
                "BAG AUDIT opens inventory bags in place. Each bank bag is moved to one " +
                "free normal-inventory slot, opened/read there, and returned to bank before " +
                "the next bank bag. Bag contents are never moved, added, deleted, or renamed.");

            DeleteIfExists(_activeCommandPath);

            if (_bags.Count == 0)
                Finish(null);
        }

        private static List<BagTarget> EnumerateBags()
        {
            string layoutError;
            if (!StorageBagPolicy.TryValidatePhysicalLayout(out layoutError))
                throw new InvalidOperationException(layoutError + " Fresh bank evidence is required before an audit.");
            // Validation above requires unique container identities. Never choose
            // an arbitrary record when two slots claim the same identity.
            var result = StorageBagPolicy.DistinctBags()
                .Select(record => SnapshotTarget(record.Location, record.Bag))
                .ToList();

            return result
                .OrderBy(target => target.Source == "bank" ? 0 : 1)
                .ThenBy(target => target.OriginalOuterSlotInstance)
                .ThenBy(target => target.UniqueIdentity.Instance)
                .ToList();
        }

        private static BagTarget SnapshotTarget(string source, Item item)
        {
            return new BagTarget
            {
                Source = source,
                UniqueIdentity = item.UniqueIdentity,
                OriginalOuterSlot = item.Slot.ToString(),
                OriginalOuterSlotType = item.Slot.Type.ToString(),
                OriginalOuterSlotInstance = item.Slot.Instance,
                UniqueIdentityText = item.UniqueIdentity.ToString(),
                UniqueIdentityType = item.UniqueIdentity.Type.ToString(),
                UniqueIdentityInstance = item.UniqueIdentity.Instance,
                Name = item.Name ?? string.Empty,
                LowId = item.Id,
                HighId = item.HighId,
                Ql = item.Ql
            };
        }

        private static bool IsBag(Item item)
        {
            return item != null &&
                item.UniqueIdentity.Type == IdentityType.Container;
        }

        private void ProcessCurrent()
        {
            if (_bags == null || _entries == null)
            {
                FinishWithFatalError("Bag audit state was not initialized.");
                return;
            }

            if (_index >= _bags.Count)
            {
                Finish(null);
                return;
            }

            if (_current == null)
            {
                StartCurrent();
                return;
            }

            switch (_phase)
            {
                case AuditPhase.MovingBankBagToInventory:
                    ProcessMoveToInventory();
                    break;

                case AuditPhase.Opening:
                    ProcessOpen();
                    break;

                case AuditPhase.ReturningBankBag:
                    ProcessReturnToBank();
                    break;

                default:
                    FinishWithFatalError(
                        $"Bag audit entered invalid phase {_phase} for {_current.UniqueIdentityText}.");
                    break;
            }
        }

        private void StartCurrent()
        {
            string layoutError;
            if (!StorageBagPolicy.TryValidatePhysicalLayout(out layoutError))
            {
                FinishWithFatalError(layoutError + " Audit stopped before another bag move.");
                return;
            }
            _current = _bags[_index];
            _preOpenContainer = null;
            _pendingEntry = null;
            _currentPreOpenHandle = 0;
            _currentMoveToInventoryElapsedMs = 0;
            _currentStagedInventorySlot = string.Empty;
            _currentStagedInventorySlotType = string.Empty;
            _currentStagedInventorySlotInstance = -1;
            _phase = AuditPhase.None;

            if (string.Equals(_current.Source, "bank", StringComparison.Ordinal))
            {
                if (Inventory.NumFreeSlots <= 0)
                {
                    AbortCurrentBeforeOpen(
                        "No free normal-inventory slot is available to stage a bank bag.",
                        false);
                    return;
                }

                Item bankItem = FindBankItems(_current.UniqueIdentity).SingleOrDefault(item =>
                    item.Slot.Instance == _current.OriginalOuterSlotInstance);
                if (bankItem == null)
                {
                    AbortCurrentBeforeOpen(
                        $"Bank bag {_current.UniqueIdentityText} disappeared before staging.",
                        false);
                    return;
                }

                _phase = AuditPhase.MovingBankBagToInventory;
                _phaseAge.Restart();
                _phaseTimeoutMs = GetBagMoveTimeoutMs();
                CapturePreMoveOccurrences();

                try
                {
                    BagOriginTrace.MoveToInventory(bankItem);
                }
                catch (Exception ex)
                {
                    AbortCurrentBeforeOpen(
                        "MoveToInventory() failed: " + ex.Message,
                        true);
                }

                return;
            }

            Item inventoryItem = FindInventoryItems(_current.UniqueIdentity).SingleOrDefault(item =>
                item.Slot.Instance == _current.OriginalOuterSlotInstance);
            if (inventoryItem == null)
            {
                AbortCurrentBeforeOpen(
                    $"Inventory bag {_current.UniqueIdentityText} disappeared before opening.",
                    false);
                return;
            }

            _currentStagedInventorySlot = inventoryItem.Slot.ToString();
            _currentStagedInventorySlotType = inventoryItem.Slot.Type.ToString();
            _currentStagedInventorySlotInstance = inventoryItem.Slot.Instance;
            BeginOpen(inventoryItem);
        }

        private void ProcessMoveToInventory()
        {
            Item inventoryItem = ArrivedItem(_current.UniqueIdentity, false);
            if (inventoryItem != null &&
                BagOccurrenceCount(_current.UniqueIdentity, false) == _preMoveInventoryOccurrences + 1 &&
                BagOccurrenceCount(_current.UniqueIdentity, true) == _preMoveBankOccurrences - 1)
            {
                _currentMoveToInventoryElapsedMs = (int)_phaseAge.ElapsedMilliseconds;
                _currentStagedInventorySlot = inventoryItem.Slot.ToString();
                _currentStagedInventorySlotType = inventoryItem.Slot.Type.ToString();
                _currentStagedInventorySlotInstance = inventoryItem.Slot.Instance;
                BeginOpen(inventoryItem);
                return;
            }

            if (_phaseAge.ElapsedMilliseconds < _phaseTimeoutMs)
                return;

            Item stillInBank = FindBankItem(_current.UniqueIdentity);
            string locationNote = stillInBank != null
                ? "The bag is still visible in bank."
                : "The bag is not visible in bank or normal inventory; physical location is uncertain.";

            AbortCurrentBeforeOpen(
                $"Bank bag {_current.UniqueIdentityText} did not arrive in normal inventory " +
                $"within {GetBagMoveTimeoutMs()} ms. {locationNote}",
                true);
        }

        private void BeginOpen(Item item)
        {
            Container before = FindContainer(_current.UniqueIdentity);
            _preOpenContainer = before;
            _currentPreOpenHandle = before != null ? before.Handle : 0;
            _openAge.Restart();
            _phaseAge.Restart();
            _phase = AuditPhase.Opening;
            _phaseTimeoutMs = GetBagOpenTimeoutMs();

            try
            {
                item.Use();
            }
            catch (Exception ex)
            {
                FinishOpen(before, "Use() failed: " + ex.Message);
            }
        }

        private void ProcessOpen()
        {
            Container container = FindContainer(_current.UniqueIdentity);
            if (container != null && container.IsOpen && !ReferenceEquals(container, _preOpenContainer))
            {
                FinishOpen(container, null);
                return;
            }

            if (_phaseAge.ElapsedMilliseconds >= _phaseTimeoutMs)
            {
                FinishOpen(
                    container,
                    $"No open container response within {GetBagOpenTimeoutMs()} ms.");
            }
        }

        private void FinishOpen(Container container, string error)
        {
            if (error != null)
                Logger.Warning("[CityBankers] BAG AUDIT OPEN FAILED character=" + Client.CharacterName +
                    "; run=" + _command.RunId + "; bag=" + _current.UniqueIdentityText +
                    "; source=" + _current.Source + "/" + _current.OriginalOuterSlotInstance +
                    "; attemptedSlot=" + _currentStagedInventorySlot +
                    "; elapsedMs=" + _openAge.ElapsedMilliseconds +
                    "; beforeHandle=" + _currentPreOpenHandle +
                    "; currentHandle=" + (container == null ? 0 : container.Handle) +
                    "; freshObject=" + (container != null && !ReferenceEquals(container, _preOpenContainer)) +
                    "; error=" + error);
            BagAuditEntry entry = SnapshotCurrentEntry(container, error);

            if (!string.Equals(_current.Source, "bank", StringComparison.Ordinal))
            {
                CommitCurrentEntry(entry);
                return;
            }

            BeginReturnToBank(entry);
        }

        private void BeginReturnToBank(BagAuditEntry entry)
        {
            // Return the slot actually opened, never another record of this identity.
            Item inventoryItem = FindInventoryItems(_current.UniqueIdentity).SingleOrDefault(item =>
                item.Slot.Instance == _currentStagedInventorySlotInstance);
            if (inventoryItem == null)
            {
                entry.Error = AppendError(
                    entry.Error,
                    "Cannot return staged bank bag because it is no longer visible in normal inventory.");
                entry.ReturnToBankAttempted = false;
                _entries.Add(entry);
                _index++;
                _current = null;
                _phase = AuditPhase.None;
                FinishWithFatalError(entry.Error);
                return;
            }

            entry.ReturnToBankAttempted = true;
            _pendingEntry = entry;
            _phase = AuditPhase.ReturningBankBag;
            _phaseAge.Restart();
            _phaseTimeoutMs = GetBagMoveTimeoutMs();
            CapturePreMoveOccurrences();

            try
            {
                BagOriginTrace.MoveToBank(inventoryItem);
            }
            catch (Exception ex)
            {
                entry.Error = AppendError(
                    entry.Error,
                    "MoveToBank() failed: " + ex.Message);
                entry.ReturnToBankElapsedMs = (int)_phaseAge.ElapsedMilliseconds;
                _entries.Add(entry);
                _index++;
                _pendingEntry = null;
                _preOpenContainer = null;
                _current = null;
                _phase = AuditPhase.None;
                FinishWithFatalError(entry.Error);
            }
        }

        private void ProcessReturnToBank()
        {
            Item bankItem = ArrivedItem(_current.UniqueIdentity, true);
            if (bankItem != null &&
                BagOccurrenceCount(_current.UniqueIdentity, true) == _preMoveBankOccurrences + 1 &&
                BagOccurrenceCount(_current.UniqueIdentity, false) == _preMoveInventoryOccurrences - 1)
            {
                _pendingEntry.ReturnedToBank = true;
                _pendingEntry.ReturnedOuterSlot = bankItem.Slot.ToString();
                _pendingEntry.ReturnedOuterSlotType = bankItem.Slot.Type.ToString();
                _pendingEntry.ReturnedOuterSlotInstance = bankItem.Slot.Instance;
                _pendingEntry.ReturnedToOriginalOuterSlot =
                    string.Equals(
                        _current.OriginalOuterSlotType,
                        bankItem.Slot.Type.ToString(),
                        StringComparison.Ordinal) &&
                    _current.OriginalOuterSlotInstance == bankItem.Slot.Instance;
                _pendingEntry.ReturnToBankElapsedMs = (int)_phaseAge.ElapsedMilliseconds;

                CommitCurrentEntry(_pendingEntry);
                return;
            }

            if (_phaseAge.ElapsedMilliseconds < _phaseTimeoutMs)
                return;

            _pendingEntry.ReturnedToBank = false;
            _pendingEntry.ReturnToBankElapsedMs = (int)_phaseAge.ElapsedMilliseconds;
            _pendingEntry.Error = AppendError(
                _pendingEntry.Error,
                $"Staged bank bag {_current.UniqueIdentityText} did not return to bank within " +
                $"{GetBagMoveTimeoutMs()} ms. Audit stopped before touching another bag.");

            string fatal = _pendingEntry.Error;
            _entries.Add(_pendingEntry);
            _index++;
            _pendingEntry = null;
            _current = null;
            _phase = AuditPhase.None;
            FinishWithFatalError(fatal);
        }

        private BagAuditEntry SnapshotCurrentEntry(Container container, string error)
        {
            bool opened = error == null && container != null && container.IsOpen &&
                !ReferenceEquals(container, _preOpenContainer);
            return new BagAuditEntry
            {
                Ordinal = _index,
                Source = _current.Source,
                OuterSlot = _current.OriginalOuterSlot,
                OuterSlotType = _current.OriginalOuterSlotType,
                OuterSlotInstance = _current.OriginalOuterSlotInstance,
                UniqueIdentity = _current.UniqueIdentityText,
                UniqueIdentityType = _current.UniqueIdentityType,
                UniqueIdentityInstance = _current.UniqueIdentityInstance,
                Name = _current.Name,
                LowId = _current.LowId,
                HighId = _current.HighId,
                Ql = _current.Ql,
                MoveToInventoryAttempted =
                    string.Equals(_current.Source, "bank", StringComparison.Ordinal),
                MoveToInventoryCompleted =
                    string.Equals(_current.Source, "bank", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(_currentStagedInventorySlot),
                StagedInventorySlot = _currentStagedInventorySlot,
                StagedInventorySlotType = _currentStagedInventorySlotType,
                StagedInventorySlotInstance = _currentStagedInventorySlotInstance,
                MoveToInventoryElapsedMs = _currentMoveToInventoryElapsedMs,
                PreOpenHandle = _currentPreOpenHandle,
                Opened = opened,
                Handle = container != null ? container.Handle : 0,
                ContainerIdentity = container != null
                    ? container.Identity.ToString()
                    : string.Empty,
                ItemCount = opened && container.Items != null
                    ? container.Items.Count
                    : -1,
                FreeSlots = opened ? container.NumFreeSlots : -1,
                OpenElapsedMs = Math.Max(
                    0,
                    (int)_openAge.ElapsedMilliseconds),
                Error = error,
                Items = opened && container.Items != null
                    ? container.Items
                        .Where(item => item != null)
                        .OrderBy(item => item.Slot.Instance)
                        .Select(SnapshotInnerItem)
                        .ToList()
                    : new List<BagInnerItem>(),
                ReturnedOuterSlotInstance = -1
            };
        }

        private void AbortCurrentBeforeOpen(string error, bool moveAttempted)
        {
            var entry = new BagAuditEntry
            {
                Ordinal = _index,
                Source = _current.Source,
                OuterSlot = _current.OriginalOuterSlot,
                OuterSlotType = _current.OriginalOuterSlotType,
                OuterSlotInstance = _current.OriginalOuterSlotInstance,
                UniqueIdentity = _current.UniqueIdentityText,
                UniqueIdentityType = _current.UniqueIdentityType,
                UniqueIdentityInstance = _current.UniqueIdentityInstance,
                Name = _current.Name,
                LowId = _current.LowId,
                HighId = _current.HighId,
                Ql = _current.Ql,
                MoveToInventoryAttempted = moveAttempted,
                MoveToInventoryCompleted = false,
                StagedInventorySlot = string.Empty,
                StagedInventorySlotType = string.Empty,
                StagedInventorySlotInstance = -1,
                MoveToInventoryElapsedMs = moveAttempted
                    ? (int)_phaseAge.ElapsedMilliseconds
                    : 0,
                PreOpenHandle = 0,
                Opened = false,
                Handle = 0,
                ContainerIdentity = string.Empty,
                ItemCount = -1,
                FreeSlots = -1,
                OpenElapsedMs = 0,
                Error = error,
                Items = new List<BagInnerItem>(),
                ReturnedOuterSlotInstance = -1
            };

            _entries.Add(entry);
            _index++;
            _current = null;
            _phase = AuditPhase.None;
            FinishWithFatalError(error);
        }

        private void CommitCurrentEntry(BagAuditEntry entry)
        {
            _entries.Add(entry);
            _index++;
            _pendingEntry = null;
            _current = null;
            _phase = AuditPhase.None;

            if (_index % ProgressInterval == 0 || _index == _bags.Count)
            {
                int openedCount = _entries.Count(e => e.Opened);
                int failedCount = _entries.Count(IsFailedEntry);
                int returnedCount = _entries.Count(e =>
                    string.Equals(e.Source, "bank", StringComparison.Ordinal) &&
                    e.ReturnedToBank);
                Logger.Information(
                    $"BAG AUDIT PROGRESS character={Client.CharacterName} " +
                    $"processed={_index}/{_bags.Count} opened={openedCount} " +
                    $"failed={failedCount} bankReturned={returnedCount}.");
            }
        }

        private int GetBagOpenTimeoutMs()
        {
            return _command != null && _command.BagOpenTimeoutMs > 0
                ? _command.BagOpenTimeoutMs
                : DefaultBagOpenTimeoutMs;
        }

        private int GetBagMoveTimeoutMs()
        {
            return _command != null && _command.BagMoveTimeoutMs > 0
                ? _command.BagMoveTimeoutMs
                : DefaultBagMoveTimeoutMs;
        }

        // Record the client's view of this bag immediately before a move is sent,
        // so the move is judged by what changed rather than by an absolute count
        // a stale second record would make unreachable.
        private void CapturePreMoveOccurrences()
        {
            _preMoveBankOccurrences = BagOccurrenceCount(_current.UniqueIdentity, true);
            _preMoveInventoryOccurrences = BagOccurrenceCount(_current.UniqueIdentity, false);
            _preMoveBankSlots.Clear();
            _preMoveInventorySlots.Clear();
            foreach (int slot in OccurrenceSlots(_current.UniqueIdentity, true)) _preMoveBankSlots.Add(slot);
            foreach (int slot in OccurrenceSlots(_current.UniqueIdentity, false)) _preMoveInventorySlots.Add(slot);
        }

        // The record at a slot the bag did not occupy before the move is the one
        // the move produced. Preferring it keeps a lingering record from being
        // reported as the bag's new location.
        private Item ArrivedItem(Identity identity, bool bank)
        {
            var before = bank ? _preMoveBankSlots : _preMoveInventorySlots;
            var candidates = (bank ? FindBankItems(identity) : FindInventoryItems(identity)).ToList();
            var arrivals = candidates.Where(item => !before.Contains(item.Slot.Instance)).ToList();
            return arrivals.Count == 1 ? arrivals[0] : null;
        }

        private static IEnumerable<int> OccurrenceSlots(Identity identity, bool bank)
        {
            return (bank ? FindBankItems(identity) : FindInventoryItems(identity))
                .Select(item => item.Slot.Instance);
        }

        private static IEnumerable<Item> FindBankItems(Identity identity)
        {
            return Inventory.Bank.Items == null
                ? Enumerable.Empty<Item>()
                : Inventory.Bank.Items.Where(item =>
                    StorageBagPolicy.IsStorageBag(item) && item.UniqueIdentity == identity);
        }

        private static IEnumerable<Item> FindInventoryItems(Identity identity)
        {
            return Inventory.Items == null
                ? Enumerable.Empty<Item>()
                : Inventory.Items.Where(item => item != null &&
                    StorageBagPolicy.IsNormalInventory(item) && StorageBagPolicy.IsStorageBag(item) &&
                    item.UniqueIdentity == identity);
        }

        private static int BagOccurrenceCount(Identity identity, bool bank)
        {
            // Counted from the same enumerations the slot helpers use, so a move
            // cannot be judged against one view and located against another.
            return (bank ? FindBankItems(identity) : FindInventoryItems(identity)).Count();
        }

        private static Item FindBankItem(Identity identity)
        {
            return FindBankItems(identity).FirstOrDefault();
        }

        private static Container FindContainer(Identity identity)
        {
            return Inventory.Containers != null
                ? Inventory.Containers.FirstOrDefault(c =>
                    StorageBagPolicy.IsStorageContainer(c) && c.Identity == identity)
                : null;
        }

        private static BagInnerItem SnapshotInnerItem(Item item)
        {
            return new BagInnerItem
            {
                Slot = item.Slot.ToString(),
                SlotType = item.Slot.Type.ToString(),
                SlotInstance = item.Slot.Instance,
                UniqueIdentity = item.UniqueIdentity.ToString(),
                Name = item.Name ?? string.Empty,
                LowId = item.Id,
                HighId = item.HighId,
                Ql = item.Ql
            };
        }

        private static string AppendError(string current, string additional)
        {
            if (string.IsNullOrWhiteSpace(current))
                return additional;
            if (string.IsNullOrWhiteSpace(additional))
                return current;
            return current + " | " + additional;
        }

        private static bool IsFailedEntry(BagAuditEntry entry)
        {
            if (entry == null || !entry.Opened || entry.Error != null)
                return true;

            return string.Equals(entry.Source, "bank", StringComparison.Ordinal) &&
                !entry.ReturnedToBank;
        }

        private void FinishWithFatalError(string error)
        {
            if (!_active)
                return;

            Logger.Error("[CityBankers] BAG AUDIT FAILED run=" + (_command?.RunId ?? "-") +
                " character=" + Client.CharacterName + "; " + error);
            Finish(error);
        }

        private void Finish(string fatalError)
        {
            try
            {
                var result = new BagAuditResult
                {
                    RunId = _command != null ? _command.RunId : string.Empty,
                    Role = _command != null ? _command.Role : string.Empty,
                    Character = Client.CharacterName,
                    ObservedUtc = DateTime.UtcNow,
                    PlayfieldModelId = (int)Playfield.ModelId,
                    BankOpened = Inventory.Bank.IsOpen,
                    BankOuterItemCount = Inventory.Bank.Items != null
                        ? Inventory.Bank.Items.Count
                        : -1,
                    BankBagCount = _bags != null
                        ? _bags.Count(b => b.Source == "bank")
                        : 0,
                    InventoryBagCount = _bags != null
                        ? _bags.Count(b => b.Source == "inventory")
                        : 0,
                    TotalBagCount = _bags != null ? _bags.Count : 0,
                    OpenedCount = _entries != null
                        ? _entries.Count(e => e.Opened)
                        : 0,
                    FailedCount = _entries != null
                        ? _entries.Count(IsFailedEntry)
                        : 0,
                    EmptyCount = _entries != null
                        ? _entries.Count(e => e.Opened && e.ItemCount == 0)
                        : 0,
                    NonEmptyCount = _entries != null
                        ? _entries.Count(e => e.Opened && e.ItemCount > 0)
                        : 0,
                    BankStagedCount = _entries != null
                        ? _entries.Count(e =>
                            string.Equals(e.Source, "bank", StringComparison.Ordinal) &&
                            e.MoveToInventoryCompleted)
                        : 0,
                    BankReturnedCount = _entries != null
                        ? _entries.Count(e =>
                            string.Equals(e.Source, "bank", StringComparison.Ordinal) &&
                            e.ReturnedToBank)
                        : 0,
                    BankReturnFailureCount = _entries != null
                        ? _entries.Count(e =>
                            string.Equals(e.Source, "bank", StringComparison.Ordinal) &&
                            e.ReturnToBankAttempted &&
                            !e.ReturnedToBank)
                        : 0,
                    FatalError = fatalError,
                    // Bag contents alone are not a custody census. Preserve loose items
                    // separately, including Central's ordinary trade inventory.
                    LooseInventoryItems = Inventory.Items == null ? null : Inventory.Items
                        .Where(item => StorageBagPolicy.IsNormalInventory(item) && !IsBag(item))
                        .Select(SnapshotInnerItem).ToList(),
                    LooseBankItems = Inventory.Bank.Items == null ? null : Inventory.Bank.Items
                        .Where(item => item != null && !IsBag(item))
                        .Select(SnapshotInnerItem).ToList(),
                    Bags = _entries ?? new List<BagAuditEntry>()
                };

                WriteAtomicJson(_activeResultPath, result);

                bool complete = result.BankOpened && result.FatalError == null && result.FailedCount == 0 &&
                    result.Bags.Count == result.TotalBagCount && result.OpenedCount == result.TotalBagCount &&
                    result.BankReturnedCount == result.BankBagCount;
                string summary =
                    $"BAG AUDIT {(complete ? "COMPLETE" : "INCOMPLETE")} run={result.RunId} character={result.Character} " +
                    $"bags={result.TotalBagCount} opened={result.OpenedCount} " +
                    $"failed={result.FailedCount} empty={result.EmptyCount} " +
                    $"nonempty={result.NonEmptyCount} staged={result.BankStagedCount} " +
                    $"returned={result.BankReturnedCount} result='{_activeResultPath}'.";
                if (complete) Logger.Information(summary);
                else Logger.Warning(summary);
            }
            catch (Exception ex)
            {
                Logger.Error($"BAG AUDIT result write failed: {ex}");
            }
            finally
            {
                _active = false;
                _command = null;
                _preOpenContainer = null;
                _bags = null;
                _entries = null;
                _current = null;
                _pendingEntry = null;
                _phase = AuditPhase.None;
                _index = 0;
                DeleteIfExists(_activeCommandPath);
                _activeCommandPath = null;
                _activeResultPath = null;
            }
        }

        private static void WriteAtomicJson(string path, object value)
        {
            string temp = path + ".tmp";
            File.WriteAllText(
                temp,
                JsonConvert.SerializeObject(value, Formatting.Indented));

            if (File.Exists(path))
                File.Delete(path);

            File.Move(temp, path);
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

        private class BagTarget
        {
            public string Source;
            public Identity UniqueIdentity;
            public string OriginalOuterSlot;
            public string OriginalOuterSlotType;
            public int OriginalOuterSlotInstance;
            public string UniqueIdentityText;
            public string UniqueIdentityType;
            public int UniqueIdentityInstance;
            public string Name;
            public int LowId;
            public int HighId;
            public int Ql;
        }

        public class BagAuditCommand
        {
            public string RunId;
            public string Role;
            public int BagOpenTimeoutMs = DefaultBagOpenTimeoutMs;
            public int BagMoveTimeoutMs = DefaultBagMoveTimeoutMs;
        }

        public class BagAuditResult
        {
            public List<BagInnerItem> LooseInventoryItems;
            public List<BagInnerItem> LooseBankItems;
            public string RunId;
            public string Role;
            public string Character;
            public DateTime ObservedUtc;
            public int PlayfieldModelId;
            public bool BankOpened;
            public int BankOuterItemCount;
            public int BankBagCount;
            public int InventoryBagCount;
            public int TotalBagCount;
            public int OpenedCount;
            public int FailedCount;
            public int EmptyCount;
            public int NonEmptyCount;
            public int BankStagedCount;
            public int BankReturnedCount;
            public int BankReturnFailureCount;
            public string FatalError;
            public List<BagAuditEntry> Bags;
        }

        public class BagAuditEntry
        {
            public int Ordinal;
            public string Source;
            public string OuterSlot;
            public string OuterSlotType;
            public int OuterSlotInstance;
            public string UniqueIdentity;
            public string UniqueIdentityType;
            public int UniqueIdentityInstance;
            public string Name;
            public int LowId;
            public int HighId;
            public int Ql;
            public bool MoveToInventoryAttempted;
            public bool MoveToInventoryCompleted;
            public string StagedInventorySlot;
            public string StagedInventorySlotType;
            public int StagedInventorySlotInstance;
            public int MoveToInventoryElapsedMs;
            public int PreOpenHandle;
            public bool Opened;
            public int Handle;
            public string ContainerIdentity;
            public int ItemCount;
            public int FreeSlots;
            public int OpenElapsedMs;
            public bool ReturnToBankAttempted;
            public bool ReturnedToBank;
            public string ReturnedOuterSlot;
            public string ReturnedOuterSlotType;
            public int ReturnedOuterSlotInstance;
            public bool ReturnedToOriginalOuterSlot;
            public int ReturnToBankElapsedMs;
            public string Error;
            public List<BagInnerItem> Items;
        }

        public class BagInnerItem
        {
            public string Slot;
            public string SlotType;
            public int SlotInstance;
            public string UniqueIdentity;
            public string Name;
            public int LowId;
            public int HighId;
            public int Ql;
        }
    }
}
