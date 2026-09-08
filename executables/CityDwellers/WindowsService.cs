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
            OnStop();
            base.OnShutdown();
        }

        private void RunHost()
        {
            int exitCode = CityDwellersCoordinator.Run(_stop, false);
            if (!_stopRequested && exitCode != 0)
            {
                RuntimeLog.Write(
                    "Unified host failed; terminating so Service Control Manager can restart it.");
                Environment.Exit(exitCode);
            }
        }
    }
}
