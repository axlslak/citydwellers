using System;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;

namespace CityManager
{
    public partial class CityManager
    {
        private SyslogSender _syslog;
        private readonly TraceCollector _traces = new TraceCollector();
        private void InitializeEventReporting()
        {
            // Diagnostic reporting must not scan/export every historical incident.
            // Optional syslog receives events without another local or SQL archive.
            try { _syslog = SyslogSender.Create(_settingsDir, message => Logger.Warning(message)); }
            catch (Exception ex) { Logger.Warning("Manager syslog disabled: " + ex.Message); }
            var sender = _syslog;
            var traces = _traces;
            ServiceEvents.Start(_settingsDir, Client.CharacterName, () => Client.LocalDynelId,
                message => Logger.Warning(message),
                // Transfer traces are also kept in a small bounded ring so one
                // transaction can be dumped with no syslog receiver configured.
                // Syslog stays the durable archive; the ring is convenience.
                report => { traces.Observe(report); sender?.Enqueue(report); });
            ServiceEvents.Report("manager.logging", "info", "Manager event reporting started.");
            Logger.Information("Manager event relay ready; banker events arrive through Central.");
        }

        private void ShutdownEventReporting()
        {
            ServiceEvents.Stop();
            _syslog?.Dispose();
            _syslog = null;
        }
    }
}
