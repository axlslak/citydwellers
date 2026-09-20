using File = CityDwellers.Shared.SqlFile;
using Directory = CityDwellers.Shared.SqlDirectory;
using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace CityDwellers.Shared
{
    // SQL control records cross the Manager AppDomain boundary, like Manager restart.
    internal static class ShutdownControl
    {
        internal const string RequestFile = "citydwellers-shutdown.request";
        internal const string AuditFile = "citydwellers-shutdown-audit.jsonl";

        internal sealed class Request
        {
            public string Id;
            public string Actor;
            public uint SenderId;
            public string Authority;
            public string Channel;
            public DateTime RequestedUtc;
        }

        internal static void Audit(string dataDirectory, Request request, string phase)
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
            WriteDurable(Path.Combine(dataDirectory, AuditFile), line, FileMode.Append);
        }

        internal static void Publish(string dataDirectory, Request request)
        {
            string path = Path.Combine(dataDirectory, RequestFile);
            File.CreateNew(path, new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(request)));
        }

        internal static void Clear(string dataDirectory)
        {
            string path = Path.Combine(dataDirectory, RequestFile);
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }

        private static void WriteDurable(string path, string text, FileMode mode)
        {
            if (mode == FileMode.Append) File.AppendAllText(path, text, new UTF8Encoding(false));
            else File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }
}
