using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace CityDwellers.Host
{
    internal static class FlipperProcessHost
    {
        private const int GracefulStopMilliseconds = 20000;

        public static int RunService(WaitHandle stopSignal)
        {
            Process process = Start(null, true);
            if (process == null) return 1;
            using (process)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                while (!process.WaitForExit(250))
                {
                    if (stopSignal == null || !stopSignal.WaitOne(0)) continue;
                    RuntimeLog.Write("Stopping isolated known-good Flipper process.");
                    try { process.StandardInput.WriteLine(); process.StandardInput.Flush(); }
                    catch (Exception ex) { RuntimeLog.Write("Unable to signal Flipper shutdown: " + ex.Message); }
                    if (!process.WaitForExit(GracefulStopMilliseconds))
                    {
                        RuntimeLog.Write("Flipper did not stop within 20 seconds; terminating it.");
                        process.Kill(); process.WaitForExit(); return 1;
                    }
                    return process.ExitCode;
                }
                return process.ExitCode;
            }
        }

        public static int RunManual(string command)
        {
            using (Process process = Start(command, false))
            {
                if (process == null) return 1;
                process.WaitForExit(); return process.ExitCode;
            }
        }

        private static Process Start(string command, bool service)
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string executable = Path.Combine(root, "Flipper.exe");
            if (!File.Exists(executable)) { RuntimeLog.Write("Required Flipper.exe was not found at '" + executable + "'."); return null; }
            var info = new ProcessStartInfo
            {
                FileName = executable, Arguments = command ?? string.Empty, WorkingDirectory = root,
                UseShellExecute = false, CreateNoWindow = service,
                RedirectStandardInput = service, RedirectStandardOutput = service, RedirectStandardError = service
            };
            var process = new Process { StartInfo = info };
            if (service) { process.OutputDataReceived += RelayOutput; process.ErrorDataReceived += RelayError; }
            try
            {
                if (!process.Start()) throw new InvalidOperationException("Process.Start returned false.");
                RuntimeLog.Write(service ? "Known-good Flipper service process started." : "Known-good Flipper process started for " + command + ".");
                return process;
            }
            catch (Exception ex) { process.Dispose(); RuntimeLog.Write("Unable to start Flipper process: " + ex); return null; }
        }

        private static void RelayOutput(object sender, DataReceivedEventArgs e) { if (e.Data != null) Console.WriteLine(e.Data); }
        private static void RelayError(object sender, DataReceivedEventArgs e) { if (e.Data != null) Console.Error.WriteLine(e.Data); }
    }
}
