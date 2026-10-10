using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace CityDwellers.Shared
{
    public sealed partial class ManagerMemory
    {
        private readonly object _serviceEventSync = new object();
        private readonly Queue<string> _serviceEvents = new Queue<string>();
        private string _serviceEventConsumer;

        // Immutable payloads cross AppDomains without retaining client callbacks.
        public bool EnqueueServiceEvent(string payload)
        {
            if (string.IsNullOrEmpty(payload) || payload.Length > 60000) return false;
            lock (_serviceEventSync)
            {
                if (_serviceEvents.Count >= 256) return false;
                _serviceEvents.Enqueue(payload);
                Monitor.PulseAll(_serviceEventSync);
                return true;
            }
        }

        public string BeginServiceEventConsumer()
        {
            lock (_serviceEventSync)
            {
                _serviceEventConsumer = Guid.NewGuid().ToString("N");
                Monitor.PulseAll(_serviceEventSync);
                return _serviceEventConsumer;
            }
        }

        public bool IsServiceEventConsumer(string consumer)
        {
            lock (_serviceEventSync)
                return consumer != null && consumer == _serviceEventConsumer;
        }

        public void EndServiceEventConsumer(string consumer)
        {
            lock (_serviceEventSync)
            {
                if (consumer == _serviceEventConsumer) _serviceEventConsumer = null;
                Monitor.PulseAll(_serviceEventSync);
            }
        }

        public string ReadServiceEvent(string consumer, int timeoutMilliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            lock (_serviceEventSync)
            {
                while (consumer != null && consumer == _serviceEventConsumer)
                {
                    if (_serviceEvents.Count != 0) return _serviceEvents.Peek();
                    int remaining = timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                    if (remaining <= 0) return null;
                    Monitor.Wait(_serviceEventSync, remaining);
                }
                return null;
            }
        }

        public void CompleteServiceEvent(string consumer, string payload)
        {
            lock (_serviceEventSync)
                if (consumer != null && consumer == _serviceEventConsumer &&
                    _serviceEvents.Count != 0 && _serviceEvents.Peek() == payload)
                    _serviceEvents.Dequeue();
        }
    }
}
