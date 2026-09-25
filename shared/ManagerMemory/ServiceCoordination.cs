using System;
using System.Collections.Generic;
using System.Threading;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class FlipperOperation
    {
        public string Id, Action, Result;
        internal FlipperOperation Copy() => (FlipperOperation)MemberwiseClone();
    }

    [Serializable]
    public sealed class BufferControlOperation
    {
        public string Id, Action, Character, Result;
        public bool? Success;
        internal BufferControlOperation Copy() => (BufferControlOperation)MemberwiseClone();
    }
    public sealed partial class ManagerMemory
    {
        private readonly object _serviceSync = new object();
        private FlipperOperation _flipperOperation;
        private string _flipperCancellation;
        private BufferControlOperation _bufferControl;
        private readonly HashSet<string> _sleepingBuffers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _bufferAdmins =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _bufferRanked =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _bufferMembers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _bufferWarpers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _bufferAuthorityReady;
        private readonly Dictionary<string, BuddyPositionSnapshot> _buddyPositions = new Dictionary<string, BuddyPositionSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BuddyHomeDirective> _buddyHomes = new Dictionary<string, BuddyHomeDirective>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _readyBuddies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // These are existing IPC message payloads, held only for this host's
        // lifetime. They are never files, database documents or history records.
        private string _raidCoordinator, _orgOutputBudget;
        private readonly Dictionary<string, string> _bankerReports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public void BeginFlipper(string id, string action)
        { lock (_serviceSync) _flipperOperation = new FlipperOperation { Id = id, Action = action }; }
        public FlipperOperation ReadFlipperOperation()
        { lock (_serviceSync) return _flipperOperation?.Copy(); }
        public void PublishFlipperResult(string id, string result)
        {
            lock (_serviceSync)
                if (_flipperOperation != null && _flipperOperation.Id == id) _flipperOperation.Result = result;
        }
        public void CancelFlipper(string id) { lock (_serviceSync) _flipperCancellation = id; }
        public bool FlipperCancelled(string id)
        { lock (_serviceSync) return !string.IsNullOrEmpty(id) && _flipperCancellation == id; }
        public void FinishFlipper(string id)
        {
            lock (_serviceSync)
            {
                if (_flipperOperation?.Id == id) _flipperOperation = null;
                if (_flipperCancellation == id) _flipperCancellation = null;
            }
        }
        // Buffer sleep is deliberately host-lifetime only. Nothing here is persisted;
        // a full City Dwellers restart starts every configured enabled buffer normally.
        public bool BeginBufferControl(string id, string action, string character)
        {
            lock (_serviceSync)
            {
                if (_bufferControl != null) return false;
                _bufferControl = new BufferControlOperation
                {
                    Id = id,
                    Action = action,
                    Character = character
                };
                Monitor.PulseAll(_serviceSync);
                return true;
            }
        }
        public BufferControlOperation WaitForBufferControl(int timeoutMilliseconds)
        {
            lock (_serviceSync)
            {
                if (_bufferControl == null || _bufferControl.Result != null)
                    Monitor.Wait(_serviceSync, Math.Max(0, timeoutMilliseconds));
                return _bufferControl != null && _bufferControl.Result == null
                    ? _bufferControl.Copy()
                    : null;
            }
        }
        public BufferControlOperation WaitForBufferControlResult(string id, int timeoutMilliseconds)
        {
            lock (_serviceSync)
            {
                if (_bufferControl == null ||
                    !string.Equals(_bufferControl.Id, id, StringComparison.Ordinal))
                    return null;
                if (_bufferControl.Result == null)
                    Monitor.Wait(_serviceSync, Math.Max(0, timeoutMilliseconds));
                return _bufferControl != null &&
                       string.Equals(_bufferControl.Id, id, StringComparison.Ordinal)
                    ? _bufferControl.Copy()
                    : null;
            }
        }
        public void PublishBufferControlResult(string id, bool success, string result)
        {
            lock (_serviceSync)
            {
                if (_bufferControl == null ||
                    !string.Equals(_bufferControl.Id, id, StringComparison.Ordinal))
                    return;
                _bufferControl.Success = success;
                _bufferControl.Result = result ?? string.Empty;
                Monitor.PulseAll(_serviceSync);
            }
        }
        public void FinishBufferControl(string id)
        {
            lock (_serviceSync)
            {
                if (_bufferControl != null &&
                    string.Equals(_bufferControl.Id, id, StringComparison.Ordinal))
                {
                    _bufferControl = null;
                    Monitor.PulseAll(_serviceSync);
                }
            }
        }
        public void SetBufferSleeping(string character, bool sleeping)
        {
            if (string.IsNullOrWhiteSpace(character)) return;
            lock (_serviceSync)
            {
                if (sleeping) _sleepingBuffers.Add(character);
                else _sleepingBuffers.Remove(character);
            }
        }
        public bool BufferSleeping(string character)
        {
            lock (_serviceSync)
                return !string.IsNullOrWhiteSpace(character) && _sleepingBuffers.Contains(character);
        }

        // Mali keeps its existing rank seam; CityManager owns the authority source.
        public void PublishBufferAuthority(
            string[] admins,
            string[] ranked,
            string[] members,
            string[] warpers)
        {
            lock (_serviceSync)
            {
                ReplaceNames(_bufferAdmins, admins);
                ReplaceNames(_bufferRanked, ranked);
                ReplaceNames(_bufferMembers, members);
                ReplaceNames(_bufferWarpers, warpers);
                _bufferAuthorityReady = true;
            }
        }
        public bool BufferAuthorityReady()
        { lock (_serviceSync) return _bufferAuthorityReady; }
        public bool BufferIsAdmin(string character)
        { lock (_serviceSync) return _bufferAuthorityReady && HasName(_bufferAdmins, character); }
        public bool BufferIsRanked(string character)
        { lock (_serviceSync) return _bufferAuthorityReady && HasName(_bufferRanked, character); }
        public bool BufferIsMember(string character)
        { lock (_serviceSync) return _bufferAuthorityReady && HasName(_bufferMembers, character); }
        public bool BufferIsWarper(string character)
        { lock (_serviceSync) return _bufferAuthorityReady && HasName(_bufferWarpers, character); }
        private static bool HasName(HashSet<string> names, string character) =>
            !string.IsNullOrWhiteSpace(character) && names.Contains(character);
        private static void ReplaceNames(HashSet<string> target, IEnumerable<string> source)
        {
            target.Clear();
            foreach (string name in source ?? new string[0])
                if (!string.IsNullOrWhiteSpace(name))
                    target.Add(name.Trim());
        }

        public void PublishBuddyPosition(string character, BuddyPositionSnapshot snapshot)
        {
            lock (_serviceSync)
                if (snapshot == null) _buddyPositions.Remove(character); else _buddyPositions[character] = snapshot.Copy();
        }
        public BuddyPositionSnapshot ReadBuddyPosition(string character)
        { lock (_serviceSync) { BuddyPositionSnapshot value; return _buddyPositions.TryGetValue(character, out value) ? value.Copy() : null; } }
        public void SetBuddyHome(string character, BuddyHomeDirective directive)
        {
            lock (_serviceSync)
                if (directive == null) _buddyHomes.Remove(character); else _buddyHomes[character] = directive.Copy();
        }
        public BuddyHomeDirective ReadBuddyHome(string character)
        { lock (_serviceSync) { BuddyHomeDirective value; return _buddyHomes.TryGetValue(character, out value) ? value.Copy() : null; } }
        public void SetBuddyReady(string character, bool ready)
        { lock (_serviceSync) { if (ready) _readyBuddies.Add(character); else _readyBuddies.Remove(character); } }
        public bool BuddyReady(string character) { lock (_serviceSync) return _readyBuddies.Contains(character); }
        public string ReadRaidCoordinator() { lock (_serviceSync) return _raidCoordinator; }
        public void SetRaidCoordinator(string value) { lock (_serviceSync) _raidCoordinator = value; }
        public string ReadOrgOutputBudget() { lock (_serviceSync) return _orgOutputBudget; }
        public void SetOrgOutputBudget(string value) { lock (_serviceSync) _orgOutputBudget = value; }
        public bool RequestBankerReport(string character, string request)
        {
            lock (_serviceSync)
            {
                if (_bankerReports.ContainsKey(character)) return false;
                _bankerReports.Add(character, request); return true;
            }
        }
        public string ReadBankerReport(string character)
        { lock (_serviceSync) { string value; return _bankerReports.TryGetValue(character, out value) ? value : null; } }
        public void FinishBankerReport(string character) { lock (_serviceSync) _bankerReports.Remove(character); }
    }
}
