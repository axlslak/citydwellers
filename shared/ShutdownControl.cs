using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace CityDwellers.Shared
{
    // Control requests live in Manager memory; the operator audit is an ordinary disk log.
    internal static class ShutdownControl
    {
        internal const string AuditFile = "citydwellers-shutdown-audit.log";

        internal sealed class Request
        {
            public string Id;
            public string Actor;
            public uint SenderId;
            public string Authority;
            public string Channel;
            public DateTime RequestedUtc;
        }

        internal static void Audit(string dataDirectory, object request, string phase)
        {
            string line = JsonConvert.SerializeObject(new
            {
                phase,
                recordedUtc = DateTime.UtcNow,
                request,
                scope = "whole-host",
                build = BuildIdentity.Label
            }) + Environment.NewLine;
            // Failure propagates: an unaudited shutdown must never be published/consumed.
            WriteDurable(Path.Combine(dataDirectory, AuditFile), line);
        }

        internal static void Publish(string dataDirectory, Request request)
        {
            ManagerMemory.Current.RequestShutdown(request.Id, request.Actor, request.SenderId,
                request.Authority, request.Channel, request.RequestedUtc);
        }

        internal static bool IsRequested => ManagerMemory.Current.ShutdownRequest() != null;
        internal static HostShutdownRequest Read() => ManagerMemory.Current.ShutdownRequest();
        internal static void Clear(string dataDirectory) => ManagerMemory.Current.ClearShutdownRequest();

        private static void WriteDurable(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }
    }
}
