using AOSharp.Common.GameData;
using System.Collections.Generic;
using System.Linq;

namespace MalisBuffBots
{
    public class BuffQueue
    {
        private readonly object _sync = new object();
        private readonly Queue<BuffEntry> _queue = new Queue<BuffEntry>();
        private BuffEntry _current;
        private readonly List<BuffEntry> _awaiting = new List<BuffEntry>();
        private readonly HashSet<BuffEntry> _deferred = new HashSet<BuffEntry>();
        internal BuffEntry[] Awaiting { get { lock (_sync) return _awaiting.ToArray(); } }
        public BuffEntry Current { get { lock (_sync) return _current; } }
        public BuffEntry[] AllEntries
        {
            get { lock (_sync) return _queue.Concat(_awaiting).Concat(_current == null ? new BuffEntry[0] : new[] { _current }).ToArray(); }
        }

        public QueueState Process(Identity preferredRequester)
        {
            lock (_sync)
            {
                if (_current != null) return QueueState.Current;
                if (_queue.Count == 0) return QueueState.Empty;
                // Finish configured self buffs before dependent customer casts.
                // Never preempt a cast already in progress.
                var preferred = _queue.FirstOrDefault(e => e.Requester == preferredRequester && !_deferred.Contains(e));
                if (preferred == null) _current = _queue.Dequeue();
                else
                {
                    var remaining = _queue.Where(e => !ReferenceEquals(e, preferred)).ToArray();
                    _queue.Clear();
                    foreach (var entry in remaining) _queue.Enqueue(entry);
                    _current = preferred;
                }
                return QueueState.Dequeue;
            }
        }

        // IPC and local requests share one atomic duplicate/capacity decision.
        public bool TryEnqueue(BuffEntry entry, out string error)
        {
            lock (_sync)
            {
                var entries = AllEntries;
                error = entry == null || entry.NanoEntry == null || entry.Requester == Identity.None
                    ? "Invalid buff request."
                    : entries.Any(e => e.Equals(entry)) ? "That buff is already queued."
                    : entries.Count(e => e.Requester == entry.Requester) >= 32 ? "You already have 32 buffs queued. Please wait."
                    : entries.Length >= 256 ? "This buffer's queue is full. Please try again shortly." : null;
                if (error != null) return false;
                _queue.Enqueue(entry);
                return true;
            }
        }

        internal bool TryEnqueuePaid(BuffEntry[] requested, out string error, int capacity = 4)
        {
            lock (_sync)
            {
                var entries = AllEntries;
                error = requested.Any(r => entries.Any(e => e.Equals(r))) ? "That buff is already queued." :
                    entries.Length + requested.Length > capacity ? "The paid buffer has a full batch; please try again after it finishes." : null;
                if (error != null) return false;
                foreach (var entry in requested) _queue.Enqueue(entry);
                return true;
            }
        }

        internal int[] RemoveQueuedTeamBuff(int nanoId, int[] recipients)
        {
            lock (_sync)
            {
                var served = _queue.Where(e => e.NanoEntry.ContainsId(nanoId) &&
                    recipients.Contains(e.Requester.Instance)).Select(e => e.Requester.Instance).Distinct().ToArray();
                var keep = _queue.Where(e => !e.NanoEntry.ContainsId(nanoId) ||
                    !recipients.Contains(e.Requester.Instance)).ToArray();
                _queue.Clear();
                foreach (var entry in keep) _queue.Enqueue(entry);
                _deferred.RemoveWhere(e => e.NanoEntry.ContainsId(nanoId) && recipients.Contains(e.Requester.Instance));
                return served;
            }
        }

        // Waiting for Manager is still admitted work (capacity, deduplication and
        // paid-session activity), but must not monopolize the active caster.
        internal void YieldCurrent(bool awaitingObservation)
        {
            lock (_sync)
            {
                if (_current == null) return;
                if (awaitingObservation) _awaiting.Add(_current);
                else
                {
                    _deferred.Add(_current);
                    _queue.Enqueue(_current);
                }
                _current = null;
            }
        }

        internal void RetryAtTail(BuffEntry entry)
        {
            lock (_sync)
                if (_awaiting.Remove(entry))
                {
                    _deferred.Add(entry);
                    _queue.Enqueue(entry);
                }
        }

        internal void AwaitConfirmation(BuffEntry entry)
        {
            lock (_sync)
            {
                Remove(entry);
                _awaiting.Add(entry);
            }
        }

        internal void Remove(BuffEntry entry)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_current, entry)) _current = null;
                _awaiting.Remove(entry);
                _deferred.Remove(entry);
                var keep = _queue.Where(e => !ReferenceEquals(e, entry)).ToArray();
                _queue.Clear();
                foreach (var item in keep) _queue.Enqueue(item);
            }
        }

        internal void ClearCurrent() { lock (_sync) { if (_current != null) _deferred.Remove(_current); _current = null; } }
        internal void Clear() { lock (_sync) { _queue.Clear(); _awaiting.Clear(); _deferred.Clear(); _current = null; } }
    }

    public enum QueueState { Current, Empty, Dequeue }
}
