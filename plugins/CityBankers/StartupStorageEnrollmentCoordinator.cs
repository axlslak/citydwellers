using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityBankers.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// Repairs missing or live-rejected worker storage maps during normal startup.
    /// Readiness already holds trades and dispatch while this coordinator audits one
    /// worker at a time and merges only that worker into operational state.
    /// </summary>
    public sealed class StartupStorageEnrollmentCoordinator : ClientlessPluginEntry
    {
        public const string ActiveMarkerFileName =
            "citybankers-enrollment-active.json";
        private const int PollMilliseconds = 500;
        private const int StableStartupSeconds = 10;
        private const int AuditTimeoutMinutes = 30;

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
        private string _dataDir;
        private Dictionary<string, RoleConfig> _roles;
        private DateTime _startedUtc;
        private DateTime _nextPollUtc;
        private DateTime? _eligibleSinceUtc;
        private ActiveEnrollment _active;
        private bool _enabled;
        private bool _completeLogged;
        private bool _halted;

        public override void Init(string pluginDir)
        {
            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            EnrollmentConfig config = LoadConfig();
            if (config == null || config.Roles == null)
                return;

            _roles = new Dictionary<string, RoleConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (string role in new[] { "central" }.Concat(StorageRoles))
            {
                RoleConfig value = FindRole(config.Roles, role);
                if (value == null || string.IsNullOrWhiteSpace(value.Character))
                {
                    Logger.Warning(
                        "[CityBankers] STARTUP STORAGE ENROLLMENT disabled: role '" +
                        role + "' is not configured.");
                    return;
                }

                _roles[role] = value;
            }

            if (!string.Equals(
                    Client.CharacterName,
                    _roles["central"].Character,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _dataDir = RuntimeStateStore.GetDataDirectory(_settingsDir);
            _startedUtc = DateTime.UtcNow;
            _nextPollUtc = _startedUtc;

            ClearStaleEnrollmentHandoffs();

            _enabled = true;
            Client.OnUpdate += Tick;
            Logger.Information(
                "[CityBankers] STARTUP STORAGE ENROLLMENT armed on Central; " +
                "missing or live-rejected worker maps will be audited one at a time " +
                "while normal readiness remains closed.");
        }

        public override void Teardown()
        {
            if (_enabled)
                Client.OnUpdate -= Tick;
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!_enabled || _halted || !Client.InPlay ||
                DateTime.UtcNow < _nextPollUtc)
            {
                return;
            }

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);

            try
            {
                if (_active != null)
                {
                    ProcessActiveEnrollment();
                    return;
                }

                if (TrustedOperators.IsAllBankersReady())
                    return;

                if (!AllCurrentBanksVerified())
                {
                    _eligibleSinceUtc = null;
                    return;
                }

                if (!_eligibleSinceUtc.HasValue)
                {
                    _eligibleSinceUtc = DateTime.UtcNow;
                    return;
                }

                if ((DateTime.UtcNow - _eligibleSinceUtc.Value).TotalSeconds <
                    StableStartupSeconds)
                {
                    return;
                }

                EnrollmentTarget target = FindNextTarget();
                if (target == null)
                {
                    DeleteIfExists(GetActiveMarkerPath(_settingsDir));
                    if (!_completeLogged)
                    {
                        _completeLogged = true;
                        Logger.Information(
                            "[CityBankers] STARTUP STORAGE ENROLLMENT found no worker " +
                            "requiring audit; normal readiness reconciliation continues.");
                    }
                    return;
                }

                StartEnrollment(target);
            }
            catch (Exception ex)
            {
                Halt("coordinator failure: " + ex);
            }
        }

        private bool AllCurrentBanksVerified()
        {
            foreach (KeyValuePair<string, RoleConfig> pair in _roles)
            {
                string path = DiagnosticPath(pair.Value.Character);
                if (!File.Exists(path))
                    return false;

                JObject diagnostic;
                try
                {
                    diagnostic = JObject.Parse(File.ReadAllText(path));
                }
                catch
                {
                    return false;
                }

                DateTime observedUtc;
                if (!DateTime.TryParse(
                        diagnostic.GetValue("ObservedUtc", StringComparison.OrdinalIgnoreCase)
                            ?.ToString(),
                        out observedUtc) ||
                    observedUtc.ToUniversalTime() < _startedUtc.AddSeconds(-1) ||
                    !BoolValue(diagnostic, "BankOpened") ||
                    !string.Equals(
                        StringValue(diagnostic, "Character"),
                        pair.Value.Character,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private EnrollmentTarget FindNextTarget()
        {
            StorageState state = RuntimeStateStore.LoadStorageState(_settingsDir);
            foreach (string role in StorageRoles)
            {
                RoleConfig config = _roles[role];
                StorageWorkerState worker = FindWorker(state, role, config.Character);
                string requestPath = WorkerStorageLayoutRecoveryAgent.GetEnrollmentRequestPath(
                    _settingsDir,
                    config.Character);

                bool structurallyMissing = worker == null ||
                    worker.Bags == null || worker.Bags.Count == 0;
                bool liveRejected = IsCurrentRepairRequest(requestPath, role, config.Character);
                if (structurallyMissing || liveRejected)
                {
                    return new EnrollmentTarget
                    {
                        Role = role,
                        Character = config.Character,
                        Reason = structurallyMissing
                            ? "operational worker map is missing or empty"
                            : ReadRepairReason(requestPath)
                    };
                }
            }

            return null;
        }

        private void StartEnrollment(EnrollmentTarget target)
        {
            string token = SafeFileToken(target.Character);
            string runId =
                "startup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" +
                SafeFileToken(target.Role) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string commandPath = Path.Combine(
                _dataDir,
                "citybankers-enrollment-command-" + token + ".json");
            string resultPath = Path.Combine(
                _dataDir,
                "citybankers-enrollment-result-" + token + ".json");

            DeleteIfExists(commandPath);
            DeleteIfExists(resultPath);

            WriteAtomicJson(commandPath, new BagAuditAgent.BagAuditCommand
            {
                RunId = runId,
                Role = target.Role,
                BagOpenTimeoutMs = ServicePolicy.BagOpenTimeoutMs,
                BagMoveTimeoutMs = ServicePolicy.BagMoveTimeoutMs
            });

            _active = new ActiveEnrollment
            {
                RunId = runId,
                Role = target.Role,
                Character = target.Character,
                Reason = target.Reason,
                CommandPath = commandPath,
                ResultPath = resultPath,
                StartedUtc = DateTime.UtcNow
            };

            WriteAtomicJson(GetActiveMarkerPath(_settingsDir), new
            {
                Format = "citybankers-enrollment-active-v1",
                RunId = runId,
                Role = target.Role,
                Character = target.Character,
                StartedUtc = _active.StartedUtc
            });

            _completeLogged = false;
            Logger.Warning(
                "[CityBankers] STARTUP STORAGE ENROLLMENT audit started run=" + runId +
                " role=" + target.Role + " character=" + target.Character +
                " reason='" + target.Reason + "'.");
            TellKavem(
                "Storage self-check started for " + target.Character + " (" +
                target.Role + "): " + target.Reason + ". Readiness remains held.");
        }

        private void ProcessActiveEnrollment()
        {
            if (!File.Exists(_active.ResultPath))
            {
                if ((DateTime.UtcNow - _active.StartedUtc).TotalMinutes >=
                    AuditTimeoutMinutes)
                {
                    Halt(
                        "audit timed out for " + _active.Character + " after " +
                        AuditTimeoutMinutes + " minutes");
                }
                return;
            }

            BagAuditAgent.BagAuditResult result;
            try
            {
                result = JsonConvert.DeserializeObject<BagAuditAgent.BagAuditResult>(
                    File.ReadAllText(_active.ResultPath));
            }
            catch
            {
                return;
            }

            string validationError;
            if (!ValidateResult(result, _active, out validationError))
            {
                ArchiveResult(_active.ResultPath, _active.RunId, "failed");
                Halt(
                    "audit rejected for " + _active.Character + ": " + validationError);
                return;
            }

            StorageWorkerState replacement = BuildWorker(result, _active.RunId);
            MergeWorker(replacement, _active.RunId);
            ArchiveResult(_active.ResultPath, _active.RunId, "complete");
            DeleteIfExists(_active.ResultPath);
            DeleteIfExists(
                WorkerStorageLayoutRecoveryAgent.GetEnrollmentRequestPath(
                    _settingsDir,
                    _active.Character));

            Logger.Information(
                "[CityBankers] STARTUP STORAGE ENROLLMENT complete run=" +
                _active.RunId + " role=" + _active.Role + " character=" +
                _active.Character + " bags=" + result.TotalBagCount + ".");
            TellKavem(
                "Storage self-check completed for " + _active.Character + ": " +
                result.TotalBagCount + " bags audited and enrolled. Readiness will " +
                "continue after live layout and write-front verification.");

            _active = null;
            _eligibleSinceUtc = DateTime.UtcNow;
        }

        private void MergeWorker(StorageWorkerState replacement, string runId)
        {
            using (var mutex = new Mutex(
                false,
                WorkerStorageLayoutRecoveryAgent.LayoutMutexName))
            {
                bool entered = false;
                try
                {
                    try
                    {
                        entered = mutex.WaitOne(TimeSpan.FromSeconds(10));
                    }
                    catch (AbandonedMutexException)
                    {
                        entered = true;
                    }

                    if (!entered)
                        throw new IOException(
                            "Timed out waiting for live storage-layout reconciliation.");

                    RuntimeStateStore.MergeAuditedStorageWorker(
                        _settingsDir,
                        replacement,
                        runId,
                        "startup-enrollment:" + runId);
                }
                finally
                {
                    if (entered)
                        mutex.ReleaseMutex();
                }
            }
        }

        private static StorageWorkerState BuildWorker(
            BagAuditAgent.BagAuditResult result,
            string runId)
        {
            var worker = new StorageWorkerState
            {
                Role = result.Role,
                Character = result.Character,
                ObservedUtc = result.ObservedUtc,
                Bags = new List<StorageBagState>()
            };

            foreach (BagAuditAgent.BagAuditEntry source in result.Bags)
            {
                bool bank = string.Equals(
                    source.Source,
                    "bank",
                    StringComparison.OrdinalIgnoreCase);
                int capacity = source.ItemCount >= 0 && source.FreeSlots >= 0
                    ? source.ItemCount + source.FreeSlots
                    : 21;
                if (capacity <= 0)
                    capacity = 21;

                var bag = new StorageBagState
                {
                    Source = bank ? "bank" : "inventory",
                    OuterSlotType = bank
                        ? source.ReturnedOuterSlotType
                        : source.OuterSlotType,
                    OuterSlotInstance = bank
                        ? source.ReturnedOuterSlotInstance
                        : source.OuterSlotInstance,
                    LastUniqueIdentity = source.UniqueIdentity,
                    LastHandle = source.Handle,
                    Capacity = capacity,
                    Items = new List<StoredItemState>()
                };

                foreach (BagAuditAgent.BagInnerItem item in
                    source.Items ?? new List<BagAuditAgent.BagInnerItem>())
                {
                    bag.Items.Add(new StoredItemState
                    {
                        UniqueIdentity = item.UniqueIdentity,
                        AoId = item.LowId,
                        HighId = item.HighId,
                        Ql = item.Ql,
                        Name = item.Name ?? string.Empty,
                        InnerSlot = item.SlotInstance,
                        ObservedUtc = result.ObservedUtc,
                        TransactionId = "startup-enrollment:" + runId
                    });
                }

                worker.Bags.Add(bag);
            }

            worker.Bags = worker.Bags
                .OrderBy(bag => string.Equals(
                    bag.Source,
                    "bank",
                    StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(bag => bag.OuterSlotInstance)
                .ToList();
            return worker;
        }

        private static bool ValidateResult(
            BagAuditAgent.BagAuditResult result,
            ActiveEnrollment expected,
            out string error)
        {
            error = null;
            if (result == null)
            {
                error = "result is empty";
                return false;
            }
            if (!string.Equals(result.RunId, expected.RunId, StringComparison.Ordinal) ||
                !string.Equals(result.Role, expected.Role, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    result.Character,
                    expected.Character,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "run, role, or character identity mismatch";
                return false;
            }
            if (!result.BankOpened)
            {
                error = "bank was not verified open at completion";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(result.FatalError))
            {
                error = "worker reported fatal error: " + result.FatalError;
                return false;
            }
            if (result.TotalBagCount <= 0 || result.Bags == null ||
                result.Bags.Count != result.TotalBagCount ||
                result.OpenedCount != result.TotalBagCount || result.FailedCount != 0)
            {
                error = "bag inspection is incomplete";
                return false;
            }
            if (result.BankReturnedCount != result.BankBagCount ||
                result.BankReturnFailureCount != 0)
            {
                error = "not every staged bank bag was verified returned";
                return false;
            }
            if (result.Bags.Any(bag => bag == null || !bag.Opened ||
                (string.Equals(bag.Source, "bank", StringComparison.OrdinalIgnoreCase) &&
                 !bag.ReturnedToBank)))
            {
                error = "bag detail contains an unopened or unreturned entry";
                return false;
            }
            return true;
        }

        private bool IsCurrentRepairRequest(
            string path,
            string role,
            string character)
        {
            if (!File.Exists(path))
                return false;
            try
            {
                JObject request = JObject.Parse(File.ReadAllText(path));
                DateTime observed;
                return string.Equals(
                        StringValue(request, "Format"),
                        "citybankers-enrollment-request-v1",
                        StringComparison.Ordinal) &&
                    string.Equals(StringValue(request, "Role"), role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        StringValue(request, "Character"),
                        character,
                        StringComparison.OrdinalIgnoreCase) &&
                    DateTime.TryParse(StringValue(request, "ObservedUtc"), out observed) &&
                    observed.ToUniversalTime() >= _startedUtc.AddSeconds(-1);
            }
            catch
            {
                return false;
            }
        }

        private static string ReadRepairReason(string path)
        {
            try
            {
                return StringValue(JObject.Parse(File.ReadAllText(path)), "Problem") ??
                    "live layout rejected persisted worker map";
            }
            catch
            {
                return "live layout rejected persisted worker map";
            }
        }

        private void ArchiveResult(string sourcePath, string runId, string outcome)
        {
            string archiveDir = Path.Combine(_dataDir, "storage-enrollments");
            Directory.CreateDirectory(archiveDir);
            string archivePath = Path.Combine(
                archiveDir,
                "storage-enrollment-" + SafeFileToken(runId) + "-" + outcome + ".json");
            if (!File.Exists(archivePath))
                File.Copy(sourcePath, archivePath);
        }

        private void ClearStaleEnrollmentHandoffs()
        {
            DeleteIfExists(GetActiveMarkerPath(_settingsDir));
            foreach (string role in StorageRoles)
            {
                string token = SafeFileToken(_roles[role].Character);
                DeleteIfExists(Path.Combine(
                    _dataDir,
                    "citybankers-enrollment-command-" + token + ".json"));
                DeleteIfExists(Path.Combine(
                    _dataDir,
                    "citybankers-enrollment-result-" + token + ".json"));
            }
        }

        private void Halt(string error)
        {
            _halted = true;
            if (_active != null)
                DeleteIfExists(_active.CommandPath);
            Logger.Error("[CityBankers] STARTUP STORAGE ENROLLMENT HALTED: " + error);
            TellKavem(
                "Storage self-check halted: " + error +
                ". Readiness remains held; no additional bag will be touched this run.");
        }

        private EnrollmentConfig LoadConfig()
        {
            try
            {
                return SettingsPaths.ReadBankersSettings(_settingsDir)
                    .ToObject<EnrollmentConfig>();
            }
            catch
            {
                return null;
            }
        }

        private static RoleConfig FindRole(
            Dictionary<string, RoleConfig> roles,
            string expected)
        {
            foreach (KeyValuePair<string, RoleConfig> pair in roles)
            {
                if (string.Equals(pair.Key, expected, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
            return null;
        }

        private static StorageWorkerState FindWorker(
            StorageState state,
            string role,
            string character)
        {
            return (state?.Workers ?? new List<StorageWorkerState>())
                .FirstOrDefault(worker => worker != null &&
                    string.Equals(worker.Role, role, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        worker.Character,
                        character,
                        StringComparison.OrdinalIgnoreCase));
        }

        private string DiagnosticPath(string character)
        {
            return Path.Combine(
                _dataDir,
                "citybankers-diagnostic-" + SafeFileToken(character) + ".json");
        }

        public static bool IsEnrollmentHoldActive(string settingsDir)
        {
            return File.Exists(GetActiveMarkerPath(settingsDir));
        }

        private static string GetActiveMarkerPath(string settingsDir)
        {
            return Path.Combine(
                RuntimeStateStore.GetDataDirectory(settingsDir),
                ActiveMarkerFileName);
        }

        private void TellKavem(string message)
        {
            TellQueueClient.Enqueue(
                _settingsDir,
                Client.CharacterName,
                TrustedOperators.BootstrapAdmin,
                message);
        }

        private static string StringValue(JObject value, string property)
        {
            JToken token = value?.GetValue(property, StringComparison.OrdinalIgnoreCase);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static bool BoolValue(JObject value, string property)
        {
            bool result;
            return bool.TryParse(StringValue(value, property), out result) && result;
        }

        private static void WriteAtomicJson(string path, object value)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(value, Formatting.Indented));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (File.Exists(path + ".tmp"))
                    File.Delete(path + ".tmp");
            }
            catch
            {
            }
        }

        private static string SafeFileToken(string value)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                token = token.Replace(invalid, '_');
            return token.Replace('\\', '_').Replace('/', '_').Replace(':', '_');
        }

        private sealed class EnrollmentConfig
        {
            public Dictionary<string, RoleConfig> Roles;
        }

        private sealed class RoleConfig
        {
            public string Character;
        }

        private class EnrollmentTarget
        {
            public string Role;
            public string Character;
            public string Reason;
        }

        private sealed class ActiveEnrollment : EnrollmentTarget
        {
            public string RunId;
            public string CommandPath;
            public string ResultPath;
            public DateTime StartedUtc;
        }
    }
}
