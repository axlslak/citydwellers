using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MalisBuffBots
{
    public class BuffQueue
    {
        public BuffEntry[] AllEntries => (Current != null) ? _queue.Concat(new[] { Current }).ToArray() : _queue.ToArray();
        public BuffEntry Current { get; private set; }

        private Queue<BuffEntry> _queue = new Queue<BuffEntry>();

        public QueueState Process()
        {
            if (Current != null)
                return QueueState.Current;

            if (_queue.Count == 0)
                return QueueState.Empty;
            else
            {
                Current = _queue.Dequeue();
                return QueueState.Dequeue;
            }
        }

        public void Enqueue(BuffEntry entry) => _queue.Enqueue(entry);

        internal void ClearCurrent() => Current = null;

        internal void Clear()
        {
            _queue.Clear();
            Current = null;
        }
    }

    public enum QueueState
    {
        Current,
        Empty,
        Dequeue
    }
}
