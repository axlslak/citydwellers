using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CityDwellers.Shared;

namespace CityDwellers.Host
{
    internal sealed class Governor
    {
        private static readonly TimeSpan[] RestartDelays =
        {
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(240),
            TimeSpan.FromSeconds(300)
        };

        private sealed class Entry
        {
            public string Name;
            public Func<WaitHandle, int> Run;
            public bool AutoRestart;
            public bool NeedsLifecycleConsumer;
            public int Generation;
            public int ConsecutiveFailures;
            public bool RestartSeriesActive;
            public DateTime? RestartAtUtc;
            public DateTime? StartedUtc;
            public DateTime? StoppedUtc;
            public DateTime NextHeartbeatUtc;
            public int? ExitCode;
            public string Phase = "planned";
            public string Reason;
            public ManualResetEvent Stop;
            public ComponentRunner Runner;
            public bool StopRequested;
        }

        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<bool> _acceptShutdown;
        private readonly Func<bool> _managerRestartRequested;
        private readonly Action _clearManagerRestart;
        private readonly GovernorAuthority _authority;
        private readonly bool _interactive;
        private bool _fatal;

        internal Governor(
            HostSettings settings,
            bool interactive,
            Func<bool> acceptShutdown,
            Func<bool> managerRestartRequested,
            Action clearManagerRestart)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _interactive = interactive;
            _acceptShutdown = acceptShutdown ?? throw new ArgumentNullException(nameof(acceptShutdown));
            _managerRestartRequested = managerRestartRequested ?? throw new ArgumentNullException(nameof(managerRestartRequested));
            _clearManagerRestart = clearManagerRestart ?? throw new ArgumentNullException(nameof(clearManagerRestart));
            _authority = ManagerMemory.Current.AcquireGovernorAuthority();

            int globalLoginWaveSize = settings.Bankers.MaxParallelLogins > 0
                ? settings.Bankers.MaxParallelLogins
                : 4;
            ManagerMemory.Current.ConfigureAoLoginAdmission(
                _authority, globalLoginWaveSize, 1000);
            RuntimeLog.Write(
                "Governor AO login admission: max " + globalLoginWaveSize +
                " ClientDomain.Start calls per 1-second wave across all unified-host components; " +
                "configured by Bankers.MaxParallelLogins.");

            Add("Flipper", stop => FlipperLoader.Run(new string[0], stop, false), true, true);
            Add("Buddies", stop => BuddiesHost.Run(new string[0], stop, false), true, true);
            if (BuffersHost.IsEnabled())
                Add("Buffers", stop => BuffersHost.Run(stop), true, false);
            if (settings.BankersEnabled)
                Add("Bankers", stop => BankerLoader.RunAll(stop, false), false, false);
            Add("Manager", stop => ManagerHost.Run(new string[0], stop, false), true, false);

            foreach (Entry entry in _entries.Values)
                Publish(entry);
        }

        internal bool OperatorShutdownRequested { get; private set; }

        private void Add(
            string name,
            Func<WaitHandle, int> run,
            bool autoRestart,
            bool needsLifecycleConsumer)
        {
            _entries.Add(name, new Entry
            {
                Name = name,
                Run = run,
                AutoRestart = autoRestart,
                NeedsLifecycleConsumer = needsLifecycleConsumer,
                Phase = "planned",
                Reason = "Governor roster planned."
            });
        }

        internal int Run(WaitHandle hostStop)
        {
            RuntimeLog.Write(
                "Governor starting components: " +
                string.Join(", ", _entries.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) + ".");

            foreach (Entry entry in _entries.Values)
                StartEntry(entry, "Initial Governor start.");

            if (_interactive)
            {
                Console.WriteLine();
                Console.WriteLine("City Dwellers is running under Governor control. Press ENTER or CTRL+C to stop all services.");
                Console.WriteLine();
            }

            while (!hostStop.WaitOne(250))
            {
                CheckExited();
                PromoteLifecycleConsumers();
                ResetHealthyRestartSeries();
                StartDueRestarts();
                PublishHeartbeats();

                if (_acceptShutdown())
                {
                    OperatorShutdownRequested = true;
                    break;
                }

                if (_managerRestartRequested() && !RestartManager(hostStop))
                {
                    _fatal = true;
                    break;
                }

                RouteLifecycleRequests();
            }

            return StopAll();
        }

        private void StartEntry(Entry entry, string reason)
        {
            if (entry.Runner != null)
                throw new InvalidOperationException(entry.Name + " already has a live Governor runner.");

            entry.Stop?.Dispose();
            entry.Stop = new ManualResetEvent(false);
            entry.StopRequested = false;
            entry.Generation++;
            entry.StartedUtc = DateTime.UtcNow;
            entry.StoppedUtc = null;
            entry.ExitCode = null;
            entry.RestartAtUtc = null;
            entry.Phase = "starting";
            entry.Reason = reason;
            entry.NextHeartbeatUtc = DateTime.UtcNow;

            if (entry.NeedsLifecycleConsumer)
                ManagerMemory.Current.SetLifecycleConsumerReady(entry.Name, false);

            WaitHandle stop = entry.Stop;
            entry.Runner = new ComponentRunner(entry.Name, () => entry.Run(stop));
            Publish(entry);
            entry.Runner.Start();

            if (!entry.NeedsLifecycleConsumer)
            {
                entry.Phase = "running";
                entry.Reason = "Governor component thread is running.";
                Publish(entry);
            }
        }

        private void PromoteLifecycleConsumers()
        {
            foreach (Entry entry in _entries.Values)
            {
                if (!entry.NeedsLifecycleConsumer ||
                    !string.Equals(entry.Phase, "starting", StringComparison.Ordinal) ||
                    entry.Runner == null ||
                    entry.Runner.Completed.WaitOne(0) ||
                    !ManagerMemory.Current.LifecycleConsumerReady(entry.Name))
                    continue;

                entry.Phase = "running";
                entry.Reason = "Governor memory command consumer is ready.";
                Publish(entry);
            }
        }

        private void CheckExited()
        {
            foreach (Entry entry in _entries.Values)
            {
                if (entry.Runner == null || !entry.Runner.Completed.WaitOne(0))
                    continue;

                int exitCode = entry.Runner.ExitCode;
                entry.Runner = null;
                entry.ExitCode = exitCode;
                entry.StoppedUtc = DateTime.UtcNow;
                entry.Stop?.Dispose();
                entry.Stop = null;
                ManagerMemory.Current.SetLifecycleConsumerReady(entry.Name, false);
                ManagerMemory.Current.CancelLifecycleCommand(
                    _authority, entry.Name, entry.Name + " stopped before completing its Governor command.");

                if (entry.StopRequested)
                {
                    entry.Phase = "stopped";
                    entry.Reason = "Stopped by Governor.";
                    Publish(entry);
                    continue;
                }

                RuntimeLog.Write(
                    entry.Name + " stopped unexpectedly with exit code " + exitCode + ".");

                if (!entry.AutoRestart)
                {
                    entry.Phase = "abandoned";
                    entry.Reason =
                        "Unexpected component exit; automatic restart is disabled for this component.";
                    Publish(entry);
                    continue;
                }

                ScheduleRestart(entry, exitCode);
            }
        }

        private void ScheduleRestart(Entry entry, int exitCode)
        {
            int delayIndex;
            if (!entry.RestartSeriesActive)
            {
                entry.RestartSeriesActive = true;
                entry.ConsecutiveFailures = 0;
                delayIndex = 0;
            }
            else
            {
                entry.ConsecutiveFailures++;
                if (entry.ConsecutiveFailures >= 5)
                {
                    entry.Phase = "abandoned";
                    entry.RestartAtUtc = null;
                    entry.Reason =
                        "Five consecutive Governor restart attempts failed; last exit code=" +
                        exitCode + ".";
                    Publish(entry);
                    return;
                }
                delayIndex = Math.Min(entry.ConsecutiveFailures, RestartDelays.Length - 1);
            }

            TimeSpan delay = RestartDelays[delayIndex];
            entry.Phase = "degraded";
            entry.RestartAtUtc = DateTime.UtcNow.Add(delay);
            entry.Reason =
                "Unexpected exit code=" + exitCode + "; Governor restart in " +
                (int)delay.TotalSeconds + "s.";
            Publish(entry);
        }

        private void StartDueRestarts()
        {
            DateTime now = DateTime.UtcNow;
            foreach (Entry entry in _entries.Values)
            {
                if (!string.Equals(entry.Phase, "degraded", StringComparison.Ordinal) ||
                    !entry.RestartAtUtc.HasValue ||
                    entry.RestartAtUtc.Value > now)
                    continue;

                RuntimeLog.Write(
                    "Governor restarting " + entry.Name + " generation " + (entry.Generation + 1) + ".");
                StartEntry(entry, "Governor restart after unexpected exit.");
            }
        }

        private void ResetHealthyRestartSeries()
        {
            DateTime now = DateTime.UtcNow;
            foreach (Entry entry in _entries.Values)
            {
                if (!entry.RestartSeriesActive ||
                    !string.Equals(entry.Phase, "running", StringComparison.Ordinal) ||
                    !entry.StartedUtc.HasValue ||
                    now - entry.StartedUtc.Value < TimeSpan.FromMinutes(30))
                    continue;

                entry.RestartSeriesActive = false;
                entry.ConsecutiveFailures = 0;
                entry.Reason = "Healthy for 30 minutes; Governor restart history cleared.";
                Publish(entry);
            }
        }

        private void RouteLifecycleRequests()
        {
            foreach (LifecycleRequest request in ManagerMemory.Current.TakeLifecycleRequests(_authority, 32))
            {
                Entry entry;
                if (!_entries.TryGetValue(request.Target ?? string.Empty, out entry))
                {
                    ManagerMemory.Current.RejectLifecycle(
                        _authority, request.Id, "unknown_component: " + (request.Target ?? "<missing>"));
                    continue;
                }

                if (!string.Equals(request.Verb, "request", StringComparison.OrdinalIgnoreCase))
                {
                    ManagerMemory.Current.RejectLifecycle(
                        _authority, request.Id, "Unsupported Governor verb '" + request.Verb + "'.");
                    continue;
                }

                if (!string.Equals(entry.Phase, "running", StringComparison.Ordinal))
                {
                    ManagerMemory.Current.RejectLifecycle(
                        _authority, request.Id,
                        entry.Name + " is " + entry.Phase +
                        (string.IsNullOrWhiteSpace(entry.Reason) ? "." : ": " + entry.Reason));
                    continue;
                }

                var command = new LifecycleCommand
                {
                    RequestId = request.Id,
                    CommandId = Guid.NewGuid().ToString("N"),
                    Component = entry.Name,
                    Verb = request.Verb,
                    Payload = request.Payload,
                    Generation = entry.Generation,
                    IssuedUtc = DateTime.UtcNow
                };

                if (!ManagerMemory.Current.PublishLifecycleCommand(_authority, command))
                {
                    ManagerMemory.Current.RejectLifecycle(
                        _authority, request.Id, entry.Name + " already has a Governor command in progress.");
                }
            }
        }

        private bool RestartManager(WaitHandle hostStop)
        {
            Entry manager;
            if (!_entries.TryGetValue("Manager", out manager))
                return false;

            RuntimeLog.Write("Governor received Manager-only restart request from AO.");
            if (!StopEntry(manager, TimeSpan.FromSeconds(90), true, "Manager restart requested from AO."))
            {
                manager.Phase = "abandoned";
                manager.Reason = "Manager could not stop cleanly for explicit restart.";
                Publish(manager);
                return false;
            }

            _clearManagerRestart();
            if (_managerRestartRequested())
            {
                manager.Phase = "abandoned";
                manager.Reason =
                    "Manager restart request could not be cleared; refusing a restart loop.";
                Publish(manager);
                return false;
            }

            if (hostStop.WaitOne(TimeSpan.FromSeconds(2)))
                return true;

            StartEntry(manager, "Explicit Manager restart requested from AO.");
            RuntimeLog.Write("Governor restarted Manager; other components remained online.");
            return true;
        }

        private bool StopEntry(
            Entry entry,
            TimeSpan timeout,
            bool requireZeroExit,
            string reason)
        {
            if (entry.Runner == null)
                return true;

            entry.StopRequested = true;
            entry.Phase = "stopping";
            entry.Reason = reason;
            Publish(entry);
            ManagerMemory.Current.CancelLifecycleCommand(
                _authority, entry.Name, entry.Name + " is stopping.");
            entry.Stop.Set();

            if (!entry.Runner.Join(timeout))
                return false;

            int exitCode = entry.Runner.ExitCode;
            entry.Runner = null;
            entry.ExitCode = exitCode;
            entry.StoppedUtc = DateTime.UtcNow;
            entry.Stop.Dispose();
            entry.Stop = null;
            ManagerMemory.Current.SetLifecycleConsumerReady(entry.Name, false);
            entry.Phase = "stopped";
            entry.Reason = "Stopped by Governor.";
            Publish(entry);
            return !requireZeroExit || exitCode == 0;
        }

        private void PublishHeartbeats()
        {
            DateTime now = DateTime.UtcNow;
            foreach (Entry entry in _entries.Values)
            {
                if (now < entry.NextHeartbeatUtc) continue;
                entry.NextHeartbeatUtc = now.AddSeconds(1);
                Publish(entry);
            }
        }

        private void Publish(Entry entry)
        {
            ManagerMemory.Current.PublishComponentStatus(_authority, new ComponentStatus
            {
                Name = entry.Name,
                Phase = entry.Phase,
                Generation = entry.Generation,
                StartedUtc = entry.StartedUtc,
                StoppedUtc = entry.StoppedUtc,
                ExitCode = entry.ExitCode,
                Reason = entry.Reason,
                ConsecutiveFailures = entry.ConsecutiveFailures,
                ObservedUtc = DateTime.UtcNow
            });
        }

        private int StopAll()
        {
            RuntimeLog.Write("Governor stopping City Dwellers components.");
            bool cleanupFailure = false;
            var order = _entries.Values
                .OrderBy(entry => string.Equals(entry.Name, "Manager", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (Entry entry in order)
            {
                if (entry.Runner == null) continue;
                entry.StopRequested = true;
                entry.Phase = "stopping";
                entry.Reason = "Unified host is stopping.";
                Publish(entry);
                ManagerMemory.Current.CancelLifecycleCommand(
                    _authority, entry.Name, entry.Name + " is stopping.");
                entry.Stop.Set();
            }

            var budget = Stopwatch.StartNew();
            TimeSpan maximum = TimeSpan.FromSeconds(160);
            foreach (Entry entry in order)
            {
                if (entry.Runner == null) continue;
                TimeSpan remaining = maximum - budget.Elapsed;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

                if (!entry.Runner.Join(remaining))
                {
                    entry.Phase = "abandoned";
                    entry.Reason = "Component did not stop inside the unified 160-second budget.";
                    cleanupFailure = true;
                    Publish(entry);
                    continue;
                }

                int exitCode = entry.Runner.ExitCode;
                entry.Runner = null;
                entry.ExitCode = exitCode;
                entry.StoppedUtc = DateTime.UtcNow;
                entry.Stop.Dispose();
                entry.Stop = null;
                ManagerMemory.Current.SetLifecycleConsumerReady(entry.Name, false);
                if (exitCode != 0) cleanupFailure = true;
                entry.Phase = "stopped";
                entry.Reason = exitCode == 0
                    ? "Unified host stopped the component cleanly."
                    : "Component returned exit code " + exitCode + " while stopping.";
                Publish(entry);
            }

            bool abandoned = _entries.Values.Any(entry =>
                string.Equals(entry.Phase, "abandoned", StringComparison.Ordinal));
            RuntimeLog.Write(
                _fatal
                    ? "Governor stopped after a terminal lifecycle failure."
                    : cleanupFailure
                        ? "Governor stopped with component cleanup errors."
                        : abandoned
                            ? "Governor stopped with one or more components already abandoned."
                            : "Governor stopped cleanly.");
            Console.Out.Flush();
            return _fatal || cleanupFailure || abandoned ? 1 : 0;
        }
    }
}
