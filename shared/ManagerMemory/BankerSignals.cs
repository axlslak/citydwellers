using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CityDwellers.Shared
{
    // Ephemeral same-host banker request/reply. Durable business state does not live here.
    public sealed class BankerSignalRequest : MarshalByRefObject
    {
        private readonly TaskCompletionSource<string> _reply =
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claim; // 0 queued, 1 claimed by destination AO thread, 2 cancelled before claim

        public BankerSignalRequest(string payload) { Payload = payload; }
        public string Payload { get; private set; }
        public Task<string> ReplyTask => _reply.Task;
        public bool TryBegin() => Interlocked.CompareExchange(ref _claim, 1, 0) == 0;
        public bool TryCancel()
        {
            if (Interlocked.CompareExchange(ref _claim, 2, 0) != 0) return false;
            _reply.TrySetCanceled();
            return true;
        }
        public void Reply(string value) => _reply.TrySetResult(value);
        public override object InitializeLifetimeService() => null;
    }

    public abstract class BankerSignalWake : MarshalByRefObject
    {
        public abstract void Wake();
        public override object InitializeLifetimeService() => null;
    }

    public sealed partial class ManagerMemory
    {
        private const int BankerSignalLimit = 64;
        private readonly object _bankerSignalSync = new object();
        private readonly Dictionary<string, Queue<BankerSignalRequest>> _bankerSignals =
            new Dictionary<string, Queue<BankerSignalRequest>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BankerSignalWake> _bankerSignalWakes =
            new Dictionary<string, BankerSignalWake>(StringComparer.OrdinalIgnoreCase);

        public void RegisterBankerSignalWake(string character, BankerSignalWake wake)
        {
            if (string.IsNullOrWhiteSpace(character) || wake == null) return;
            lock (_bankerSignalSync) _bankerSignalWakes[character] = wake;
        }

        public void UnregisterBankerSignalWake(string character)
        {
            if (string.IsNullOrWhiteSpace(character)) return;
            List<BankerSignalRequest> cancelled = null;
            lock (_bankerSignalSync)
            {
                _bankerSignalWakes.Remove(character);
                Queue<BankerSignalRequest> queue;
                if (_bankerSignals.TryGetValue(character, out queue))
                {
                    cancelled = new List<BankerSignalRequest>(queue);
                    _bankerSignals.Remove(character);
                }
            }
            if (cancelled != null)
                foreach (BankerSignalRequest request in cancelled)
                    try { request.TryCancel(); } catch { }
        }

        public bool EnqueueBankerSignal(string character, BankerSignalRequest request)
        {
            if (string.IsNullOrWhiteSpace(character) || request == null) return false;
            BankerSignalWake wake;
            lock (_bankerSignalSync)
            {
                if (!_bankerSignalWakes.TryGetValue(character, out wake))
                    return false;
                Queue<BankerSignalRequest> queue;
                if (!_bankerSignals.TryGetValue(character, out queue))
                    _bankerSignals.Add(character, queue = new Queue<BankerSignalRequest>());
                if (queue.Count >= BankerSignalLimit) return false;
                queue.Enqueue(request);
            }
            try { wake.Wake(); } catch { }
            return true;
        }

        public BankerSignalRequest TakeBankerSignal(string character)
        {
            if (string.IsNullOrWhiteSpace(character)) return null;
            lock (_bankerSignalSync)
            {
                Queue<BankerSignalRequest> queue;
                if (!_bankerSignals.TryGetValue(character, out queue) || queue.Count == 0) return null;
                BankerSignalRequest request = queue.Dequeue();
                if (queue.Count == 0) _bankerSignals.Remove(character);
                return request;
            }
        }
    }
}
