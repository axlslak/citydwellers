using System;
using System.IO;
using System.Text;

namespace CityDwellers.Shared
{
    public static class HostFailure
    {
        public static void Stop(string message, Exception error = null)
        {
            try
            {
                using (var writer = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false), 1024, true))
                {
                    writer.WriteLine("FATAL: " + message);
                    if (error != null) writer.WriteLine("Failure type: " + error.GetType().FullName);
                    writer.Flush();
                }
            }
            catch { }
            Environment.Exit(2);
            throw new InvalidOperationException(message);
        }
    }
}
