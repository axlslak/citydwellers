using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;
using SmokeLounge.AOtomation.Messaging.GameData;

namespace CityManager
{
    public partial class CityManager
    {
        // Separate namespaces in the existing append-only cloak event table.
        // The singleton CloakState and legacy/unprefixed history remain AP-only.
        private const string AllianceCloakSource = "AllianceFlipper:";
        private readonly object _allianceSync = new object();
        private readonly Dictionary<string, AllianceCloak> _allianceCloaks =
            new Dictionary<string, AllianceCloak>(StringComparer.OrdinalIgnoreCase);
        private bool _allianceRequestInFlight;

        private sealed class AllianceConfig
        {
            public Dictionary<string, FlipperTargetSettings> Targets;
        }

        private sealed class AllianceCloak
        {
            public string Key;
            public FlipperTargetSettings Settings;
            public CloakStatus Status;
            public DateTime ObservedUtc, NextAttemptUtc;
            public DateTime? DueUtc;
            public long Revision;
        }

        private static bool IsAllianceCloakEvent(CityDwellers.Shared.CloakEvent row)
        {
            return row.Source != null && row.Source.StartsWith(AllianceCloakSource, StringComparison.Ordinal);
        }

        private void InitializeAllianceFlippers()
        {
            AllianceConfig config;
            string error;
            if (!SettingsPaths.TryReadSettingsSection(_settingsDir, "Flipper", out config, out error))
                throw new InvalidOperationException(error);
            var history = ManagerMemory.Current.ReadCloakEvents(ManagerAccounting.TransactionId);
            foreach (var entry in config?.Targets ?? new Dictionary<string, FlipperTargetSettings>())
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 64 || entry.Value == null ||
                    string.IsNullOrWhiteSpace(entry.Value.Character) || string.IsNullOrWhiteSpace(entry.Value.RelayBot) ||
                    _allianceCloaks.ContainsKey(entry.Key))
                    throw new InvalidOperationException("Invalid Flipper.Targets configuration.");
                var state = new AllianceCloak { Key = entry.Key, Settings = entry.Value,
                    Status = CloakStatus.Unknown, NextAttemptUtc = DateTime.UtcNow };
                var latest = history.Where(row => string.Equals(row.Source, AllianceCloakSource + entry.Key, StringComparison.OrdinalIgnoreCase) &&
                    !UtcTimestamp.IsFuture(row.OccurredUtc, DateTime.UtcNow))
                    .OrderByDescending(row => row.OccurredUtc).FirstOrDefault();
                if (latest != null)
                {
                    state.Status = (CloakStatus)latest.NewStatus;
                    state.ObservedUtc = UtcTimestamp.Normalize(latest.OccurredUtc);
                    state.DueUtc = UtcTimestamp.Normalize(latest.CanRaiseAtUtc);
                    if (state.Status == CloakStatus.Disabled && state.DueUtc.HasValue)
                        state.NextAttemptUtc = state.DueUtc.Value;
                }
                _allianceCloaks.Add(entry.Key, state);
                Logger.Information($"ALLIANCE CLOAK {entry.Key}: {entry.Value.Character}; " +
                    $"relay={entry.Value.RelayBot}; restored={state.Status}; due={state.DueUtc:O}.");
            }
        }

        private void QueueAllianceRelay(uint senderId, string senderName, string raw, string transport, string channel, object channelId = null)
        {
            if (senderId == 0 || !_allianceCloaks.Values.Any(state =>
                string.Equals(state.Settings.RelayBot, senderName, StringComparison.OrdinalIgnoreCase))) return;
            // SDK-resolved sender identity, never a name extracted from the prose.
            // Keep the original before parsing; the AP transport is not the source org.
            QueueServiceMessage(new NativeServiceMessage {
                Source = "Relay.Alliance", RawText = raw,
                Fields = new JObject { ["SenderId"] = senderId, ["SenderName"] = senderName,
                    ["Transport"] = transport, ["ChannelName"] = channel,
                    ["ChannelId"] = channelId == null ? JValue.CreateNull() : JToken.FromObject(channelId) }
            });
        }

        private bool ObserveAllianceRelay(ServiceEvent report, NativeServiceMessage notice)
        {
            string sender = (string)notice.Fields?["SenderName"];
            if (((uint?)notice.Fields?["SenderId"]).GetValueOrDefault() == 0) return false;
            // Only a leading explicit origin label is accepted. A quoted player
            // message containing GOA or a cloak suffix is not a service event.
            string preserved = Regex.Replace(notice.RawText, "<Unknown>", "AnarchyOnline", RegexOptions.IgnoreCase);
            string text = WebUtility.HtmlDecode(Regex.Replace(preserved, "<[^>]*>", "")).Trim();
            var match = Regex.Match(text,
                @"\A(?:\[Guest\]\s*)?\[(?<org>[^\]\r\n]+)\]\s*(?:(?:\[(?:Org Msg|Org|System)\]:?|(?:Org Msg|Org|System|AnarchyOnline|<Unknown>|Unknown):)\s*)?(?<actor>" +
                CharacterNames.RegexToken + @") turned the cloaking device in your city (?<state>on|off)\.\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return false;
            string label = match.Groups["org"].Value.Trim();
            var targets = _allianceCloaks.Values.Where(state =>
                string.Equals(state.Settings.RelayBot, sender, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(state.Key, label, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(state.Settings.Organization, label, StringComparison.OrdinalIgnoreCase) ||
                 (state.Settings.RelayLabels ?? new List<string>()).Any(alias =>
                    string.Equals(alias, label, StringComparison.OrdinalIgnoreCase)))).ToList();
            if (targets.Count != 1) return false;
            var target = targets[0];
            var status = match.Groups["state"].Value.Equals("on", StringComparison.OrdinalIgnoreCase)
                ? CloakStatus.Enabled : CloakStatus.Disabled;
            DateTime observed = UtcTimestamp.Normalize(report.TimeUtc);
            lock (_allianceSync)
            {
                if (observed < target.ObservedUtc) return true;
                DateTime? due = status == CloakStatus.Disabled
                    ? target.Status == CloakStatus.Disabled && target.DueUtc.HasValue
                        ? target.DueUtc : observed.AddSeconds(ProvisionalCloakDownSeconds)
                    : (DateTime?)null;
                RecordAllianceCloak(target, status, observed, due, null,
                    "relay_cloak_" + match.Groups["state"].Value.ToLowerInvariant(),
                    match.Groups["actor"].Value, notice.RawText);
                target.NextAttemptUtc = due ?? DateTime.MaxValue;
            }
            Logger.Information($"ALLIANCE CLOAK {target.Key}: relay={status}; due={target.DueUtc:O}.");
            return true;
        }

        private void RecordAllianceCloak(AllianceCloak target, CloakStatus status, DateTime observed,
            DateTime? due, int? timer, string kind, string actor, string raw)
        {
            var record = new CityDwellers.Shared.CloakEvent {
                Id = Guid.NewGuid().ToString("N"), OccurredUtc = observed,
                PreviousStatus = (int)target.Status, NewStatus = (int)status,
                ShieldTimerInSeconds = timer, CanRaiseAtUtc = due,
                EventType = kind, Source = AllianceCloakSource + target.Key,
                Actor = actor, ChannelName = target.Settings.Organization ?? target.Key, RawMessage = raw
            };
            ManagerAccounting.Transaction("Alliance cloak " + target.Key, () =>
                ManagerMemory.Current.RecordCloakEvent(ManagerAccounting.TransactionId, record));
            target.Status = status; target.ObservedUtc = observed; target.DueUtc = due; target.Revision++;
        }

        private bool PrimaryFlipperReserved()
        {
            if (CityRaidAutomation.IsFlipperBusy()) return true;
            lock (_raidSync)
                return _raidSession != null && (_raidSession.Stage == RaidStage.ControllerFill ||
                    _raidSession.Stage == RaidStage.LoweringCloak);
        }

        private void TickAllianceFlippers()
        {
            if (_publicWorkStopped || _allianceCloaks.Count == 0 || PrimaryFlipperReserved()) return;
            AllianceCloak target;
            long revision;
            lock (_allianceSync)
            {
                if (_allianceRequestInFlight) return;
                target = _allianceCloaks.Values.Where(row => row.NextAttemptUtc <= DateTime.UtcNow)
                    .OrderBy(row => row.NextAttemptUtc).FirstOrDefault();
                if (target == null) return;
                revision = target.Revision;
                _allianceRequestInFlight = true;
            }
            ThreadPool.QueueUserWorkItem(_ => RaiseAllianceCloak(target, revision));
        }

        private void RaiseAllianceCloak(AllianceCloak target, long revision)
        {
            try
            {
                if (_publicWorkStopped || PrimaryFlipperReserved()) return;
                var request = new WorkerRequest { Id = Guid.NewGuid().ToString("N"), Command = "ensure-enabled",
                    FlipperTarget = target.Key, NotBeforeUtc = DateTime.UtcNow, TimeoutSeconds = 145 };
                WorkerResponse response;
                lock (CityRaidAutomation.FlipperRequestSync)
                {
                    if (_publicWorkStopped || PrimaryFlipperReserved()) return;
                    response = SendWorkerRequest(FlipperComponentName, request, WorkerRequestMinimumTimeoutMs);
                }
                if (_publicWorkStopped) return;
                lock (_allianceSync)
                {
                    // Newer relay evidence owns the next decision. An in-flight
                    // result cannot erase a second cloak change while it logged in.
                    if (target.Revision != revision) return;
                    DateTime observed = UtcTimestamp.Normalize(response.ObservedUtc) ?? DateTime.UtcNow;
                    bool identity = string.Equals(response.Character, target.Settings.Character, StringComparison.OrdinalIgnoreCase);
                    bool valid = identity && !response.Cached && !UtcTimestamp.IsFuture(observed, DateTime.UtcNow) &&
                        observed >= request.NotBeforeUtc.Value;
                    bool enabled = valid && response.Ok && string.Equals(response.CloakState, "Enabled", StringComparison.OrdinalIgnoreCase);
                    bool disabled = valid && string.Equals(response.CloakState, "Disabled", StringComparison.OrdinalIgnoreCase);
                    if (enabled || disabled)
                    {
                        int retry = response.ShieldTimerInSeconds.GetValueOrDefault() > 0
                            ? Math.Max(5, Math.Min(3600, response.ShieldTimerInSeconds.Value) + 2) : 30;
                        DateTime? due = enabled ? (DateTime?)null : observed.AddSeconds(retry);
                        RecordAllianceCloak(target, enabled ? CloakStatus.Enabled : CloakStatus.Disabled,
                            observed, due, response.ShieldTimerInSeconds, "flipper_observation",
                            response.Character, response.Message);
                        target.NextAttemptUtc = due ?? DateTime.MaxValue;
                    }
                    else target.NextAttemptUtc = DateTime.UtcNow.AddSeconds(90);
                    if (enabled) Logger.Information($"ALLIANCE CLOAK {target.Key}: confirmed Enabled by {response.Character}.");
                    else Logger.Warning($"ALLIANCE CLOAK {target.Key}: {response.Message}; next attempt={target.NextAttemptUtc:O}.");
                }
            }
            catch (Exception error)
            {
                lock (_allianceSync)
                    if (target.Revision == revision) target.NextAttemptUtc = DateTime.UtcNow.AddSeconds(30);
                if (!_publicWorkStopped) Logger.Warning($"ALLIANCE CLOAK {target.Key}: {error.Message}; will retry.");
            }
            finally
            {
                lock (_allianceSync) _allianceRequestInFlight = false;
            }
        }
    }
}
