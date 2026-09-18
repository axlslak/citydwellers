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
        private System.Threading.Timer _incidentTimer;
        private int _incidentExporting;
        private void InitializeEventReporting()
        {
            _incidentTimer = new System.Threading.Timer(_ =>
            {
                if (System.Threading.Interlocked.Exchange(ref _incidentExporting, 1) != 0) return;
                try
                {
                    foreach (var incident in IncidentJournal.Recent(_dataDir, int.MaxValue))
                        IncidentJournal.Export(_dataDir, incident.Id, true);
                }
                catch (Exception ex) { Logger.Warning("Incident dump export will retry: " + ex.Message); }
                finally { System.Threading.Interlocked.Exchange(ref _incidentExporting, 0); }
            }, null, 15000, 30000);
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
            _incidentTimer?.Dispose();
            _incidentTimer = null;
            ServiceEvents.Stop();
            _syslog?.Dispose();
            _syslog = null;
        }
    }
}
