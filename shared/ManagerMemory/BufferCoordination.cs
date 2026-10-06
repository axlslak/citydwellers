using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class BufferMemorySignal
    {
        public string Id;
        public string Kind;
        public string Payload;
        public DateTime CreatedUtc;

        internal BufferMemorySignal Copy() => (BufferMemorySignal)MemberwiseClone();
    }

    [Serializable]
    public sealed class BufferPublicCommandRequest
    {
        public string Id;
        public string Command;
        public string TargetCharacter;
        public uint SenderId;
        public string SenderName;
        public string[] Arguments;
        public int ObservedNanoId; // Nonzero only for Manager-observed automatic refresh.
        public DateTime CreatedUtc;
        public string ClaimedBy;
        public DateTime? ClaimedUtc;

        internal BufferPublicCommandRequest Copy()
        {
            var copy = (BufferPublicCommandRequest)MemberwiseClone();
            copy.Arguments = Arguments == null ? null : (string[])Arguments.Clone();
            return copy;
        }
    }

    [Serializable]
    public sealed class BufferPublicCommandOutcome
    {
        public string Id;
        public bool Success;
        public string Message;
        public string[] Tags;
        public string ClaimedBy;
        public DateTime CompletedUtc;

        internal BufferPublicCommandOutcome Copy()
        {
            var copy = (BufferPublicCommandOutcome)MemberwiseClone();
            copy.Tags = Tags == null ? null : (string[])Tags.Clone();
            return copy;
        }
    }

    public sealed partial class ManagerMemory
    {
        private const int BufferSignalLimit = 256;
        private const int BufferPublicCommandLimit = 128;
        private sealed class BufferCastWatch
        {
            internal uint Requester;
            internal string Tag;
            internal bool Finished;
            internal DateTime ExpiresUtc;
        }
        private readonly Dictionary<string, BufferCastWatch> _bufferCastWatches =
            new Dictionary<string, BufferCastWatch>();

        public string WatchBufferCast(uint requester, string tag)
        {
            lock (_bufferPublicCommandSync)
            {
                foreach (var id in _bufferCastWatches.Where(p => p.Value.ExpiresUtc < DateTime.UtcNow)
                    .Select(p => p.Key).ToArray()) _bufferCastWatches.Remove(id);
                string idNew = Guid.NewGuid().ToString("N");
                _bufferCastWatches.Add(idNew, new BufferCastWatch { Requester = requester, Tag = tag,
                    ExpiresUtc = DateTime.UtcNow.AddMinutes(5) });
                return idNew;
            }
        }

        public void ReportBufferCastFinished(uint requester, string[] tags)
        {
            lock (_bufferPublicCommandSync)
            {
                foreach (var watch in _bufferCastWatches.Values.Where(w => w.Requester == requester &&
                    (tags ?? new string[0]).Contains(w.Tag, StringComparer.OrdinalIgnoreCase)))
                    watch.Finished = true;
                Monitor.PulseAll(_bufferPublicCommandSync);
            }
        }

        public bool WaitForBufferCast(string id, int timeoutMilliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            lock (_bufferPublicCommandSync)
            {
                BufferCastWatch watch;
                while (_bufferCastWatches.TryGetValue(id, out watch))
                {
                    if (watch.Finished) return true;
                    int remaining = timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds;
                    if (remaining <= 0) return false;
                    Monitor.Wait(_bufferPublicCommandSync, remaining);
                }
                return false;
            }
        }

        public void FinishBufferCastWatch(string id)
        {
            lock (_bufferPublicCommandSync) _bufferCastWatches.Remove(id);
        }
        private readonly Dictionary<string, Queue<BufferMemorySignal>> _bufferSignals =
            new Dictionary<string, Queue<BufferMemorySignal>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _bufferPublicCommandSync = new object();
        private readonly Dictionary<string, BufferPublicCommandRequest> _bufferPublicCommands =
            new Dictionary<string, BufferPublicCommandRequest>(StringComparer.Ordinal);
        private readonly Dictionary<string, BufferPublicCommandOutcome> _bufferPublicOutcomes =
            new Dictionary<string, BufferPublicCommandOutcome>(StringComparer.Ordinal);

        public void PublishBufferQueue(string character, int profession, string queueJson)
        {
            if (string.IsNullOrWhiteSpace(character))
                throw new ArgumentException("Buffer character required.");
            lock (_bufferBotSync)
            {
                BufferBotInfo info;
                if (!_bufferBots.TryGetValue(character, out info))
                {
                    info = new BufferBotInfo { Character = character, Profession = profession };
                    _bufferBots.Add(character, info);
                }
                info.Profession = profession;
                info.QueueJson = queueJson ?? "[]";
                info.QueueObservedUtc = DateTime.UtcNow;
            }
        }

        public bool EnqueueBufferSignalForProfession(int profession, string kind, string payload)
        {
            if (string.IsNullOrWhiteSpace(kind)) return false;
            string character = null;
            lock (_bufferBotSync)
            {
                DateTime freshAfter = DateTime.UtcNow.AddSeconds(-5);
                BufferBotInfo target = _bufferBots.Values
                    .Where(x => !IsPaidBuffer(x.Character) && x.Profession == profession && x.InPlay && x.Ready &&
                                x.ObservedUtc >= freshAfter)
                    .OrderBy(x => x.Character, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                character = target?.Character;
                if (character == null) return false;

                Queue<BufferMemorySignal> queue;
                if (!_bufferSignals.TryGetValue(character, out queue))
                    _bufferSignals.Add(character, queue = new Queue<BufferMemorySignal>());
                if (queue.Count >= BufferSignalLimit) return false;
                queue.Enqueue(new BufferMemorySignal
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = kind,
                    Payload = payload,
                    CreatedUtc = DateTime.UtcNow
                });
                return true;
            }
        }

        public bool ReturnBufferSignal(string character, BufferMemorySignal signal)
        {
            lock (_bufferBotSync)
            {
                Queue<BufferMemorySignal> queue;
                if (!_bufferSignals.TryGetValue(character, out queue))
                    _bufferSignals.Add(character, queue = new Queue<BufferMemorySignal>());
                if (queue.Any(s => s.Id == signal.Id)) return true;
                if (queue.Count >= BufferSignalLimit) return false;
                queue.Enqueue(signal.Copy());
                return true;
            }
        }

        private sealed class ObservedBuffAssignment
        {
            internal string Character, Id, LastCharacter;
        }
        private readonly Dictionary<string, ObservedBuffAssignment> _observedBuffAssignments =
            new Dictionary<string, ObservedBuffAssignment>();
        private readonly Queue<string> _bufferDiagnostics = new Queue<string>();

        public void ReportBufferDiagnostic(string message)
        {
            lock (_bufferBotSync)
            {
                // Console already has the complete record; bound pending dev output.
                if (_bufferDiagnostics.Count >= 256) _bufferDiagnostics.Dequeue();
                _bufferDiagnostics.Enqueue(message);
            }
        }

        public string[] TakeBufferDiagnostics()
        {
            lock (_bufferBotSync)
            {
                var result = _bufferDiagnostics.ToArray();
                _bufferDiagnostics.Clear();
                return result;
            }
        }

        public void PruneObservedBuffAssignments(string[] visibleKeys)
        {
            var keys = new HashSet<string>(visibleKeys);
            lock (_bufferBotSync)
                foreach (var key in _observedBuffAssignments.Keys.Where(k => !keys.Contains(k)).ToArray())
                    _observedBuffAssignments.Remove(key);
        }

        public bool HasObservedBuffAssignment(int requester, int nanoId)
        {
            lock (_bufferBotSync)
            {
                ObservedBuffAssignment assignment;
                if (!_observedBuffAssignments.TryGetValue(requester + ":" + nanoId, out assignment) ||
                    assignment.Character == null) return false;
                BufferBotInfo bot;
                if (_bufferBots.TryGetValue(assignment.Character, out bot) && bot.Ready && bot.InPlay &&
                    bot.ObservedUtc >= DateTime.UtcNow.AddSeconds(-5)) return true;
                assignment.Character = assignment.Id = null;
                return false;
            }
        }

        public string LastObservedBuffProvider(int requester, int nanoId)
        {
            lock (_bufferBotSync)
            {
                ObservedBuffAssignment assignment;
                return _observedBuffAssignments.TryGetValue(requester + ":" + nanoId, out assignment)
                    ? assignment.LastCharacter : null;
            }
        }

        public int PendingObservedBuffCount(string character)
        {
            lock (_bufferBotSync)
                return _observedBuffAssignments.Values.Count(a =>
                    string.Equals(a.Character, character, StringComparison.OrdinalIgnoreCase));
        }

        public bool OwnsObservedBuffRefresh(string character, int requester, int nanoId, string id)
        {
            lock (_bufferBotSync)
            {
                ObservedBuffAssignment assignment;
                return _observedBuffAssignments.TryGetValue(requester + ":" + nanoId, out assignment) &&
                    assignment.Id == id && string.Equals(assignment.Character, character, StringComparison.OrdinalIgnoreCase);
            }
        }

        public void ReleaseObservedBuffRefresh(string character, int requester, int nanoId, string id)
        {
            lock (_bufferBotSync)
            {
                ObservedBuffAssignment assignment;
                if (_observedBuffAssignments.TryGetValue(requester + ":" + nanoId, out assignment) &&
                    assignment.Id == id && string.Equals(assignment.Character, character, StringComparison.OrdinalIgnoreCase))
                    assignment.Character = assignment.Id = null;
            }
        }

        public bool RequestObservedBuffRefresh(string character, int requester, int nanoId)
        {
            if (!ObservedBuffNeedsRefresh(requester, nanoId)) return false;
            lock (_bufferBotSync)
            {
                BufferBotInfo bot;
                if (!_bufferBots.TryGetValue(character, out bot) || IsPaidBuffer(character) ||
                    !bot.Ready || !bot.InPlay || bot.ObservedUtc < DateTime.UtcNow.AddSeconds(-5) ||
                    !(bot.AdvertisedBuffs ?? new BufferAdvertisedBuff[0]).Any(b =>
                        b.Type == "Single" && (b.NanoIds ?? new int[0]).Contains(nanoId))) return false;
                string key = requester + ":" + nanoId;
                ObservedBuffAssignment assignment;
                if (_observedBuffAssignments.TryGetValue(key, out assignment) && assignment.Character != null)
                    return false;
                Queue<BufferMemorySignal> queue;
                if (!_bufferSignals.TryGetValue(character, out queue))
                    _bufferSignals.Add(character, queue = new Queue<BufferMemorySignal>());
                if (queue.Count >= BufferSignalLimit) return false;
                string id = Guid.NewGuid().ToString("N");
                _observedBuffAssignments[key] = new ObservedBuffAssignment {
                    Character = character, LastCharacter = character, Id = id };
                queue.Enqueue(new BufferMemorySignal { Id = id,
                    Kind = "observer-refresh", Payload = key, CreatedUtc = DateTime.UtcNow });
                return true;
            }
        }

        public List<BufferMemorySignal> TakeBufferSignals(string character, int maximum)
        {
            var result = new List<BufferMemorySignal>();
            if (string.IsNullOrWhiteSpace(character) || maximum <= 0) return result;
            lock (_bufferBotSync)
            {
                Queue<BufferMemorySignal> queue;
                if (!_bufferSignals.TryGetValue(character, out queue)) return result;
                while (result.Count < maximum && queue.Count > 0)
                    result.Add(queue.Dequeue().Copy());
                if (queue.Count == 0) _bufferSignals.Remove(character);
            }
            return result;
        }

        public bool BeginBufferPublicCommand(BufferPublicCommandRequest request)
        {
            if (request == null ||
                string.IsNullOrWhiteSpace(request.Id) ||
                string.IsNullOrWhiteSpace(request.Command) ||
                request.SenderId == 0)
                return false;

            lock (_bufferPublicCommandSync)
            {
                PruneBufferPublicCommands(DateTime.UtcNow);
                if (_bufferPublicCommands.Count >= BufferPublicCommandLimit ||
                    _bufferPublicCommands.ContainsKey(request.Id) ||
                    _bufferPublicOutcomes.ContainsKey(request.Id))
                    return false;

                var paid = PaidBufferLocked(request.TargetCharacter);
                if (!string.IsNullOrWhiteSpace(request.TargetCharacter) &&
                    (paid == null || paid.Blocked || DateTime.UtcNow < paid.RetryAfterUtc)) return false;

                BufferPublicCommandRequest copy = request.Copy();
                if (copy.CreatedUtc == default(DateTime))
                    copy.CreatedUtc = DateTime.UtcNow;
                copy.ClaimedBy = null;
                copy.ClaimedUtc = null;
                _bufferPublicCommands.Add(copy.Id, copy);
                Monitor.PulseAll(_bufferPublicCommandSync);
                return true;
            }
        }

        public List<BufferPublicCommandRequest> PendingBufferPublicCommands(int maximum)
        {
            var result = new List<BufferPublicCommandRequest>();
            if (maximum <= 0) return result;

            lock (_bufferPublicCommandSync)
            {
                DateTime now = DateTime.UtcNow;
                PruneBufferPublicCommands(now);
                foreach (BufferPublicCommandRequest request in _bufferPublicCommands.Values
                    .Where(item => string.IsNullOrWhiteSpace(item.ClaimedBy))
                    .OrderBy(item => item.CreatedUtc)
                    .Take(maximum))
                {
                    result.Add(request.Copy());
                }
            }

            return result;
        }

        public bool TryClaimBufferPublicCommand(string id, string character)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(character))
                return false;

            lock (_bufferPublicCommandSync)
            {
                PruneBufferPublicCommands(DateTime.UtcNow);
                BufferPublicCommandRequest request;
                if (!_bufferPublicCommands.TryGetValue(id, out request) ||
                    !string.IsNullOrWhiteSpace(request.ClaimedBy))
                    return false;

                if (!string.IsNullOrWhiteSpace(request.TargetCharacter))
                {
                    var _paidBuffer = PaidBufferLocked(character);
                    if (!IsPaidBufferLocked(character) ||
                        !string.Equals(request.TargetCharacter, character, StringComparison.OrdinalIgnoreCase) ||
                        !_paidBuffer.Running || _paidBuffer.Blocked || _paidBuffer.Draining || _paidBuffer.Parked) return false;
                    if (PaidBufferCatalogue.UsesManagerTeam(_paidBuffer.Profession) && !PaidFixerMemberLocked(request.SenderId)) return false;
                    _paidBuffer.BusyUtc = DateTime.UtcNow;
                }
                else if (IsPaidBufferLocked(character)) return false;

                request.ClaimedBy = character;
                request.ClaimedUtc = DateTime.UtcNow;
                return true;
            }
        }

        public void CompleteBufferPublicCommand(BufferPublicCommandOutcome outcome)
        {
            if (outcome == null || string.IsNullOrWhiteSpace(outcome.Id))
                return;

            lock (_bufferPublicCommandSync)
            {
                BufferPublicCommandRequest request;
                if (!_bufferPublicCommands.TryGetValue(outcome.Id, out request) ||
                    string.IsNullOrWhiteSpace(request.ClaimedBy))
                    return;

                if (!string.IsNullOrWhiteSpace(outcome.ClaimedBy) &&
                    !string.Equals(
                        outcome.ClaimedBy,
                        request.ClaimedBy,
                        StringComparison.OrdinalIgnoreCase))
                    return;

                BufferPublicCommandOutcome copy = outcome.Copy();
                copy.ClaimedBy = request.ClaimedBy;
                if (copy.CompletedUtc == default(DateTime))
                    copy.CompletedUtc = DateTime.UtcNow;
                _bufferPublicCommands.Remove(outcome.Id);
                _bufferPublicOutcomes[outcome.Id] = copy;
                Monitor.PulseAll(_bufferPublicCommandSync);
            }
        }

        public BufferPublicCommandOutcome WaitForBufferPublicCommandOutcome(
            string id,
            int timeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            int timeout = Math.Max(0, timeoutMilliseconds);
            var elapsed = Stopwatch.StartNew();

            lock (_bufferPublicCommandSync)
            {
                BufferPublicCommandOutcome outcome;
                while (!_bufferPublicOutcomes.TryGetValue(id, out outcome))
                {
                    int remaining = timeout -
                        (int)Math.Min(int.MaxValue, elapsed.ElapsedMilliseconds);
                    if (remaining <= 0)
                    {
                        _bufferPublicCommands.Remove(id);
                        return null;
                    }
                    Monitor.Wait(_bufferPublicCommandSync, remaining);
                }

                return outcome.Copy();
            }
        }

        public bool ObservedPaidRefreshPending(string id, string character)
        {
            lock (_bufferPublicCommandSync)
            {
                if (_bufferPublicCommands.ContainsKey(id)) return true;
                var paid = PaidBufferLocked(character);
                if (paid != null && paid.Running && !paid.Parked) return true;
                _bufferPublicOutcomes.Remove(id);
                return false;
            }
        }

        public void CancelUnclaimedObservedBufferCommand(string id)
        {
            lock (_bufferPublicCommandSync)
            {
                BufferPublicCommandRequest request;
                if (_bufferPublicCommands.TryGetValue(id, out request) && request.ObservedNanoId > 0 &&
                    string.IsNullOrWhiteSpace(request.ClaimedBy))
                    _bufferPublicCommands.Remove(id);
            }
        }

        public void FinishBufferPublicCommand(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            lock (_bufferPublicCommandSync)
            {
                _bufferPublicCommands.Remove(id);
                _bufferPublicOutcomes.Remove(id);
            }
        }

        private void PruneBufferPublicCommands(DateTime now)
        {
            DateTime requestCutoff = now.AddSeconds(-125);
            DateTime outcomeCutoff = now.AddMinutes(-1);

            foreach (string id in _bufferPublicCommands
                .Where(pair => pair.Value.CreatedUtc < (string.IsNullOrWhiteSpace(pair.Value.TargetCharacter)
                    ? requestCutoff : now.AddSeconds(-125)))
                .Select(pair => pair.Key)
                .ToArray())
            {
                _bufferPublicCommands.Remove(id);
            }

            foreach (string id in _bufferPublicOutcomes
                .Where(pair => pair.Value.CompletedUtc < outcomeCutoff)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _bufferPublicOutcomes.Remove(id);
            }
        }

        internal void ClearBufferSignals(string character)
        {
            if (string.IsNullOrWhiteSpace(character)) return;
            lock (_bufferBotSync) _bufferSignals.Remove(character);
        }

    }
}




