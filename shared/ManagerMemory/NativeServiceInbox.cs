using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace CityDwellers.Shared
{
    public sealed partial class ManagerMemory
    {
        private readonly object _nativeServiceSync = new object();
        private readonly Queue<string> _nativeServiceMessages = new Queue<string>();
        private string _nativeServiceConsumer;

        // Native server text only, collected once by Manager. Unlike disposable
        // telemetry this inbox never evicts an unpersisted observation. Payloads
        // are already bounded by the AO packet, not a public command input.
        public void EnqueueNativeServiceMessage(string payload)
        {
            if (string.IsNullOrEmpty(payload)) throw new ArgumentException("Native service payload is empty.");
            lock (_nativeServiceSync)
            {
                _nativeServiceMessages.Enqueue(payload);
                Monitor.PulseAll(_nativeServiceSync);
            }
        }

        public string BeginNativeServiceConsumer()
        {
            lock (_nativeServiceSync)
            {
                _nativeServiceConsumer = Guid.NewGuid().ToString("N");
                Monitor.PulseAll(_nativeServiceSync);
                return _nativeServiceConsumer;
            }
        }

        public bool IsNativeServiceConsumer(string consumer)
        {
            lock (_nativeServiceSync) return consumer != null && consumer == _nativeServiceConsumer;
        }

        public void EndNativeServiceConsumer(string consumer)
        {
            lock (_nativeServiceSync)
            {
                if (consumer == _nativeServiceConsumer) _nativeServiceConsumer = null;
                Monitor.PulseAll(_nativeServiceSync);
            }
        }

        public string ReadNativeServiceMessage(string consumer, int timeoutMilliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            lock (_nativeServiceSync)
                while (consumer != null && consumer == _nativeServiceConsumer)
                {
                    if (_nativeServiceMessages.Count != 0) return _nativeServiceMessages.Peek();
                    int remaining = timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                    if (remaining <= 0) return null;
                    Monitor.Wait(_nativeServiceSync, remaining);
                }
            return null;
        }

        public void CompleteNativeServiceMessage(string consumer, string payload)
        {
            lock (_nativeServiceSync)
                if (consumer != null && consumer == _nativeServiceConsumer &&
                    _nativeServiceMessages.Count != 0 && _nativeServiceMessages.Peek() == payload)
                    _nativeServiceMessages.Dequeue();
        }
    }
}
