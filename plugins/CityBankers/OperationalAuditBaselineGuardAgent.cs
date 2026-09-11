using System;
using System.IO;
using System.Linq;

using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityBankers.Shared;
using Newtonsoft.Json.Linq;

namespace CityBankers
{
    /// <summary>
    /// storage-baseline.json is an audit/cutover artifact, not a live operational feed.
    /// Normal Banker.exe all runs use storage-state.json as the mutable authority. This
    /// Central-only guard removes the replaceable audit 'latest' pointer and stale transient
    /// bagaudit command/result inputs before normal service ticks can mistake old audit data
    /// for permission to overwrite live occupancy. Immutable per-run archives/dumps remain
    /// untouched. Explicit Banker.exe bagaudit mode bypasses this guard completely.
    /// </summary>
    public class OperationalAuditBaselineGuardAgent : ClientlessPluginEntry
    {
        private string _settingsDir;
        private bool _enabled;
        private DateTime _nextPollUtc;
        private string _lastNoticeRunId;

        public override void Init(string pluginDir)
        {
            // Full physical census and BankingService now own normal-mode recovery.
            if (StartupCensusGate.UsesPhysicalRecovery) return;

            if (!ServicePolicy.IsBagAuditMode() && StartupCensusGate.Defer(() => Init(pluginDir)))
                return;

            if (IsBagAuditMode())
                return;

            string error;
            if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out error))
                throw new InvalidOperationException(error);

            if (!CentralCharacterGuard.IsCurrentCharacterCentral(
                    _settingsDir,
                    Client.CharacterName))
            {
                return;
            }

            _enabled = true;
            _nextPollUtc = DateTime.UtcNow;

            // Init runs before the normal OnUpdate loop. Remove stale audit handoff inputs
            // now so the legacy coordinator/seeder/importer pollers have nothing to promote
            // during Banker.exe all.
            DeleteTransientAuditInputs();
            QuarantineLatestPointer();
            Client.OnUpdate += Tick;

            Logger.Information(
                "[CityBankers] OPERATIONAL AUDIT-BASELINE GUARD initialized on Central; " +
                "normal service treats storage-state.json as mutable authority and keeps " +
                "bagaudit handoff artifacts out of the live import path.");
        }

        public override void Teardown()
        {
            if (_enabled)
                Client.OnUpdate -= Tick;
        }

        private void Tick(object sender, double deltaTime)
        {
            if (!ServicePolicy.IsBagAuditMode() && !StartupCensusGate.IsOpen)
                return;

            if (!_enabled || DateTime.UtcNow < _nextPollUtc)
                return;

            _nextPollUtc = DateTime.UtcNow.AddMilliseconds(250);
            try
            {
                DeleteTransientAuditInputs();
                QuarantineLatestPointer();
            }
            catch (Exception ex)
            {
                Logger.Error(
                    "[CityBankers] OPERATIONAL AUDIT-BASELINE GUARD failed: " + ex);
            }
        }

        private void DeleteTransientAuditInputs()
        {
            DeleteMatching("citybankers-bagaudit-command-*");
            DeleteMatching("citybankers-bagaudit-result-*");
        }

        private void DeleteMatching(string pattern)
        {
            foreach (string path in Directory.GetFiles(
                RuntimeStateStore.GetDataDirectory(_settingsDir),
                pattern))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Retry on the next 250ms guard pass. These are transient handoff files,
                    // never the immutable diagnostic dump or storage-baseline archive.
                }
            }
        }

        private void QuarantineLatestPointer()
        {
            string dataDir = RuntimeStateStore.GetDataDirectory(_settingsDir);
            string latestPath = Path.Combine(dataDir, "storage-baseline.json");
            if (!File.Exists(latestPath))
                return;

            string runId = null;
            string content = File.ReadAllText(latestPath);
            try
            {
                JObject root = JObject.Parse(content);
                runId = root.GetValue("runId", StringComparison.OrdinalIgnoreCase)?.ToString();
            }
            catch
            {
            }

            string archiveDir = Path.Combine(dataDir, "storage-baselines");
            Directory.CreateDirectory(archiveDir);
            string archivePath;
            if (!string.IsNullOrWhiteSpace(runId))
            {
                archivePath = Path.Combine(
                    archiveDir,
                    "storage-baseline-" + SafeFileToken(runId) + ".json");
            }
            else
            {
                archivePath = Path.Combine(
                    archiveDir,
                    "storage-baseline-quarantined-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json");
            }

            if (!File.Exists(archivePath))
                File.WriteAllText(archivePath, content);

            File.Delete(latestPath);

            string noticeKey = runId ?? Path.GetFileName(archivePath);
            if (!string.Equals(noticeKey, _lastNoticeRunId, StringComparison.Ordinal))
            {
                _lastNoticeRunId = noticeKey;
                Logger.Warning(
                    "[CityBankers] LIVE BASELINE GUARD removed audit latest pointer from " +
                    "normal service mode; preserved archive='" + archivePath +
                    "'. Operational storage-state was not replaced.");
            }
        }

        private static bool IsBagAuditMode()
        {
            return ServicePolicy.IsBagAuditMode();
        }

        private static string SafeFileToken(string value)
        {
            string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                token = token.Replace(invalid, '_');
            return token.Replace('\\', '_').Replace('/', '_').Replace(':', '_');
        }
    }
}
