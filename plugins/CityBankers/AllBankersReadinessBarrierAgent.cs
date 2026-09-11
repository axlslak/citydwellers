using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityBankers.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// Holds all Central dispatch work while Banker.exe all is still assembling the
    /// configured clientless bankers. Readiness is current-run physical evidence: every
    /// configured canonical banker must publish a fresh diagnostic, every storage worker
    /// must reconcile its persisted bag identities to the current live AO outer slots,
    /// and every worker must prove that the next bag it would actually write to has live,
    /// safely reconciled free capacity. That complete set must remain present briefly
    /// before the queue is released.
    ///
    /// This does not move donation items. It only controls when the already-proven
    /// BankingService and recovery machinery are allowed to run.
    /// </summary>
    public class AllBankersReadinessBarrierAgent : ClientlessPluginEntry
    {
        private const string StartupQueuedStatus = "startup-hold-queued";
        private const string StartupRecoverableFailedStatus = "startup-hold-recoverable-failed";
        private const int PollMilliseconds = 250;
        private const int ReadySettleMilliseconds = 1500;

        private static readonly string[] RequiredRoles =
        {
            "central",
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
        private bool _enabled;
        private DateTime _startedUtc;
        private DateTime _nextPollUtc;
        private DateTime? _allDiagnosticsSeenUtc;
        private Dictionary<string, string> _expectedCharacters;

        public override void Init(string pluginDir)
        {
            if (!ServicePolicy.IsBagAuditMode() && StartupCensusGate.Defer(() => Init(pluginDir)))
                return;

            // A census must not run accounting, handshake, or recovery writers.
            if (ServicePolicy.IsBagAuditMode())
                return;

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            string centralCharacter;
            if (!TryLoadExpectedCharacters(out _expectedCharacters) ||
                !_expectedCharacters.TryGetValue("central", out centralCharacter) ||
                !string.Equals(
                    centralCharacter,
                    Client.CharacterName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _enabled = true;
            _startedUtc = DateTime.UtcNow;
            _nextPollUtc = DateTime.UtcNow;

            ClearReadyMarker();
            HoldDispatchQueueForStartup();
            Client.OnUpdate += Tick;

            Logger.Information(
                "[CityBankers] ALL-BANKERS READINESS barrier armed on Central; " +
                "dispatch/recovery held until all " + RequiredRoles.Length +
                " fresh diagnostics, all " + (RequiredRoles.Length - 1) + " live " +
                "storage-layout reconciliations, and all " + (RequiredRoles.Length - 1) +
                " live write-front capacity " +
                "proofs are complete.");
        }

        public override void Teardown()
        {
            if (!_enabled)
                return;

            Client.OnUpdate -= Tick;
            ClearReadyMarker();
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!ServicePolicy.IsBagAuditMode() && !StartupCensusGate.IsOpen)
                return;

            if (!_enabled || !Client.InPlay || DateTime.UtcNow < _nextPollUtc)
                return;

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(PollMilliseconds);

            try
            {
                if (TrustedOperators.IsAllBankersReady())
                    return;

                HoldDispatchQueueForStartup();

                if (StartupStorageEnrollmentCoordinator.IsEnrollmentHoldActive(_settingsDir) ||
                    !AllFreshDiagnosticsPresent() ||
                    !AllStorageLayoutsReady() ||
                    !AllStorageWriteFrontsReady())
                {
                    _allDiagnosticsSeenUtc = null;
                    return;
                }

                if (!_allDiagnosticsSeenUtc.HasValue)
                {
                    _allDiagnosticsSeenUtc = DateTime.UtcNow;
                    return;
                }

                if ((DateTime.UtcNow - _allDiagnosticsSeenUtc.Value).TotalMilliseconds <
                    ReadySettleMilliseconds)
                {
                    return;
                }

                PublishReadyMarker();
                ReleaseDispatchQueueAfterStartup();

                string message =
                    "All " + RequiredRoles.Length + " bankers are ready. Fresh diagnostics, all " +
                    (RequiredRoles.Length - 1) + " live storage layouts, and all " +
                    (RequiredRoles.Length - 1) + " live write fronts are reconciled; startup " +
                    "dispatch barrier released.";
                Logger.Information("[CityBankers] " + message);
                TellKavem(message);
            }
            catch (Exception ex)
            {
                Logger.Error("[CityBankers] ALL-BANKERS READINESS barrier failed: " + ex);
            }
        }

        private bool TryLoadExpectedCharacters(out Dictionary<string, string> characters)
        {
            characters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                JObject root = SettingsPaths.ReadBankersSettings(_settingsDir);
                JObject roles = root.GetValue("Roles", StringComparison.OrdinalIgnoreCase) as JObject;
                if (roles == null)
                    return false;

                foreach (string role in RequiredRoles)
                {
                    JProperty roleProperty = roles.Properties().FirstOrDefault(property =>
                        string.Equals(property.Name, role, StringComparison.OrdinalIgnoreCase));
                    JObject roleObject = roleProperty?.Value as JObject;
                    string character = roleObject?.GetValue(
                        "Character",
                        StringComparison.OrdinalIgnoreCase)?.ToString();
                    if (string.IsNullOrWhiteSpace(character))
                        return false;
                    characters[role] = character.Trim();
                }

                return characters.Count == RequiredRoles.Length;
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    "[CityBankers] ALL-BANKERS READINESS could not read the Bankers section: " +
                    ex.Message);
                return false;
            }
        }

        private bool AllFreshDiagnosticsPresent()
        {
            foreach (KeyValuePair<string, string> pair in _expectedCharacters)
            {
                string path = GetDiagnosticPath(pair.Value);
                if (!File.Exists(path))
                    return false;

                try
                {
                    JObject diagnostic = JObject.Parse(File.ReadAllText(path));
                    string character = diagnostic.GetValue(
                        "Character",
                        StringComparison.OrdinalIgnoreCase)?.ToString();
                    if (!string.Equals(
                        character,
                        pair.Value,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    DateTime observedUtc;
                    JToken observed = diagnostic.GetValue(
                        "ObservedUtc",
                        StringComparison.OrdinalIgnoreCase);
                    if (!RuntimeStateStore.TryReadUtc(observed, out observedUtc))
                    {
                        return false;
                    }

                    if (observedUtc < _startedUtc)
                        return false;
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        private bool AllStorageLayoutsReady()
        {
            StorageState storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            string baselineRunId = storage?.BaselineRunId;

            foreach (KeyValuePair<string, string> pair in _expectedCharacters)
            {
                if (string.Equals(pair.Key, "central", StringComparison.OrdinalIgnoreCase))
                    continue;

                string path = WorkerStorageLayoutRecoveryAgent.GetLayoutReadyPath(
                    _settingsDir,
                    pair.Value);
                if (!File.Exists(path))
                    return false;

                try
                {
                    JObject marker = JObject.Parse(File.ReadAllText(path));
                    string character = marker.GetValue(
                        "Character",
                        StringComparison.OrdinalIgnoreCase)?.ToString();
                    string role = marker.GetValue(
                        "Role",
                        StringComparison.OrdinalIgnoreCase)?.ToString();
                    if (!string.Equals(character, pair.Value, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(role, pair.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    DateTime readyUtc;
                    JToken ready = marker.GetValue(
                        "ReadyUtc",
                        StringComparison.OrdinalIgnoreCase);
                    if (!RuntimeStateStore.TryReadUtc(ready, out readyUtc) ||
                        readyUtc < _startedUtc)
                    {
                        return false;
                    }

                    string markerBaseline = marker.GetValue(
                        "BaselineRunId",
                        StringComparison.OrdinalIgnoreCase)?.ToString();
                    if (!string.IsNullOrWhiteSpace(baselineRunId) &&
                        !string.Equals(
                            markerBaseline,
                            baselineRunId,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        private bool AllStorageWriteFrontsReady()
        {
            StorageState storage = RuntimeStateStore.LoadStorageState(_settingsDir);
            string baselineRunId = storage?.BaselineRunId;

            foreach (KeyValuePair<string, string> pair in _expectedCharacters)
            {
                if (string.Equals(pair.Key, "central", StringComparison.OrdinalIgnoreCase))
                    continue;

                string path = StorageWriteFrontReconciliationAgent.GetReadyPath(
                    _settingsDir,
                    pair.Value);
                if (!File.Exists(path))
                    return false;

                try
                {
                    JObject marker = JObject.Parse(File.ReadAllText(path));
                    string character = marker.GetValue(
                        "Character",
                        StringComparison.OrdinalIgnoreCase)?.ToString();
                    string role = marker.GetValue(
                        "Role",
                        StringComparison.OrdinalIgnoreCase)?.ToString();
                    if (!string.Equals(character, pair.Value, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(role, pair.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    DateTime readyUtc;
                    JToken ready = marker.GetValue(
                        "ReadyUtc",
                        StringComparison.OrdinalIgnoreCase);
                    if (!RuntimeStateStore.TryReadUtc(ready, out readyUtc) ||
                        readyUtc < _startedUtc)
                    {
                        return false;
                    }

                    string markerBaseline = marker.GetValue(
                        "BaselineRunId",
                        StringComparison.OrdinalIgnoreCase)?.ToString();
                    if (!string.IsNullOrWhiteSpace(baselineRunId) &&
                        !string.Equals(
                            markerBaseline,
                            baselineRunId,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }

                    int freeSlots;
                    JToken free = marker.GetValue(
                        "FreeSlots",
                        StringComparison.OrdinalIgnoreCase);
                    if (free == null ||
                        !int.TryParse(free.ToString(), out freeSlots) ||
                        freeSlots <= 0)
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        private void HoldDispatchQueueForStartup()
        {
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            bool changed = false;

            foreach (DispatchBatchState batch in
                queue?.Batches ?? new List<DispatchBatchState>())
            {
                if (batch == null)
                    continue;

                if (string.Equals(
                    batch.Status,
                    "queued",
                    StringComparison.OrdinalIgnoreCase))
                {
                    batch.Status = StartupQueuedStatus;
                    batch.UpdatedUtc = DateTime.UtcNow;
                    changed = true;
                    continue;
                }

                if (string.Equals(
                        batch.Status,
                        "failed",
                        StringComparison.OrdinalIgnoreCase) &&
                    IsRecoverablePreTransferFailure(batch.LastError))
                {
                    batch.Status = StartupRecoverableFailedStatus;
                    batch.UpdatedUtc = DateTime.UtcNow;
                    changed = true;
                }
            }

            if (changed)
                RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
        }

        private void ReleaseDispatchQueueAfterStartup()
        {
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            bool changed = false;

            foreach (DispatchBatchState batch in
                queue?.Batches ?? new List<DispatchBatchState>())
            {
                if (batch == null)
                    continue;

                if (string.Equals(
                    batch.Status,
                    StartupQueuedStatus,
                    StringComparison.OrdinalIgnoreCase))
                {
                    batch.Status = "queued";
                    batch.UpdatedUtc = DateTime.UtcNow;
                    changed = true;
                }
                else if (string.Equals(
                    batch.Status,
                    StartupRecoverableFailedStatus,
                    StringComparison.OrdinalIgnoreCase))
                {
                    batch.Status = "failed";
                    batch.UpdatedUtc = DateTime.UtcNow;
                    changed = true;
                }
            }

            if (changed)
                RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);
        }

        private static bool IsRecoverablePreTransferFailure(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return false;

            return string.Equals(
                       error,
                       "Internal worker trade was declined. AO should have returned the offered items to Central inventory.",
                       StringComparison.Ordinal) ||
                   string.Equals(
                       error,
                       "Routed item multiset changed before it could be added to worker trade.",
                       StringComparison.Ordinal) ||
                   error.StartsWith(
                       "Internal worker trade timed out. Any incomplete outgoing AO trade is declined so offered items return to Central inventory.",
                       StringComparison.Ordinal);
        }

        private void PublishReadyMarker()
        {
            string path = GetReadyMarkerPath();
            string temp = path + ".tmp";
            var state = new
            {
                format = "citybankers-all-bankers-ready-v1",
                startedUtc = _startedUtc,
                readyUtc = DateTime.UtcNow,
                characters = RequiredRoles.Select(role => new
                {
                    role = role,
                    character = _expectedCharacters[role]
                }).ToList()
            };

            File.WriteAllText(temp, JsonConvert.SerializeObject(state, Formatting.Indented));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        private void ClearReadyMarker()
        {
            string path = GetReadyMarkerPath();
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

        private string GetReadyMarkerPath()
        {
            return Path.Combine(
                RuntimeStateStore.GetDataDirectory(_settingsDir),
                TrustedOperators.AllBankersReadyMarkerFileName);
        }

        private string GetDiagnosticPath(string character)
        {
            return Path.Combine(
                RuntimeStateStore.GetDataDirectory(_settingsDir),
                "citybankers-diagnostic-" + SafeFileToken(character) + ".json");
        }

        private static string SafeFileToken(string value)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                token = token.Replace(invalid, '_');
            return token;
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
                "central",
                "TELL -> " + TrustedOperators.BootstrapAdmin + ": " + message);
        }
    }
}
