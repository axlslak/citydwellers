using System;
using System.ServiceProcess;
using System.Threading;

namespace CityDwellers.Host
{
    internal sealed class CityDwellersWindowsService : ServiceBase
    {
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private Thread _hostThread;
        private volatile bool _stopRequested;

        public CityDwellersWindowsService()
        {
            ServiceName = ServiceCommands.ServiceName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            _stopRequested = false;
            _stop.Reset();
            _hostThread = new Thread(RunHost)
            {
                IsBackground = false,
                Name = "CityDwellers.ServiceHost"
            };
            _hostThread.Start();
        }

        protected override void OnStop()
        {
            RuntimeLog.Write("Stop requested by Windows Service Control Manager.");
            StopHost();
        }

        private void StopHost()
        {
            _stopRequested = true;
            RequestAdditionalTime(180000);
            _stop.Set();

            if (_hostThread != null && !_hostThread.Join(TimeSpan.FromSeconds(170)))
            {
                RuntimeLog.Write(
                    "Windows service stop timed out while waiting for components.");
            }
        }

        protected override void OnShutdown()
        {
            RuntimeLog.Write("Stop requested by Windows system shutdown.");
            StopHost();
            base.OnShutdown();
        }

        private void RunHost()
        {
            int exitCode = CityDwellersCoordinator.Run(_stop, false);
            if (CityDwellersCoordinator.OperatorShutdownRequested)
            {
                // Report a normal service stop even if a component had failed earlier.
                // Use another thread: OnStop joins this host thread.
                RuntimeLog.Write("AO operator shutdown completed; stopping Windows service without recovery restart.");
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    ExitCode = 0;
                    Stop();
                    // Stop reports SERVICE_STOPPED first, so SCM recovery is not triggered.
                    // Also terminates any foreground component left after the stop budget.
                    Environment.Exit(0);
                });
                return;
            }
            if (!_stopRequested && exitCode != 0)
            {
                RuntimeLog.Write(
                    "Unified host failed; terminating so Service Control Manager can restart it.");
                Environment.Exit(exitCode);
            }
        }
    }
}
