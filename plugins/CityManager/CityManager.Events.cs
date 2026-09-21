using System;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;

namespace CityManager
{
    public partial class CityManager
    {
        private SyslogSender _syslog;
        private void InitializeEventReporting()
        {
            // Diagnostic reporting must not scan/export every historical incident.
            // Optional syslog receives events without another local or SQL archive.
            try { _syslog = SyslogSender.Create(_settingsDir, message => Logger.Warning(message)); }
            catch (Exception ex) { Logger.Warning("Manager syslog disabled: " + ex.Message); }
            var sender = _syslog;
            ServiceEvents.Start(_settingsDir, Client.CharacterName, () => Client.LocalDynelId,
                message => Logger.Warning(message), report => sender?.Enqueue(report));
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
