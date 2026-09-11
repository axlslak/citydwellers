using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityBankers.Shared;
using Newtonsoft.Json;

namespace CityBankers
{
    /// <summary>
    /// Keeps Banker.exe bagaudit observational. Failed-timeout recovery normally retries
    /// automatically; during a physical reconciliation audit that would move the very
    /// items we are trying to locate. Central therefore places only those failed timeout
    /// batches on a reversible hold for the lifetime of the bagaudit process.
    ///
    /// The original LastError strings are persisted in a sidecar before the queue is
    /// changed. A clean teardown restores them. A later normal startup also restores a
    /// leftover hold after an unclean audit-process exit.
    /// </summary>
    public class AuditRecoveryHoldGuard : ClientlessPluginEntry
    {
        private const string HoldError = "BAGAUDIT PHYSICAL RECONCILIATION HOLD";

        private string _settingsDir;
        private string _role;
        private bool _isCentral;
        private bool _auditMode;
        private string _holdPath;
        private bool _enabled;

        public override void Init(string pluginDir)
        {
            // Full physical census and BankingService now own normal-mode recovery.
            if (StartupCensusGate.UsesPhysicalRecovery) return;

            if (!ServicePolicy.IsBagAuditMode() && StartupCensusGate.Defer(() => Init(pluginDir)))
                return;

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            _role = ResolveCurrentRole();
            _isCentral = string.Equals(_role, "central", StringComparison.OrdinalIgnoreCase);
            if (!_isCentral)
                return;

            _holdPath = Path.Combine(
                RuntimeStateStore.GetDataDirectory(_settingsDir),
                "bagaudit-recovery-hold.json");
            _auditMode = ServicePolicy.IsBagAuditMode();

            // First repair any hold left by an earlier unclean exit.
            RestoreExistingHold();

            if (!_auditMode)
                return;

            ApplyAuditHold();
            _enabled = true;
        }

        public override void Teardown()
        {
            if (_isCentral && _enabled)
                RestoreExistingHold();
        }

        private void ApplyAuditHold()
        {
            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            List<HeldBatch> held = (queue?.Batches ?? new List<DispatchBatchState>())
                .Where(batch =>
                    batch != null &&
                    string.Equals(batch.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(batch.LastError) &&
                    batch.LastError.IndexOf(
                        "Internal worker trade timed out",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(batch => new HeldBatch
                {
                    BatchId = batch.BatchId,
                    LastError = batch.LastError
                })
                .ToList();

            if (held.Count == 0)
            {
                Logger.Information(
                    "[CityBankers] BAGAUDIT RECOVERY HOLD: no failed timeout batches require suspension.");
                return;
            }

            var file = new HoldFile
            {
                Format = "citybankers-bagaudit-recovery-hold-v1",
                CreatedUtc = DateTime.UtcNow,
                Batches = held
            };
            WriteAtomicJson(_holdPath, file);

            foreach (HeldBatch item in held)
            {
                DispatchBatchState batch = queue.Batches.FirstOrDefault(candidate =>
                    candidate != null && string.Equals(
                        candidate.BatchId,
                        item.BatchId,
                        StringComparison.Ordinal));
                if (batch != null)
                {
                    batch.LastError = HoldError;
                    batch.UpdatedUtc = DateTime.UtcNow;
                }
            }
            RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);

            Logger.Information(
                $"[CityBankers] BAGAUDIT RECOVERY HOLD armed on Central; held={held.Count}. " +
                "No failed-timeout batch may auto-requeue while physical truth is being audited.");
        }

        private void RestoreExistingHold()
        {
            if (string.IsNullOrWhiteSpace(_holdPath) || !File.Exists(_holdPath))
                return;

            HoldFile file;
            try
            {
                file = JsonConvert.DeserializeObject<HoldFile>(File.ReadAllText(_holdPath));
            }
            catch (Exception ex)
            {
                Logger.Error($"BAGAUDIT RECOVERY HOLD could not read '{_holdPath}': {ex}");
                return;
            }

            DispatchQueueState queue = RuntimeStateStore.LoadDispatchQueue(_settingsDir);
            int restored = 0;
            foreach (HeldBatch held in file?.Batches ?? new List<HeldBatch>())
            {
                DispatchBatchState batch = (queue?.Batches ?? new List<DispatchBatchState>())
                    .FirstOrDefault(candidate =>
                        candidate != null && string.Equals(
                            candidate.BatchId,
                            held.BatchId,
                            StringComparison.Ordinal));
                if (batch == null ||
                    !string.Equals(batch.Status, "failed", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(batch.LastError, HoldError, StringComparison.Ordinal))
                {
                    continue;
                }

                batch.LastError = held.LastError;
                batch.UpdatedUtc = DateTime.UtcNow;
                restored++;
            }

            if (restored > 0)
                RuntimeStateStore.SaveDispatchQueue(_settingsDir, queue);

            try
            {
                File.Delete(_holdPath);
            }
            catch (Exception ex)
            {
                Logger.Warning($"BAGAUDIT RECOVERY HOLD restored but sidecar cleanup failed: {ex.Message}");
            }

            Logger.Information(
                $"[CityBankers] BAGAUDIT RECOVERY HOLD restored={restored}; normal failed-batch recovery policy is active again.");
        }

        private string ResolveCurrentRole()
        {
            try
            {
                BankerConfig config = SettingsPaths.ReadBankersSettings(_settingsDir)
                    .ToObject<BankerConfig>();
                foreach (KeyValuePair<string, RoleConfig> pair in
                    config?.Roles ?? new Dictionary<string, RoleConfig>())
                {
                    if (pair.Value != null && string.Equals(
                        pair.Value.Character,
                        Client.CharacterName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Key;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"BAGAUDIT RECOVERY HOLD could not resolve role: {ex.Message}");
            }
            return null;
        }

        private static void WriteAtomicJson(string path, object value)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(value, Formatting.Indented));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }

        private sealed class HoldFile
        {
            public string Format;
            public DateTime CreatedUtc;
            public List<HeldBatch> Batches = new List<HeldBatch>();
        }

        private sealed class HeldBatch
        {
            public string BatchId;
            public string LastError;
        }

        private sealed class BankerConfig
        {
            public Dictionary<string, RoleConfig> Roles;
        }

        private sealed class RoleConfig
        {
            public string Username;
            public string Character;
        }
    }
}
