using System;
using System.IO;
using System.Text;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;
using Newtonsoft.Json;

namespace CityManager
{
    public partial class CityManager
    {
        private SyslogSender _syslog;
        private void InitializeEventReporting()
        {
            // Manager alone owns the external sender and canonical event file.
            _syslog = SyslogSender.Create(_settingsDir, message => Logger.Warning(message));
            if (_syslog == null) return;
            var sender = _syslog;
            string path = Path.Combine(_dataDir, "citydwellers-events.jsonl");
            ServiceEvents.Start(_settingsDir, Client.CharacterName, () => Client.LocalDynelId,
                message => Logger.Warning(message), report =>
                {
                    File.AppendAllText(path, JsonConvert.SerializeObject(report) + Environment.NewLine, new UTF8Encoding(false));
                    sender.Enqueue(report);
                });
            ServiceEvents.Report("manager.logging", "info", "Manager event reporting started.");
            Logger.Information("Manager owns syslog reporting; banker events arrive through Central.");
        }

        private void ShutdownEventReporting()
        {
            ServiceEvents.Stop();
            _syslog?.Dispose();
            _syslog = null;
        }
    }
}
