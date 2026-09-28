using System;
using System.Collections.Generic;
using System.Linq;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class PaidBufferSession
    {
        public string Character;
        public bool Running, Ready, Draining, Parked, Blocked;
        public DateTime StartedUtc, ObservedUtc, BusyUtc, RetryAfterUtc;
        internal PaidBufferSession Copy() => (PaidBufferSession)MemberwiseClone();
    }

    public sealed partial class ManagerMemory
    {
        // Account names never leave this in-memory arbiter or enter diagnostics.
        private readonly object _aoAccountSync = new object();
        private readonly Dictionary<string, string> _aoAccountOwners =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _aoAccountWaiters =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private PaidBufferSession _paidBuffer;

        public bool TryAcquireAoAccount(string account, string owner, bool priority)
        {
            lock (_aoAccountSync)
            {
                string current, waiter;
                if (priority) _aoAccountWaiters[account] = owner;
                if (_aoAccountOwners.TryGetValue(account, out current)) return current == owner;
                if (_aoAccountWaiters.TryGetValue(account, out waiter) && waiter != owner) return false;
                _aoAccountOwners.Add(account, owner);
                if (waiter == owner) _aoAccountWaiters.Remove(account);
                return true;
            }
        }

        public bool AoAccountHasWaiter(string account)
        {
            lock (_aoAccountSync) return _aoAccountWaiters.ContainsKey(account);
        }

        public void CancelAoAccountWait(string account, string owner)
        {
            lock (_aoAccountSync)
            {
                string current;
                if (_aoAccountWaiters.TryGetValue(account, out current) && current == owner)
                    _aoAccountWaiters.Remove(account);
            }
        }

        public void ReleaseAoAccount(string account, string owner)
        {
            lock (_aoAccountSync)
            {
                string current;
                if (_aoAccountOwners.TryGetValue(account, out current) && current == owner)
                    _aoAccountOwners.Remove(account);
            }
        }

        public static bool IsPaidFixerTag(string tag)
        {
            return string.Equals(tag, "lu", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "fsc", StringComparison.OrdinalIgnoreCase);
        }

        public void RegisterPaidBuffer(string character)
        {
            lock (_bufferPublicCommandSync)
            {
                // A failed unload must not reset a live session or its account lock.
                if (_paidBuffer != null && _paidBuffer.Running)
                    throw new InvalidOperationException("Previous paid buffer session has not unloaded.");
                _paidBuffer = string.IsNullOrWhiteSpace(character) ? null :
                    new PaidBufferSession { Character = character };
            }
        }

        public PaidBufferSession ReadPaidBuffer()
        {
            lock (_bufferPublicCommandSync) return _paidBuffer?.Copy();
        }

        public bool IsPaidBuffer(string character)
        {
            lock (_bufferPublicCommandSync) return IsPaidBufferLocked(character);
        }

        private bool IsPaidBufferLocked(string character) => _paidBuffer != null &&
            string.Equals(_paidBuffer.Character, character, StringComparison.OrdinalIgnoreCase);

        private bool HasPaidRequestsLocked() => _paidBuffer != null &&
            _bufferPublicCommands.Values.Any(r =>
                string.Equals(r.TargetCharacter, _paidBuffer.Character, StringComparison.OrdinalIgnoreCase));

        public bool HasPaidBufferRequests()
        {
            lock (_bufferPublicCommandSync)
            {
                PruneBufferPublicCommands(DateTime.UtcNow);
                return HasPaidRequestsLocked();
            }
        }

        public void StartPaidBufferSession()
        {
            lock (_bufferPublicCommandSync)
            {
                _paidBuffer.Running = true;
                _paidBuffer.Ready = _paidBuffer.Draining = _paidBuffer.Parked = false;
                _paidBuffer.StartedUtc = _paidBuffer.ObservedUtc = _paidBuffer.BusyUtc = DateTime.UtcNow;
            }
        }

        public void DrainPaidBuffer()
        {
            lock (_bufferPublicCommandSync) if (_paidBuffer != null) _paidBuffer.Draining = true;
        }

        // Called on the AO update thread after command admission. Parking and claiming
        // share the same lock: an idle snapshot cannot race a newly accepted cast.
        public bool UpdatePaidBufferActivity(string character, bool ready, bool busy)
        {
            lock (_bufferPublicCommandSync)
            {
                if (!IsPaidBufferLocked(character) || !_paidBuffer.Running) return false;
                var now = DateTime.UtcNow;
                _paidBuffer.ObservedUtc = now;
                _paidBuffer.Ready = ready;
                PruneBufferPublicCommands(now);
                if (now - _paidBuffer.StartedUtc >= TimeSpan.FromSeconds(90))
                {
                    _paidBuffer.Draining = true;
                    FailPaidRequestsLocked("The paid buffer could not accept your request during this session. Stand near it and retry after it logs out.");
                }
                if (busy || (!_paidBuffer.Draining && HasPaidRequestsLocked())) _paidBuffer.BusyUtc = now;
                if (ready && !busy && (_paidBuffer.Draining ||
                    now - _paidBuffer.BusyUtc >= TimeSpan.FromSeconds(20)))
                    _paidBuffer.Parked = _paidBuffer.Draining = true;
                return _paidBuffer.Parked;
            }
        }

        public void StopPaidBufferSession(bool failed, string message)
        {
            lock (_bufferPublicCommandSync)
            {
                if (_paidBuffer == null) return;
                _paidBuffer.Running = _paidBuffer.Ready = false;
                _paidBuffer.RetryAfterUtc = DateTime.UtcNow.AddSeconds(failed ? 60 : 0);
                if (failed) FailPaidRequestsLocked(message);
            }
        }

        public void FailPaidBufferRequests(string message)
        {
            lock (_bufferPublicCommandSync) FailPaidRequestsLocked(message);
        }

        public void BlockPaidBuffer(string message)
        {
            lock (_bufferPublicCommandSync)
            {
                if (_paidBuffer == null) return;
                _paidBuffer.Blocked = _paidBuffer.Draining = true;
                FailPaidRequestsLocked(message);
            }
        }

        private void FailPaidRequestsLocked(string message)
        {
            if (_paidBuffer == null) return;
            foreach (var r in _bufferPublicCommands.Values.Where(r =>
                string.Equals(r.TargetCharacter, _paidBuffer.Character, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _bufferPublicCommands.Remove(r.Id);
                _bufferPublicOutcomes[r.Id] = new BufferPublicCommandOutcome {
                    Id = r.Id, Success = false, Message = message, CompletedUtc = DateTime.UtcNow
                };
            }
            System.Threading.Monitor.PulseAll(_bufferPublicCommandSync);
        }
    }
}
