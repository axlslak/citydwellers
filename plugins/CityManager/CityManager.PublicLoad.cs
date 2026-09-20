using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AOSharp.Clientless.Logging;

namespace CityManager
{
    public partial class CityManager
    {
        // Limit work before creating tasks or rendering replies. Alts share a budget.
        private sealed class PublicCaller
        {
            public double Tokens = 6, Updated, NoticeAfter;
            public int Work;
        }
        private readonly object _publicLoadSync = new object();
        private readonly Dictionary<string, PublicCaller> _publicCallers =
            new Dictionary<string, PublicCaller>(StringComparer.OrdinalIgnoreCase);
        private double _publicTokens = 64, _publicUpdated;
        private int _publicWork;
        private double _publicBacklogCheckAfter;
        private bool _publicBacklogged;
        private volatile bool _publicWorkStopped;
        private readonly object _withdrawalAdmissionSync = new object();
        private static double PublicNow => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        private string PublicCallerKey(string sender, ReplyTarget target)
        {
            string name = ResolveCanonicalAltMain(sender ?? target?.SenderName);
            return !string.IsNullOrWhiteSpace(name) ? name : "id:" + (target?.SenderId ?? 0);
        }

        private PublicCaller GetPublicCaller(string key, double now)
        {
            PublicCaller caller;
            if (_publicCallers.TryGetValue(key, out caller)) return caller;
            if (_publicCallers.Count >= 1024)
            {
                foreach (string old in _publicCallers.Where(p => p.Value.Work == 0 &&
                    now - p.Value.Updated >= 600).Select(p => p.Key).ToArray()) _publicCallers.Remove(old);
                if (_publicCallers.Count >= 1024) return null;
            }
            caller = new PublicCaller { Updated = now };
            _publicCallers.Add(key, caller);
            return caller;
        }

        private void PublicBusy(ReplyTarget target, bool notify)
        {
            // A rejection must not itself become an org flood or a tell backlog.
            if (notify && target != null && target.SenderId != 0)
                Reply(ReplyTarget.ForTell(target.SenderId, target.SenderName),
                    "Please slow down or wait for your pending replies. Busy requests were not queued; try again shortly.");
        }

        private bool AdmitPublicCommand(string sender, ReplyTarget target)
        {
            string key = PublicCallerKey(sender, target);
            bool accepted = false, notify = false;
            lock (_publicLoadSync)
            {
                double now = PublicNow;
                var caller = GetPublicCaller(key, now);
                if (caller == null) return false;
                caller.Tokens = Math.Min(6, caller.Tokens + (now - caller.Updated) / 2);
                caller.Updated = now;
                _publicTokens = Math.Min(64, _publicTokens + (now - _publicUpdated) * 10);
                _publicUpdated = now;
                if (now >= _publicBacklogCheckAfter)
                {
                    _publicBacklogCheckAfter = now + 1;
                    bool wasBacklogged = _publicBacklogged;
                    try { _publicBacklogged = CityDwellers.Shared.TellQueue.IsBacklogged(_dataDir, wasBacklogged ? 128 : 256); }
                    catch { _publicBacklogged = true; }
                    if (wasBacklogged != _publicBacklogged)
                        Logger.Warning(_publicBacklogged ? "Public command admission paused: tell backlog or queue storage unavailable."
                            : "Public command admission resumed: tell backlog cleared.");
                }
                if (!_publicWorkStopped && !_publicBacklogged && caller.Tokens >= 1 && _publicTokens >= 1)
                { caller.Tokens--; _publicTokens--; accepted = true; }
                else if (!_publicBacklogged && now >= caller.NoticeAfter)
                { caller.NoticeAfter = now + 10; notify = true; }
            }
            PublicBusy(target, notify);
            return accepted;
        }

        private bool BeginPublicWork(ReplyTarget target, out string key)
        {
            key = PublicCallerKey(null, target);
            bool accepted = false, notify = false;
            lock (_publicLoadSync)
            {
                double now = PublicNow;
                var caller = GetPublicCaller(key, now);
                if (caller == null) return false;
                caller.Updated = now;
                if (!_publicWorkStopped && !_publicBacklogged && _publicWork < 24 && caller.Work < 3)
                { _publicWork++; caller.Work++; accepted = true; }
                else if (!_publicBacklogged && now >= caller.NoticeAfter)
                { caller.NoticeAfter = now + 10; notify = true; }
            }
            PublicBusy(target, notify);
            return accepted;
        }

        private void EndPublicWork(string key)
        {
            lock (_publicLoadSync)
            {
                _publicWork--;
                _publicCallers[key].Work--;
            }
        }

        private void QueuePublicWork(ReplyTarget target, Action action)
        {
            QueuePublicWorkAsync(target, () => { action(); return Task.CompletedTask; });
        }

        private void QueuePublicWorkAsync(ReplyTarget target, Func<Task> action)
        {
            string key;
            if (!BeginPublicWork(target, out key)) return;
            try
            {
                if (!ThreadPool.QueueUserWorkItem(async ignored =>
                {
                    try { if (!_publicWorkStopped) await action().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        Logger.Error("Public command failed: " + ex);
                        if (!_publicWorkStopped) Reply(target, "That request could not be completed. Please try again shortly.");
                    }
                    finally { EndPublicWork(key); }
                })) EndPublicWork(key);
            }
            catch { EndPublicWork(key); throw; }
        }
    }
}
