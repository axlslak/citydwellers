using System;
using System.Collections.Generic;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class FlipperOperation
    {
        public string Id, Action, Result;
        internal FlipperOperation Copy() => (FlipperOperation)MemberwiseClone();
    }
    public sealed partial class ManagerMemory
    {
        private readonly object _serviceSync = new object();
        private FlipperOperation _flipperOperation;
        private string _flipperCancellation;
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
