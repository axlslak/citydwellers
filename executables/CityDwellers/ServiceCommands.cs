using System;
using System.Diagnostics;
using System.IO;

namespace CityDwellers.Host
{
    internal static class ServiceCommands
    {
        public const string ServiceName = "CityDwellers";

        public static int Install()
        {
            string executable = Process.GetCurrentProcess().MainModule.FileName;
            string binaryCommand = "\"" + executable + "\" service";

            int result = RunSc(
                "create " + ServiceName +
                " binPath= \"" + binaryCommand.Replace("\"", "\\\"") + "\"" +
                " DisplayName= \"City Dwellers\"" +
                " start= delayed-auto" +
                " depend= Tcpip/LanmanWorkstation");
            if (result != 0)
                return result;

            RunSc(
                "description " + ServiceName +
                " \"Unified AO Manager, Flipper, Buddies, and Bankers host\"");

            result = RunSc(
                "failure " + ServiceName +
                " reset= 86400" +
                " actions= restart/60000/restart/120000/restart/300000");
            if (result != 0)
                return result;

            RunSc("failureflag " + ServiceName + " 1");

            Console.WriteLine();
            Console.WriteLine("City Dwellers service installed for delayed automatic startup.");
            Console.WriteLine(
                "Before starting it from network-backed storage, open services.msc and " +
                "set Log On to an account that can read and write that UNC share.");
            Console.WriteLine(
                "Mapped drive letters are not used by the service. Keep the executable " +
                "path and its directory link resolvable by the service account.");
            Console.WriteLine();
            Console.WriteLine("Start now with: sc.exe start " + ServiceName);
            return 0;
        }

        public static int Uninstall()
        {
            RunSc("stop " + ServiceName);
            int result = RunSc("delete " + ServiceName);
            if (result == 0)
                Console.WriteLine("City Dwellers service removed.");
            return result;
        }

        private static int RunSc(string arguments)
        {
            try
            {
                using (Process process = Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.System),
                            "sc.exe"),
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }))
                {
                    if (process == null)
                        return 1;

                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (!string.IsNullOrWhiteSpace(output))
                        Console.Write(output);
                    if (!string.IsNullOrWhiteSpace(error))
                        Console.Error.Write(error);

                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Unable to run Service Control: " + ex);
                return 1;
            }
        }
    }
}
