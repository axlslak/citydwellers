using File = CityDwellers.Shared.SqlFile;
using Directory = CityDwellers.Shared.SqlDirectory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;

using CityDwellers.Shared;

using Newtonsoft.Json;

namespace CityDwellers.Host
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (ClientlessGameDataBootstrap.IsRestoreCommand(args))
                return ClientlessGameDataBootstrap.Run(args);

            bool isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            bool consoleMode = HasCommand(args, "console");
            if (!isWindows && (HasCommand(args, "service") || HasCommand(args, "install-service") ||
                HasCommand(args, "uninstall-service")))
            {
                Console.Error.WriteLine("Windows service commands are unavailable here. Run: mono CityDwellers.exe console");
                return 1;
            }

            if (HasCommand(args, "install-service"))
                return ServiceCommands.Install();

            if (HasCommand(args, "uninstall-service"))
                return ServiceCommands.Uninstall();

            // Mono may report noninteractive even in a terminal. Only Windows
            // uses that property to select the Windows Service Control Manager.
            if (HasCommand(args, "service") || (isWindows && !consoleMode && !Environment.UserInteractive))
            {
                ServiceBase.Run(new CityDwellersWindowsService());
                return 0;
            }

            if (HasCommand(args, "flipper-probe"))
                return RunManualFlipper(false);

            if (HasCommand(args, "flipper-toggle"))
                return RunManualFlipper(true);

            if (HasCommand(args, "bankers-bagaudit"))
                return RunBankersBagAudit();

            if (args != null && args.Length > 0 && !consoleMode)
            {
                PrintUsage();
                return 1;
            }

            return RunInteractive();
        }

        private static int RunInteractive()
        {
            using (var stop = new ManualResetEvent(false))
            {
                Console.CancelKeyPress += (sender, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    RuntimeLog.Write("Stop requested from console: " + eventArgs.SpecialKey + ".");
                    stop.Set();
                };

                var inputThread = new Thread(() =>
                {
                    string input = Console.ReadLine();
                    RuntimeLog.Write(input == null ? "Stop requested: console input reached EOF." :
                        "Stop requested: console ENTER.");
                    stop.Set();
                })
                {
                    IsBackground = true,
                    Name = "CityDwellers.ConsoleStop"
                };
                inputThread.Start();

                int exitCode = CityDwellersCoordinator.Run(stop, true);
                if (CityDwellersCoordinator.OperatorShutdownRequested)
                {
                    RuntimeLog.Write("AO operator shutdown: exiting after the component stop budget; no restart.");
                    // A stuck foreground component must not keep a terminal command alive.
                    Environment.Exit(0);
                }
                return exitCode;
            }
        }

        private static int RunManualFlipper(bool toggle)
        {
            using (var stop = new ManualResetEvent(false))
            {
                HostSettings settings;
                if (!CityDwellersCoordinator.Prepare(stop, out settings))
                    return 1;

                return FlipperLoader.Run(
                    new[] { toggle ? "toggle" : "probe" },
                    null,
                    true);
            }
        }

        private static int RunBankersBagAudit()
        {
            using (var stop = new ManualResetEvent(false))
            {
                HostSettings settings;
                if (!CityDwellersCoordinator.Prepare(stop, out settings))
                    return 1;

                BagAuditRunner.Run();
                return Environment.ExitCode;
            }
        }

        private static bool HasCommand(string[] args, string expected)
        {
            return args != null &&
                   args.Length == 1 &&
                   string.Equals(args[0], expected, StringComparison.OrdinalIgnoreCase);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("City Dwellers unified host");
            Console.WriteLine();
            Console.WriteLine("  CityDwellers.exe");
            Console.WriteLine("  CityDwellers.exe console   (Mono: mono CityDwellers.exe console)");
            Console.WriteLine("  CityDwellers.exe install-service");
            Console.WriteLine("  CityDwellers.exe uninstall-service");
            Console.WriteLine("  CityDwellers.exe flipper-probe");
            Console.WriteLine("  CityDwellers.exe flipper-toggle");
            Console.WriteLine("  CityDwellers.exe bankers-bagaudit");
        }
    }

    internal static class CityDwellersCoordinator
    {
        private const string ManagerRestartRequestFile =
            "citydwellers-manager-restart.request";
        private static string _dataDirectory;
        internal static bool OperatorShutdownRequested { get; private set; }

        public static int Run(ManualResetEvent stop, bool interactive)
        {
            HostSettings settings;
            if (!Prepare(stop, out settings))
                return stop.WaitOne(0) ? 0 : 1;

            RuntimeLog.Write(
                "Starting Flipper, Buddies and the configured CityBankers clients.");

            var components = new List<ComponentRunner>
            {
                new ComponentRunner(
                    "Flipper",
                    () => FlipperLoader.Run(new string[0], stop, false)),
                new ComponentRunner(
                    "Buddies",
                    () => BuddiesHost.Run(new string[0], stop, false))
            };
            if (BuffersHost.IsEnabled())
                components.Add(new ComponentRunner("Buffers", () => BuffersHost.Run(stop)));
            if (settings.BankersEnabled)
                components.Add(new ComponentRunner("Bankers", () => BankerLoader.RunAll(stop, false)));
            else
            {
                string readyMarker = Path.Combine(_dataDirectory, "citybankers-all-bankers-ready.json");
                try { if (File.Exists(readyMarker)) File.Delete(readyMarker); }
                catch (IOException ex) { RuntimeLog.Write("Could not clear old banker readiness: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { RuntimeLog.Write("Could not clear old banker readiness: " + ex.Message); }
                RuntimeLog.Write("Bankers disabled by configuration; Manager, Flipper and Buddies remain enabled.");
            }

            foreach (ComponentRunner component in components)
                component.Start();

            if (stop.WaitOne(TimeSpan.FromSeconds(1)))
                return StopComponents(components, false);

            var unavailable = new HashSet<ComponentRunner>();
            foreach (ComponentRunner component in components)
            {
                if (!component.Completed.WaitOne(0))
                    continue;

                RuntimeLog.Write(
                    component.Name + " did not remain running; other components will continue.");
                unavailable.Add(component);
            }

            ManualResetEvent managerStop = new ManualResetEvent(false);
            ComponentRunner manager = StartManager(managerStop);

            if (interactive)
            {
                Console.WriteLine();
                Console.WriteLine("City Dwellers is running. Press ENTER or CTRL+C to stop all services.");
                Console.WriteLine();
            }

            bool unexpectedExit = unavailable.Count != 0;
            while (!stop.WaitOne(0))
            {
                var monitored = new List<ComponentRunner>();
                var waits = new List<WaitHandle> { stop };
                foreach (ComponentRunner candidate in components)
                {
                    if (unavailable.Contains(candidate)) continue;
                    monitored.Add(candidate);
                    waits.Add(candidate.Completed);
                }
                if (!unavailable.Contains(manager))
                {
                    monitored.Add(manager);
                    waits.Add(manager.Completed);
                }

                int signaled = WaitHandle.WaitAny(waits.ToArray(), 500);
                if (signaled == 0 || stop.WaitOne(0))
                    break;

                if (TryAcceptShutdown())
                {
                    OperatorShutdownRequested = true;
                    stop.Set();
                    break;
                }

                bool restartRequested = IsManagerRestartRequested();

                if (restartRequested)
                {
                    RuntimeLog.Write("Manager-only restart requested from AO.");
                    managerStop.Set();
                    if (!manager.Join(TimeSpan.FromSeconds(90)) ||
                        manager.ExitCode != 0)
                    {
                        RuntimeLog.Write("Manager could not stop cleanly for restart.");
                        unexpectedExit = true;
                        stop.Set();
                        break;
                    }

                    managerStop.Dispose();
                    DeleteManagerRestartRequest();
                    if (IsManagerRestartRequested())
                    {
                        RuntimeLog.Write(
                            "Manager restart request could not be cleared; " +
                            "stopping instead of entering a restart loop.");
                        unexpectedExit = true;
                        stop.Set();
                        break;
                    }

                    if (stop.WaitOne(TimeSpan.FromSeconds(2)))
                        break;

                    managerStop = new ManualResetEvent(false);
                    manager = StartManager(managerStop);
                    RuntimeLog.Write(
                        "Manager restarted; Flipper, Buddies, and Bankers remained online.");
                    continue;
                }

                if (signaled == WaitHandle.WaitTimeout)
                    continue;

                ComponentRunner component = monitored[signaled - 1];
                RuntimeLog.Write(
                    component.Name + " stopped unexpectedly with exit code " +
                    component.ExitCode + ". Other components remain running.");
                unexpectedExit = true;
                unavailable.Add(component);
            }

            managerStop.Set();
            components.Add(manager);
            int exitCode = StopComponents(components, unexpectedExit);
            managerStop.Dispose();
            return exitCode;
        }

        public static bool Prepare(
            ManualResetEvent stop,
            out HostSettings settings)
        {
            settings = null;
            OperatorShutdownRequested = false;

            SettingsPaths.BindRuntimeDirectoryToProcess(
                AppDomain.CurrentDomain.BaseDirectory);

            string runtimeDirectory;
            string dataDirectory;
            string error;
            if (!SettingsPaths.TryEnsureDirectories(
                    out runtimeDirectory,
                    out dataDirectory,
                    out error))
            {
                Console.Error.WriteLine(error);
                return false;
            }

            try
            {
                settings = LoadOrCreateSettings(runtimeDirectory);
                SqlStore.Initialize(runtimeDirectory);
                SqlStore.StartRuntime();
                RuntimeLog.Initialize(dataDirectory);
                BuildIdentity.StartHost(runtimeDirectory);
                RuntimeLog.Write("BUILD " + BuildIdentity.Label + " | revision=" + BuildIdentity.Revision);
                foreach (string build in BuildIdentity.DescribeComponents(true))
                    RuntimeLog.Write("BUILD " + build);
                _dataDirectory = dataDirectory;
                // Never replay a terminal command from a previous process.
                if (File.Exists(Path.Combine(dataDirectory, ShutdownControl.RequestFile)))
                    RuntimeLog.Write("Discarding stale AO shutdown request on explicit startup; audit retained.");
                ShutdownControl.Clear(dataDirectory);
                ReportRuntimeLayout(runtimeDirectory, dataDirectory);
                DeleteManagerRestartRequest();
                if (IsManagerRestartRequested())
                    throw new IOException(
                        "A stale Manager restart request could not be removed from MySQL.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("City Dwellers startup failed: " + ex);
                return false;
            }

            RuntimeLog.Write("Portable runtime: " + runtimeDirectory);
            RuntimeLog.Write("Mutable state, logs, queues and diagnostics: mandatory MySQL backend.");

            if (!settings.RequireTrustedTime)
            {
                RuntimeLog.Write(
                    "WARNING: trusted-time startup gate is disabled in citydwellers.json.");
                RepositoryUpdates.Start(RuntimeLog.Write);
                return true;
            }

            var gate = new TimeReadinessGate(settings);
            if (!gate.WaitUntilReady(stop))
                return false;

            RuntimeLog.MarkTimeTrusted();
            RuntimeLog.Write("Network time is trustworthy; AO services may start.");
            RepositoryUpdates.Start(RuntimeLog.Write);
            return true;
        }

        private static HostSettings LoadOrCreateSettings(string runtimeDirectory)
        {
            string path = Path.Combine(runtimeDirectory, "citydwellers.json");
            if (!System.IO.File.Exists(path))
            {
                HostSettings defaults = HostSettings.CreateDefault();
                System.IO.File.WriteAllText(
                    path,
                    JsonConvert.SerializeObject(defaults, Formatting.Indented));
                Console.WriteLine("Created unified host settings: " + path);
                throw new InvalidDataException(
                    "Created the complete citydwellers.json template. " +
                    "Replace its example credentials, then start CityDwellers again.");
            }

            HostSettings settings = JsonConvert.DeserializeObject<HostSettings>(
                System.IO.File.ReadAllText(path));
            string validationError;
            if (!HostSettings.TryValidate(settings, out validationError))
                throw new InvalidDataException(
                    "citydwellers.json is invalid: " + validationError);

            return settings;
        }

        private static void ReportRuntimeLayout(
            string runtimeDirectory,
            string dataDirectory)
        {
            List<string> warnings;
            try
            {
                warnings = SettingsPaths.InspectRuntimeLayout(
                    runtimeDirectory,
                    dataDirectory);
            }
            catch (Exception ex)
            {
                RuntimeLog.Write(
                    "WARNING: runtime inventory inspection could not finish: " +
                    ex.Message);
                return;
            }

            if (warnings.Count == 0)
            {
                RuntimeLog.Write("MySQL runtime is ready; disk data is permitted for the item catalogue, logs and diagnostic dumps.");
                return;
            }

            RuntimeLog.Write(
                "Runtime inventory has " + warnings.Count +
                (warnings.Count == 1 ? " notice." : " notices."));
            foreach (string warning in warnings)
                RuntimeLog.Write("WARNING: " + warning);
        }

        private static int StopComponents(
            List<ComponentRunner> components,
            bool unexpectedExit)
        {
            RuntimeLog.Write("Stopping all City Dwellers components.");
            bool cleanupFailure = false;

            var stopBudget = Stopwatch.StartNew();
            TimeSpan maximumStopTime = TimeSpan.FromSeconds(160);

            foreach (ComponentRunner component in components)
            {
                TimeSpan remaining = maximumStopTime - stopBudget.Elapsed;
                if (remaining < TimeSpan.Zero)
                    remaining = TimeSpan.Zero;

                if (!component.Join(remaining))
                {
                    RuntimeLog.Write(
                        component.Name + " did not stop within the unified 160-second stop budget.");
                    cleanupFailure = true;
                }
                else if (component.ExitCode != 0)
                {
                    RuntimeLog.Write(component.Name + " returned exit code " + component.ExitCode +
                        " while components were stopping.");
                    cleanupFailure = true;
                }
            }

            RuntimeLog.Write(
                unexpectedExit
                    ? "Unified host stopped after an earlier component failure; shutdown results are reported above."
                    : cleanupFailure ? "Unified host stopped with component cleanup errors."
                    : "Unified host stopped cleanly.");
            Console.Out.Flush();
            return unexpectedExit || cleanupFailure ? 1 : 0;
        }

        private static ComponentRunner StartManager(WaitHandle managerStop)
        {
            var manager = new ComponentRunner(
                "Manager",
                () => ManagerHost.Run(new string[0], managerStop, false));
            manager.Start();
            return manager;
        }

        private static bool TryAcceptShutdown()
        {
            string path = Path.Combine(_dataDirectory, ShutdownControl.RequestFile);
            if (!File.Exists(path)) return false;
            try
            {
                var request = JsonConvert.DeserializeObject<ShutdownControl.Request>(File.ReadAllText(path));
                if (request == null || string.IsNullOrWhiteSpace(request.Id) ||
                    string.IsNullOrWhiteSpace(request.Actor) || string.IsNullOrWhiteSpace(request.Authority))
                    throw new InvalidDataException("Missing shutdown actor, authority or request ID.");
                ShutdownControl.Audit(_dataDirectory, request, "host-accepted");
                RuntimeLog.Write("SHUTDOWN from AO: actor=" + request.Actor +
                    "; authority=" + request.Authority + "; channel=" + request.Channel +
                    "; requestedUtc=" + request.RequestedUtc.ToString("O") +
                    "; request=" + request.Id + "; stopping ALL components without restart.");
                // The durable audit is retained even after the one-shot marker is removed.
                try { ShutdownControl.Clear(_dataDirectory); }
                catch (Exception ex) { RuntimeLog.Write("Shutdown marker cleanup: " + ex.Message); }
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("AO shutdown could not be read/audited; host remains running: " + ex.Message);
                return false;
            }
        }

        private static bool IsManagerRestartRequested()
        {
            try
            {
                return !string.IsNullOrWhiteSpace(_dataDirectory) &&
                       File.Exists(Path.Combine(
                           _dataDirectory,
                           ManagerRestartRequestFile));
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Unable to inspect Manager restart request: " + ex.Message);
                return false;
            }
        }

        private static void DeleteManagerRestartRequest()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_dataDirectory))
                    return;

                string path = Path.Combine(
                    _dataDirectory,
                    ManagerRestartRequestFile);
                if (File.Exists(path))
                    File.Delete(path);

                string temporaryPath = path + ".tmp";
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Unable to clear Manager restart request: " + ex.Message);
            }
        }
    }

    internal sealed class ComponentRunner
    {
        private readonly Func<int> _run;
        private readonly Thread _thread;

        public ComponentRunner(string name, Func<int> run)
        {
            Name = name;
            _run = run;
            Completed = new ManualResetEvent(false);
            ExitCode = -1;
            _thread = new Thread(Execute)
            {
                IsBackground = false,
                Name = "CityDwellers." + name
            };
        }

        public string Name { get; }
        public ManualResetEvent Completed { get; }
        public int ExitCode { get; private set; }

        public void Start()
        {
            _thread.Start();
        }

        public bool Join(TimeSpan timeout)
        {
            return !_thread.IsAlive || _thread.Join(timeout);
        }

        private void Execute()
        {
            try
            {
                ExitCode = _run();
            }
            catch (Exception ex)
            {
                ExitCode = 1;
                RuntimeLog.Write(Name + " crashed: " + ex);
            }
            finally
            {
                Completed.Set();
            }
        }
    }
}
