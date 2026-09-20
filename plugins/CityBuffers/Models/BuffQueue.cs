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
        public BuffEntry Current { get { lock (_sync) return _current; } }
        public BuffEntry[] AllEntries
        {
            get { lock (_sync) return _current != null ? _queue.Concat(new[] { _current }).ToArray() : _queue.ToArray(); }
        }

        public QueueState Process()
        {
            lock (_sync)
            {
                if (_current != null) return QueueState.Current;
                if (_queue.Count == 0) return QueueState.Empty;
                _current = _queue.Dequeue();
                return QueueState.Dequeue;
            }
        }

        // IPC and local requests share one atomic duplicate/capacity decision.
        public bool TryEnqueue(BuffEntry entry, out string error)
        {
            lock (_sync)
            {
                var entries = _current != null ? _queue.Concat(new[] { _current }).ToArray() : _queue.ToArray();
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

        internal void ClearCurrent() { lock (_sync) _current = null; }
        internal void Clear() { lock (_sync) { _queue.Clear(); _current = null; } }
    }

    public enum QueueState { Current, Empty, Dequeue }
}
