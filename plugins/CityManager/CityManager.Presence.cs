using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private ProxyPresenceClient _proxyPresence;
        private string _presenceOwnerToken;
        private readonly object _presenceQueriesSync = new object();
        private readonly List<PresenceQuery> _presenceQueries = new List<PresenceQuery>();
        private readonly ConcurrentQueue<string> _presenceAltUpdates = new ConcurrentQueue<string>();
        private int _presenceQueriesChanged;
        private long _lastPresenceRevision = -1;

        private sealed class PresenceQuery
        {
            internal string Name, WorkKey;
            internal ReplyTarget Target;
            internal string[] Names;
            internal DateTime DeadlineUtc;
            internal volatile bool AltsPending;
        }

        private void InitializePresence()
        {
            try
            {
                var root = JObject.Parse(File.ReadAllText(Path.Combine(_settingsDir, "citydwellers.json")));
                var manager = root.GetValue("Manager", StringComparison.OrdinalIgnoreCase) as JObject;
                var section = manager?.GetValue("ChatProxy", StringComparison.OrdinalIgnoreCase) as JObject;
                var settings = section?.ToObject<ProxyPresenceSettings>();
                if (settings == null || !settings.Enabled) return;
                if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port < 1 || settings.Port > 65535 ||
                    settings.ReconnectSeconds < 1)
                    throw new InvalidDataException("Manager.ChatProxy requires Host, a valid Port and a positive ReconnectSeconds.");
                string token = Guid.NewGuid().ToString("N");
                if (!ManagerMemory.Current.AcquirePresenceService(Client.CharacterName, token)) return;
                _presenceOwnerToken = token;
                ManagerMemory.Current.SetPresenceService(token, false, "Connecting to the buddy proxy.");
                _proxyPresence = new ProxyPresenceClient(settings);
                _proxyPresence.Start();
            }
            catch (Exception ex)
            {
                ShutdownPresence();
                // Do not echo the configuration or credential values.
                Logger.Warning("Proxy presence unavailable: " + (ex is InvalidDataException ? ex.Message : ex.GetType().Name));
            }
        }

        private void ShutdownPresence()
        {
            _proxyPresence?.Dispose();
            _proxyPresence = null;
            if (_presenceOwnerToken != null) ManagerMemory.Current.ReleasePresenceService(_presenceOwnerToken);
            _presenceOwnerToken = null;
            lock (_presenceQueriesSync)
            {
                foreach (var query in _presenceQueries) EndPublicWork(query.WorkKey);
                _presenceQueries.Clear();
            }
        }

        private void ProcessIsCommand(string[] parts, ReplyTarget target)
        {
            string name, error;
            if (parts.Length != 2 || !TryNormalizeAltName(parts[1], out name, out error))
            { Reply(target, Usage(target, "is <character>")); return; }
            string workKey;
            if (!BeginPublicWork(target, out workKey)) return;
            var query = new PresenceQuery { Name = name, Target = target, WorkKey = workKey,
                DeadlineUtc = DateTime.UtcNow.AddSeconds(30) };
            try
            {
                query.Names = GetAltIdentityCandidates(name).Concat(new[] { name }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                ManagerMemory.Current.TrackPlayerPresence(query.Names);
                if (!string.IsNullOrWhiteSpace(_altsBotName))
                {
                    query.AltsPending = true;
                    if (!QueueAltLookup(name, new AltLookupWaiter { Completed = success => {
                        if (success && !_publicWorkStopped) _presenceAltUpdates.Enqueue(name);
                        query.AltsPending = false;
                        Interlocked.Exchange(ref _presenceQueriesChanged, 1);
                    } },
                        false, false, "presence")) query.AltsPending = false;
                }
                lock (_presenceQueriesSync) _presenceQueries.Add(query);
                Interlocked.Exchange(ref _presenceQueriesChanged, 1);
            }
            catch { EndPublicWork(workKey); throw; }
        }

        private void TickPresence()
        {
            var memory = ManagerMemory.Current;
            string updatedAlts;
            while (_presenceAltUpdates.TryDequeue(out updatedAlts))
                memory.TrackKnownAltPresence(updatedAlts);
            if (_proxyPresence != null)
            {
                var reports = new List<PlayerPresenceReport>();
                ProxyPresenceEvent value;
                for (int i = 0; i < 2048 && _proxyPresence.TryRead(out value); i++)
                {
                    if (value.Report != null) { reports.Add(value.Report); continue; }
                    if (reports.Count != 0) { memory.ApplyPresenceReports(_presenceOwnerToken, reports.ToArray()); reports.Clear(); }
                    if (value.Ready.HasValue)
                    {
                        string previous = memory.ReadPresenceServiceStatus();
                        memory.SetPresenceService(_presenceOwnerToken, value.Ready.Value, value.Message);
                        if (previous != value.Message)
                        {
                            Logger.Information("PRESENCE " + value.Message);
                            DevTrace("PRESENCE " + value.Message);
                        }
                    }
                    else if (value.MissingName != null)
                    {
                        if (value.Message == "removed") memory.InvalidatePlayerPresence(_presenceOwnerToken, value.MissingName);
                        else memory.PresenceLookupFailed(_presenceOwnerToken, value.MissingName);
                    }
                }
                if (reports.Count != 0) memory.ApplyPresenceReports(_presenceOwnerToken, reports.ToArray());
                foreach (string name in memory.TakePresenceRequests(_presenceOwnerToken, 128))
                    _proxyPresence.Track(name);
            }

            PresenceQuery[] queries;
            long revision = memory.ReadPresenceRevision();
            bool changed = Interlocked.Exchange(ref _presenceQueriesChanged, 0) != 0 || revision != _lastPresenceRevision;
            _lastPresenceRevision = revision;
            lock (_presenceQueriesSync)
            {
                if (!changed && !_presenceQueries.Any(q => DateTime.UtcNow >= q.DeadlineUtc)) return;
                queries = _presenceQueries.ToArray();
            }
            foreach (var query in queries)
            {
                // Passive or requested alt replies may have expanded the group.
                string[] names = GetAltIdentityCandidates(query.Name).Concat(new[] { query.Name })
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (!new HashSet<string>(query.Names, StringComparer.OrdinalIgnoreCase).SetEquals(names))
                {
                    query.Names = names;
                    memory.TrackPlayerPresence(names);
                }
                var rows = names.Select(memory.ReadPlayerPresence).ToArray();
                string[] online = rows.Where(r => r.Online == true).Select(r => r.Seen.Name).ToArray();
                bool pending = rows.Any(r => !r.Online.HasValue && r.Error == null);
                if (online.Length == 0 && _proxyPresence != null && _proxyPresence.Connecting &&
                    DateTime.UtcNow < query.DeadlineUtc) continue;
                if (online.Length == 0 && memory.IsPresenceServiceReady() &&
                    (pending || query.AltsPending) && DateTime.UtcNow < query.DeadlineUtc) continue;
                lock (_presenceQueriesSync)
                    if (!_presenceQueries.Remove(query)) continue;
                try
                {
                    string answer;
                    if (online.Length != 0)
                        answer = PresenceText(query.Name, ColorTitle) + ": " + PresenceText("online", ColorGood) +
                            " — " + string.Join(", ", online.Select(n => PresenceText(n, ColorGood))) + ".";
                    else
                    {
                        int offline = rows.Count(r => r.Online == false);
                        int unknown = rows.Count(r => !r.Online.HasValue);
                        answer = PresenceText(query.Name, ColorTitle) + ": " +
                            PresenceText(unknown == 0 ? "offline" : "online status unknown", unknown == 0 ? ColorMuted : ColorWarn) +
                            " (" + offline + "/" + names.Length + " known characters confirmed offline" +
                            (unknown == 0 ? "" : "; " + unknown + " unconfirmed") + ").";
                        if (!memory.IsPresenceServiceReady()) answer += " " + EscapeBlobText(memory.ReadPresenceServiceStatus());
                        else if (rows.Any(r => r.Error != null)) answer += " " + EscapeBlobText(rows.First(r => r.Error != null).Error);
                    }
                    Reply(query.Target, answer);
                }
                finally { EndPublicWork(query.WorkKey); }
            }
        }

        private static string PresenceText(string text, string color) =>
            "<font color='" + color + "'>" + EscapeBlobText(text) + "</font>";
    }
}
