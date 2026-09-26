using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    // Host-only capability. It is deliberately neither Serializable nor MarshalByRefObject,
    // so a client AppDomain cannot carry it through the remoting boundary.
    public sealed class GovernorAuthority
    {
        internal GovernorAuthority() { }
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
        private readonly HashSet<string> _abandonedLifecycleRequests =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _lifecycleRequesters =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _lifecycleConsumers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private GovernorAuthority _governorAuthority;

        public GovernorAuthority AcquireGovernorAuthority()
        {
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
                throw new InvalidOperationException("Only the host Governor can acquire lifecycle authority.");
            lock (_governanceSync)
                return _governorAuthority ?? (_governorAuthority = new GovernorAuthority());
        }

        private void RequireGovernor(GovernorAuthority authority)
        {
            if (!ReferenceEquals(authority, _governorAuthority) || authority == null)
                throw new InvalidOperationException("Governor lifecycle authority required.");
        }

        public void SetLifecycleConsumerReady(string component, bool ready)
        {
            if (string.IsNullOrWhiteSpace(component)) return;
            lock (_governanceSync)
            {
                if (ready) _lifecycleConsumers.Add(component);
                else _lifecycleConsumers.Remove(component);
                Monitor.PulseAll(_governanceSync);
            }
        }

        public bool LifecycleConsumerReady(string component)
        {
            lock (_governanceSync)
                return !string.IsNullOrWhiteSpace(component) && _lifecycleConsumers.Contains(component);
        }

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
                    _abandonedLifecycleRequests.Contains(request.Id) ||
                    _lifecycleRequests.Any(item => item.Id == request.Id))
                    return false;
                LifecycleRequest copy = request.Copy();
                if (copy.RequestedUtc == default(DateTime)) copy.RequestedUtc = DateTime.UtcNow;
                _lifecycleRequests.Enqueue(copy);
                if (!string.IsNullOrWhiteSpace(copy.Requester))
                    _lifecycleRequesters[copy.Id] = copy.Requester;
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
                {
                    LifecycleRequest request = _lifecycleRequests.Dequeue();
                    if (_abandonedLifecycleRequests.Remove(request.Id))
                    {
                        _lifecycleRequesters.Remove(request.Id);
                        continue;
                    }
                    result.Add(request.Copy());
                }
            }
            return result;
        }

        public bool PublishLifecycleCommand(GovernorAuthority authority, LifecycleCommand command)
        {
            RequireGovernor(authority);
            if (command == null || string.IsNullOrWhiteSpace(command.Component) ||
                string.IsNullOrWhiteSpace(command.CommandId) || string.IsNullOrWhiteSpace(command.RequestId))
                return false;
            lock (_governanceSync)
            {
                if (_abandonedLifecycleRequests.Contains(command.RequestId) ||
                    _lifecycleCommands.ContainsKey(command.Component))
                    return false;
                _lifecycleCommands[command.Component] = command.Copy();
                Monitor.PulseAll(_governanceSync);
                return true;
            }
        }

        public LifecycleCommand WaitForLifecycleCommand(string component, int timeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(component)) return null;
            int timeout = Math.Max(0, timeoutMilliseconds);
            var elapsed = Stopwatch.StartNew();
            lock (_governanceSync)
            {
                LifecycleCommand value;
                while (!_lifecycleCommands.TryGetValue(component, out value))
                {
                    int remaining = timeout - (int)Math.Min(int.MaxValue, elapsed.ElapsedMilliseconds);
                    if (remaining <= 0) return null;
                    Monitor.Wait(_governanceSync, remaining);
                }
                return value.Copy();
            }
        }

        public void PublishLifecycleOutcome(LifecycleOutcome outcome)
        {
            if (outcome == null || string.IsNullOrWhiteSpace(outcome.RequestId)) return;
            lock (_governanceSync)
            {
                if (!string.IsNullOrWhiteSpace(outcome.CommandId))
                {
                    LifecycleCommand active = _lifecycleCommands.Values.FirstOrDefault(command =>
                        string.Equals(command.CommandId, outcome.CommandId, StringComparison.Ordinal) &&
                        string.Equals(command.RequestId, outcome.RequestId, StringComparison.Ordinal));
                    if (active == null)
                        return; // Late result from a cancelled/stopped component generation.
                    _lifecycleCommands.Remove(active.Component);
                }

                if (_abandonedLifecycleRequests.Remove(outcome.RequestId))
                {
                    _lifecycleRequesters.Remove(outcome.RequestId);
                    Monitor.PulseAll(_governanceSync);
                    return;
                }

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

        public void CancelLifecycleCommand(GovernorAuthority authority, string component, string message)
        {
            RequireGovernor(authority);
            if (string.IsNullOrWhiteSpace(component)) return;
            lock (_governanceSync)
            {
                LifecycleCommand active;
                if (!_lifecycleCommands.TryGetValue(component, out active)) return;
                _lifecycleCommands.Remove(component);
                if (_abandonedLifecycleRequests.Remove(active.RequestId))
                {
                    _lifecycleRequesters.Remove(active.RequestId);
                    Monitor.PulseAll(_governanceSync);
                    return;
                }
                _lifecycleOutcomes[active.RequestId] = new LifecycleOutcome
                {
                    RequestId = active.RequestId,
                    CommandId = active.CommandId,
                    Ok = false,
                    Message = message ?? "Component command was cancelled.",
                    CompletedUtc = DateTime.UtcNow
                };
                Monitor.PulseAll(_governanceSync);
            }
        }

        public LifecycleOutcome WaitForLifecycleOutcome(string requestId, int timeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return null;
            int timeout = Math.Max(0, timeoutMilliseconds);
            var elapsed = Stopwatch.StartNew();
            lock (_governanceSync)
            {
                LifecycleOutcome value;
                while (!_lifecycleOutcomes.TryGetValue(requestId, out value))
                {
                    int remaining = timeout - (int)Math.Min(int.MaxValue, elapsed.ElapsedMilliseconds);
                    if (remaining <= 0) return null;
                    Monitor.Wait(_governanceSync, remaining);
                }
                return value.Copy();
            }
        }

        public void FinishLifecycle(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return;
            lock (_governanceSync)
            {
                if (_lifecycleOutcomes.Remove(requestId))
                    _lifecycleRequesters.Remove(requestId);
                else
                    _abandonedLifecycleRequests.Add(requestId);
                Monitor.PulseAll(_governanceSync);
            }
        }

        public void AbandonLifecycleRequester(string requester)
        {
            if (string.IsNullOrWhiteSpace(requester)) return;
            lock (_governanceSync)
            {
                string[] requestIds = _lifecycleRequesters
                    .Where(pair => string.Equals(
                        pair.Value, requester, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Key)
                    .ToArray();

                foreach (string requestId in requestIds)
                {
                    if (!_lifecycleOutcomes.Remove(requestId))
                        _abandonedLifecycleRequests.Add(requestId);
                    _lifecycleRequesters.Remove(requestId);
                }

                if (requestIds.Length != 0)
                    Monitor.PulseAll(_governanceSync);
            }
        }
    }
}
