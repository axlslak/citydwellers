using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CityDwellers.Shared
{
    [Serializable]
    public sealed class WorkerRequest
    {
        public string Id;
        public string Command;
        public DateTime? NotBeforeUtc;
        public int? TimeoutSeconds;
        public int? Level;
        public int? Index;
        public List<int> Indexes;
        public string Purpose;
        public int? LeaseSeconds;
        public bool Home;
        public bool LogoutAfterHome;
        public string HomeJobId;
    }

    [Serializable]
    public sealed class WorkerResponse
    {
        public string Id;
        public bool Ok;
        public string Message;
        public string Character;
        public string CloakState;
        public int? ShieldTimerInSeconds;
        public float? ControllerCharge;
        public int? Level;
        public int? Index;
        public List<string> Characters;
        public List<int> Indexes;
        public int? Count;
        public bool Cached;
        public DateTime? ObservedUtc;
        public bool ActionSent;
        public List<BuddyPositionSnapshot> Positions;
        public string HomeJobId;
        public bool HomeRunning;
        public int HomeAttempted;
        public int HomeStarted;
        public int HomeTerminal;
        public int HomeReached;
        public int HomeStopped;
        public List<string> HomeFailures;
    }

    [Serializable]
    public sealed class ComponentStatus
    {
        public string Name;
        public string Phase;
        public int Generation;
        public DateTime? StartedUtc;
        public DateTime? StoppedUtc;
        public int? ExitCode;
        public string Reason;
        public int ConsecutiveFailures;
        public DateTime ObservedUtc;

        internal ComponentStatus Copy() => (ComponentStatus)MemberwiseClone();
    }

    [Serializable]
    public sealed class LifecycleRequest
    {
        public string Id;
        public string Target;
        public string Verb;
        public string Payload;
        public DateTime RequestedUtc;
        public string Requester;

        internal LifecycleRequest Copy() => (LifecycleRequest)MemberwiseClone();
    }

    [Serializable]
    public sealed class LifecycleCommand
    {
        public string RequestId;
        public string CommandId;
        public string Component;
        public string Verb;
        public string Payload;
        public int Generation;
        public DateTime IssuedUtc;

        internal LifecycleCommand Copy() => (LifecycleCommand)MemberwiseClone();
    }

    [Serializable]
    public sealed class LifecycleOutcome
    {
        public string RequestId;
        public string CommandId;
        public bool Ok;
        public string Payload;
        public string Message;
        public DateTime CompletedUtc;

        internal LifecycleOutcome Copy() => (LifecycleOutcome)MemberwiseClone();
    }

    public sealed partial class ManagerMemory
    {
        private readonly object _governanceSync = new object();
        private readonly Dictionary<string, ComponentStatus> _componentStatus =
            new Dictionary<string, ComponentStatus>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<LifecycleRequest> _lifecycleRequests = new Queue<LifecycleRequest>();
        private readonly Dictionary<string, LifecycleCommand> _lifecycleCommands =
            new Dictionary<string, LifecycleCommand>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LifecycleOutcome> _lifecycleOutcomes =
            new Dictionary<string, LifecycleOutcome>(StringComparer.Ordinal);

        public void PublishComponentStatus(ComponentStatus status)
        {
            if (status == null || string.IsNullOrWhiteSpace(status.Name))
                throw new ArgumentException("Component name required.");
            lock (_governanceSync)
            {
                ComponentStatus copy = status.Copy();
                copy.ObservedUtc = DateTime.UtcNow;
                _componentStatus[copy.Name] = copy;
                Monitor.PulseAll(_governanceSync);
            }
        }

        public ComponentStatus ReadComponentStatus(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            lock (_governanceSync)
            {
                ComponentStatus value;
                return _componentStatus.TryGetValue(name, out value) ? value.Copy() : null;
            }
        }

        public List<ComponentStatus> ReadAllComponentStatus()
        {
            lock (_governanceSync)
                return _componentStatus.Values.Select(value => value.Copy()).ToList();
        }

        public bool RequestLifecycle(LifecycleRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Id) ||
                string.IsNullOrWhiteSpace(request.Target) || string.IsNullOrWhiteSpace(request.Verb))
                return false;
            lock (_governanceSync)
            {
                if (_lifecycleRequests.Count >= 256 ||
                    _lifecycleOutcomes.ContainsKey(request.Id) ||
                    _lifecycleRequests.Any(item => item.Id == request.Id))
                    return false;
                LifecycleRequest copy = request.Copy();
                if (copy.RequestedUtc == default(DateTime)) copy.RequestedUtc = DateTime.UtcNow;
                _lifecycleRequests.Enqueue(copy);
                Monitor.PulseAll(_governanceSync);
                return true;
            }
        }

        public List<LifecycleRequest> TakeLifecycleRequests(int maximum)
        {
            var result = new List<LifecycleRequest>();
            if (maximum <= 0) return result;
            lock (_governanceSync)
            {
                while (result.Count < maximum && _lifecycleRequests.Count != 0)
                    result.Add(_lifecycleRequests.Dequeue().Copy());
            }
            return result;
        }

        public bool PublishLifecycleCommand(LifecycleCommand command)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.Component) ||
                string.IsNullOrWhiteSpace(command.CommandId) || string.IsNullOrWhiteSpace(command.RequestId))
                return false;
            lock (_governanceSync)
            {
                if (_lifecycleCommands.ContainsKey(command.Component)) return false;
                _lifecycleCommands[command.Component] = command.Copy();
                Monitor.PulseAll(_governanceSync);
                return true;
            }
        }

        public LifecycleCommand WaitForLifecycleCommand(string component, int timeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(component)) return null;
            lock (_governanceSync)
            {
                LifecycleCommand value;
                if (!_lifecycleCommands.TryGetValue(component, out value))
                {
                    Monitor.Wait(_governanceSync, Math.Max(0, timeoutMilliseconds));
                    if (!_lifecycleCommands.TryGetValue(component, out value)) return null;
                }
                return value.Copy();
            }
        }

        public void PublishLifecycleOutcome(LifecycleOutcome outcome)
        {
            if (outcome == null || string.IsNullOrWhiteSpace(outcome.RequestId)) return;
            lock (_governanceSync)
            {
                LifecycleCommand active = _lifecycleCommands.Values.FirstOrDefault(command =>
                    string.Equals(command.CommandId, outcome.CommandId, StringComparison.Ordinal));
                if (active != null) _lifecycleCommands.Remove(active.Component);
                LifecycleOutcome copy = outcome.Copy();
                if (copy.CompletedUtc == default(DateTime)) copy.CompletedUtc = DateTime.UtcNow;
                _lifecycleOutcomes[copy.RequestId] = copy;
                Monitor.PulseAll(_governanceSync);
            }
        }

        public void RejectLifecycle(string requestId, string message)
        {
            PublishLifecycleOutcome(new LifecycleOutcome
            {
                RequestId = requestId,
                Ok = false,
                Message = message,
                CompletedUtc = DateTime.UtcNow
            });
        }

        public LifecycleOutcome WaitForLifecycleOutcome(string requestId, int timeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return null;
            lock (_governanceSync)
            {
                LifecycleOutcome value;
                if (!_lifecycleOutcomes.TryGetValue(requestId, out value))
                {
                    Monitor.Wait(_governanceSync, Math.Max(0, timeoutMilliseconds));
                    if (!_lifecycleOutcomes.TryGetValue(requestId, out value)) return null;
                }
                return value.Copy();
            }
        }

        public void FinishLifecycle(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return;
            lock (_governanceSync) _lifecycleOutcomes.Remove(requestId);
        }
    }
}
