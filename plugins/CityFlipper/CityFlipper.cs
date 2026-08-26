using AOSharp.Clientless;
using AOSharp.Clientless.Common;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using CityDwellers.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CityFlipper
{
    public class CityFlipper : ClientlessPluginEntry
    {
        private const float MinimumRaidControllerCharge = 0.75f;

        private string _pluginDir;
        private string _operationId;
        private string _cancelRequestPath;

        private readonly Stopwatch _timer = new Stopwatch();
        private readonly object _sync = new object();

        private bool _charInPlay;
        private bool _controllerFound;
        private bool _gotCityInfo;
        private bool _gotCloakInfo;
        private bool _gotChargeInfo;

        private double _inPlayMs;
        private double _controllerMs;
        private double _cityInfoMs;
        private double _cloakInfoMs;
        private double _chargeInfoMs;

        private float _controllerCharge;
        private bool _controllerOpenRequested;

        private Identity _liveControllerIdentity;
        private bool _haveLiveControllerIdentity;

        private Dictionary<string, string> _cityInfo = new Dictionary<string, string>();
        private Dictionary<string, string> _cloakInfo = new Dictionary<string, string>();
        private Dictionary<string, string> _postToggleCloakInfo = new Dictionary<string, string>();

        private bool _toggleRequested;
        private bool _cancellationRequested;
        private bool _ensureEnabledOnly;
        private bool _ensureDisabledReadyOnly;
        private bool _ensureDisabledWatchOnly;
        private bool _toggleSent;
        private bool _gotPostToggleCloakInfo;
        private string _toggleBlockedReason;
        private double _toggleSentMs;
        private double _postToggleCloakInfoMs;
        private string _initialCloakState;
        private int _initialShieldTimerInSeconds;
        private string _postToggleCloakState;
        private int _postToggleShieldTimerInSeconds;
        private double _watchDeadlineMs;
        private double _nextControllerPollMs;
        private int _chargePollCount;

        private bool _resultWritten;

        public override void Init(string pluginDir)
        {
            _pluginDir = pluginDir;
            string settingsDirectory;
            string settingsError;
            if (SettingsPaths.TryEnsureDirectory(
                    out settingsDirectory,
                    out settingsError))
            {
                _cancelRequestPath = Path.Combine(
                    settingsDirectory,
                    "cityflipper-cancel.request");
            }
            else
            {
                Logger.Warning(
                    $"Flipper cancellation checks are unavailable: {settingsError}");
            }

            string operationIdPath = Path.Combine(
                _pluginDir,
                "cityflipper-operation.id");

            if (File.Exists(operationIdPath))
            {
                try
                {
                    _operationId = File.ReadAllText(operationIdPath).Trim();
                }
                catch
                {
                    _operationId = null;
                }
            }

            string toggleRequestPath = Path.Combine(
                _pluginDir,
                "cityflipper-toggle.request");

            string requestedAction = null;
            if (File.Exists(toggleRequestPath))
            {
                try
                {
                    requestedAction = File.ReadAllText(toggleRequestPath).Trim();
                }
                catch
                {
                    requestedAction = "toggle";
                }
            }

            _toggleRequested = !string.IsNullOrWhiteSpace(requestedAction);
            _ensureEnabledOnly = string.Equals(
                requestedAction,
                "enable",
                StringComparison.OrdinalIgnoreCase);
            _ensureDisabledWatchOnly =
                requestedAction != null &&
                requestedAction.StartsWith(
                    "disable-watch:",
                    StringComparison.OrdinalIgnoreCase);

            int watchSeconds = 60;
            if (_ensureDisabledWatchOnly)
            {
                int parsed;
                if (int.TryParse(
                        requestedAction.Substring("disable-watch:".Length),
                        out parsed))
                {
                    watchSeconds = Math.Max(1, Math.Min(60, parsed));
                }
            }

            _watchDeadlineMs = watchSeconds * 1000.0;
            _ensureDisabledReadyOnly =
                _ensureDisabledWatchOnly ||
                string.Equals(
                    requestedAction,
                    "disable-ready",
                    StringComparison.OrdinalIgnoreCase);

            Logger.Information("CityFlipper diagnostic probe initialized.");
            Logger.Information(
                _ensureEnabledOnly
                    ? "Mode: ENSURE ENABLED (may raise cloak, never lower it)."
                    : _ensureDisabledWatchOnly
                        ? $"Mode: RAID START WATCH (up to {watchSeconds}s for 75% CT charge)."
                        : _ensureDisabledReadyOnly
                            ? "Mode: RAID START (lower only when CT is at least 75% charged)."
                            : _toggleRequested
                                ? "Mode: TOGGLE (one guarded cloak toggle requested)."
                                : "Mode: OBSERVE (read-only).");

            _timer.Start();

            Client.MessageReceived += MessageReceived;
        }

        private void Tick(object sender, double e)
        {
            bool writeTimeout = false;
            bool writeCancellation = false;
            double elapsedMs = _timer.Elapsed.TotalMilliseconds;

            lock (_sync)
            {
                if (_resultWritten)
                    return;

                if (!_toggleSent && IsCancellationRequested())
                {
                    _toggleBlockedReason =
                        "Raid start was canceled before the cloak was lowered.";
                    _cancellationRequested = true;
                    _resultWritten = true;
                    writeCancellation = true;
                }

                if (!writeCancellation &&
                    _ensureDisabledWatchOnly &&
                    !_toggleSent &&
                    elapsedMs >= _watchDeadlineMs)
                {
                    _toggleBlockedReason =
                        $"City Controller charge remained {_controllerCharge * 100:F1}%; " +
                        $"it did not reach {MinimumRaidControllerCharge * 100:F0}% " +
                        "within the fill window.";
                    _resultWritten = true;
                    writeTimeout = true;
                }
            }

            if (writeTimeout || writeCancellation)
            {
                Client.OnUpdate -= Tick;
                WriteResult();
                return;
            }

            if (_toggleSent)
                return;

            if (_controllerOpenRequested && !_ensureDisabledWatchOnly)
                return;

            if (_ensureDisabledWatchOnly &&
                _controllerOpenRequested &&
                elapsedMs < _nextControllerPollMs)
            {
                return;
            }

            var controller = DynelManager.AllDynels
                .FirstOrDefault(d => d.Name == "City Controller");

            if (controller == null)
                return;

            if (!_haveLiveControllerIdentity)
                return;

            float distance = DynelManager.LocalPlayer.DistanceFrom(controller);

            if (!_controllerFound)
            {
                _controllerFound = true;
                _controllerMs = _timer.Elapsed.TotalMilliseconds;

                Logger.Information($"City Controller found after {_controllerMs:F0} ms.");
                Logger.Information($"Controller identity: {controller.Identity}");
                Logger.Information($"Distance to controller: {distance:F2} m");
            }

            if (distance > 10f)
                return;

            bool firstOpen = !_controllerOpenRequested;
            _controllerOpenRequested = true;

            if (_ensureDisabledWatchOnly)
            {
                lock (_sync)
                {
                    _gotChargeInfo = false;
                    _nextControllerPollMs = elapsedMs + 1000.0;
                }
            }

            if (firstOpen)
            {
                Logger.Information($"Opening City Controller at {distance:F2} m.");
                Logger.Information($"Static controller identity: {controller.Identity}");
                Logger.Information($"Live controller identity: {_liveControllerIdentity}");
            }
            else
            {
                Logger.Debug("Refreshing City Controller charge.");
            }

            Client.Send(new GenericCmdMessage
            {
                Temp1 = 0,
                Count = 5,
                Action = GenericCmdAction.Use,
                Temp4 = 1,
                User = DynelManager.LocalPlayer.Identity,
                Target = _liveControllerIdentity,
                Unknown = 1
            });

            if (firstOpen)
                Logger.Information("Requested City Controller open.");

            if (!_ensureDisabledWatchOnly)
                Client.OnUpdate -= Tick;
        }

        private void MessageReceived(object sender, Message e)
        {
            try
            {
                if (e?.Body == null)
                    return;

                if (e.Body.PacketType != PacketType.N3Message)
                    return;

                var n3Message = (N3Message)e.Body;

                if (_charInPlay)
                    Logger.Debug($"N3MessageType = {n3Message.N3MessageType}");

                switch (n3Message.N3MessageType)
                {
                    case N3MessageType.CharInPlay:
                    {
                        var charInPlay = (CharInPlayMessage)e.Body;

                        if (charInPlay.Identity.Instance == Client.LocalDynelId)
                            OnCharInPlay();

                        break;
                    }

                    case N3MessageType.GenericCmd:
                    {
                        var cmd = (GenericCmdMessage)e.Body;

                        Logger.Information("GenericCmd received from server:");
                        LogDictionary("GENERIC CMD", DumpObject(cmd));
                        break;
                    }

                    case N3MessageType.AOTransportSignal:
                    {
                        var signal = (AOTransportSignalMessage)e.Body;

                        Logger.Information($"AOTransportSignal action = {signal.Action}");
                        HandleTransportSignal(signal);
                        break;
                    }

                    case N3MessageType.PlayfieldAnarchyF:
                    {
                        var pf = (PlayfieldAnarchyFMessage)e.Body;

                        Logger.Information(
                            $"PlayfieldAnarchyF received. " +
                            $"PlayfieldId={pf.PlayfieldId1}, " +
                            $"ProxyId={pf.ProxyId}, " +
                            $"SG={pf.SG}, " +
                            $"Dynels={pf.Dynels?.Length ?? 0}");

                        if (pf.Dynels != null)
                        {
                            foreach (var d in pf.Dynels)
                            {
                                Logger.Information(
                                    $"PF DYNEL: " +
                                    $"Type={d.IdentityType}, " +
                                    $"Instance=0x{d.Instance:X}, " +
                                    $"U1={d.Unknown1}, " +
                                    $"U2={d.Unknown2}, " +
                                    $"U3={d.Unknown3}");

                                if (d.IdentityType == IdentityType.CityController)
                                {
                                    _liveControllerIdentity = new Identity(
                                        IdentityType.CityController,
                                        (int)d.Instance);

                                    _haveLiveControllerIdentity = true;

                                    Logger.Information(
                                        $"Live City Controller identity: {_liveControllerIdentity}");
                                }
                            }
                        }

                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception processing CityFlipper message: {ex}");
            }
        }

        private void OnCharInPlay()
        {
            if (_charInPlay)
                return;

            _charInPlay = true;
            _inPlayMs = _timer.Elapsed.TotalMilliseconds;

            Logger.Information($"CharInPlay after {_inPlayMs:F0} ms.");
            Client.OnUpdate += Tick;
        }

        private void HandleTransportSignal(AOTransportSignalMessage signal)
        {
            try
            {
                switch (signal.Action)
                {
                    case AOSignalAction.CityInfo:
                    {
                        double now = _timer.Elapsed.TotalMilliseconds;
                        object value = signal.TransportSignalMessage;

                        lock (_sync)
                        {
                            _cityInfoMs = now;
                            _cityInfo = DumpObject(value);
                            _gotCityInfo = true;
                        }

                        Logger.Information($"CityInfo received after {now:F0} ms.");
                        LogDictionary("CITY INFO", _cityInfo);

                        TryFinish();
                        break;
                    }

                    case AOSignalAction.CloakInfo:
                    {
                        double now = _timer.Elapsed.TotalMilliseconds;
                        var cloakInfo = (CloakInfo)signal.TransportSignalMessage;
                        bool postToggle;
                        bool acceptedPostToggle = false;
                        string receivedState = cloakInfo.CloakState.ToString();

                        lock (_sync)
                        {
                            postToggle = _toggleSent && _gotCloakInfo;

                            if (!postToggle)
                            {
                                _cloakInfoMs = now;
                                _cloakInfo = DumpObject(cloakInfo);
                                _initialCloakState = cloakInfo.CloakState.ToString();
                                _initialShieldTimerInSeconds = cloakInfo.ShieldTimerInSeconds;
                                _gotCloakInfo = true;
                            }
                            else if (!_gotPostToggleCloakInfo)
                            {
                                // AO can repeat the original CloakInfo packet after
                                // the toggle is sent but before the changed state is
                                // delivered. That repeated packet is not a result.
                                if (!string.Equals(
                                        receivedState,
                                        _initialCloakState,
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    _postToggleCloakInfoMs = now;
                                    _postToggleCloakInfo = DumpObject(cloakInfo);
                                    _postToggleCloakState = receivedState;
                                    _postToggleShieldTimerInSeconds = cloakInfo.ShieldTimerInSeconds;
                                    _gotPostToggleCloakInfo = true;
                                    acceptedPostToggle = true;
                                }
                            }
                        }

                        if (!postToggle)
                        {
                            Logger.Information($"CloakInfo received after {now:F0} ms.");
                            LogDictionary("CLOAK INFO", _cloakInfo);
                        }
                        else if (acceptedPostToggle)
                        {
                            Logger.Information(
                                $"Post-toggle CloakInfo received after {now:F0} ms.");
                            LogDictionary("POST-TOGGLE CLOAK INFO", _postToggleCloakInfo);
                        }
                        else
                        {
                            Logger.Information(
                                $"Ignoring repeated pre-toggle CloakInfo state '{receivedState}' " +
                                $"after {now:F0} ms; waiting for the changed state.");
                        }

                        TryFinish();
                        break;
                    }

                    case AOSignalAction.ChargeInfo:
                    {
                        double now = _timer.Elapsed.TotalMilliseconds;
                        var chargeInfo = (CityCharge)signal.TransportSignalMessage;

                        lock (_sync)
                        {
                            _chargeInfoMs = now;
                            _controllerCharge = chargeInfo.CityControllerCharge;
                            _gotChargeInfo = true;
                            _chargePollCount++;
                        }

                        if (!_ensureDisabledWatchOnly || _chargePollCount == 1)
                        {
                            Logger.Information($"ChargeInfo received after {now:F0} ms.");
                            Logger.Information($"City Controller charge raw = {_controllerCharge}");
                            Logger.Information(
                                $"City Controller charge candidate percent = {_controllerCharge * 100:F1}%");
                        }
                        else
                        {
                            Logger.Debug(
                                $"City Controller watch charge = {_controllerCharge * 100:F1}%.");
                        }

                        TryFinish();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error processing AOTransportSignal: {ex}");
            }
        }

        private void TryFinish()
        {
            bool sendToggle = false;
            bool writeResult = false;

            lock (_sync)
            {
                if (_resultWritten)
                    return;

                if (!_toggleSent && IsCancellationRequested())
                {
                    _toggleBlockedReason =
                        "Raid start was canceled before the cloak was lowered.";
                    _cancellationRequested = true;
                    _resultWritten = true;
                    writeResult = true;
                }

                if (!writeResult &&
                    (!_gotCityInfo || !_gotCloakInfo || !_gotChargeInfo))
                    return;

                if (writeResult)
                {
                    // Cancellation already selected the result above.
                }
                else if (!_toggleRequested)
                {
                    _resultWritten = true;
                    writeResult = true;
                }
                else if (!_toggleSent)
                {
                    if (_ensureEnabledOnly &&
                        string.Equals(
                            _initialCloakState,
                            "Enabled",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _toggleBlockedReason =
                            "Cloak is already enabled; no toggle was sent.";
                        _resultWritten = true;
                        writeResult = true;
                    }
                    else if (_ensureEnabledOnly &&
                             !string.Equals(
                                 _initialCloakState,
                                 "Disabled",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        _toggleBlockedReason =
                            $"Cloak state '{_initialCloakState}' is not safely actionable; no toggle was sent.";
                        _resultWritten = true;
                        writeResult = true;
                    }
                    else if (_ensureDisabledReadyOnly &&
                             string.Equals(
                                 _initialCloakState,
                                 "Disabled",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        _toggleBlockedReason =
                            "Cloak is already disabled; a raid-start lower action was not sent.";
                        _resultWritten = true;
                        writeResult = true;
                    }
                    else if (_ensureDisabledReadyOnly &&
                             !string.Equals(
                                 _initialCloakState,
                                 "Enabled",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        _toggleBlockedReason =
                            $"Cloak state '{_initialCloakState}' is not safely actionable; no lower action was sent.";
                        _resultWritten = true;
                        writeResult = true;
                    }
                    else if (_ensureDisabledReadyOnly &&
                             _controllerCharge < MinimumRaidControllerCharge)
                    {
                        if (_ensureDisabledWatchOnly &&
                            _timer.Elapsed.TotalMilliseconds < _watchDeadlineMs)
                        {
                            // Keep this client online. Tick reopens the controller
                            // about once per second and the next ChargeInfo packet
                            // will re-evaluate readiness.
                            return;
                        }

                        _toggleBlockedReason =
                            $"City Controller charge is {_controllerCharge * 100:F1}%; " +
                            $"at least {MinimumRaidControllerCharge * 100:F0}% is required.";
                        _resultWritten = true;
                        writeResult = true;
                    }
                    else if (_initialShieldTimerInSeconds > 0)
                    {
                        _toggleBlockedReason =
                            $"Shield timer is {_initialShieldTimerInSeconds} seconds; " +
                            "cloak is not currently toggleable.";

                        _resultWritten = true;
                        writeResult = true;
                    }
                    else
                    {
                        _toggleSent = true;
                        _toggleSentMs = _timer.Elapsed.TotalMilliseconds;
                        sendToggle = true;
                    }
                }
                else
                {
                    if (!_gotPostToggleCloakInfo)
                        return;

                    _resultWritten = true;
                    writeResult = true;
                }
            }

            if (sendToggle)
            {
                try
                {
                    Logger.Warning(
                        $"Sending ONE cloak {(_ensureEnabledOnly ? "ENABLE" : _ensureDisabledReadyOnly ? "RAID LOWER" : "toggle")}. " +
                        $"Pre-state={_initialCloakState}, " +
                        $"ShieldTimerInSeconds={_initialShieldTimerInSeconds}.");

                    Client.Send(new ToggleCloakMessage
                    {
                        Unknown1 = 49152
                    });

                    if (_ensureEnabledOnly)
                    {
                        lock (_sync)
                        {
                            _resultWritten = true;
                        }

                        Logger.Warning(
                            "Cloak enable packet sent. Flipper action is authoritative; writing result immediately.");
                        WriteResult();
                    }
                    else
                    {
                        Logger.Warning(
                            "Cloak toggle packet sent; waiting for post-toggle CloakInfo.");
                    }
                }
                catch (Exception ex)
                {
                    lock (_sync)
                    {
                        _toggleBlockedReason = $"Failed to send toggle packet: {ex.Message}";
                        _resultWritten = true;
                    }

                    Logger.Error($"Failed sending cloak toggle: {ex}");
                    WriteResult();
                }

                return;
            }

            if (writeResult)
                WriteResult();
        }

        private bool IsCancellationRequested()
        {
            if (string.IsNullOrWhiteSpace(_operationId) ||
                string.IsNullOrWhiteSpace(_cancelRequestPath))
            {
                return false;
            }

            try
            {
                return File.Exists(_cancelRequestPath) &&
                       string.Equals(
                           File.ReadAllText(_cancelRequestPath).Trim(),
                           _operationId,
                           StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private Dictionary<string, string> DumpObject(object value)
        {
            var result = new Dictionary<string, string>();

            if (value == null)
            {
                result["<null>"] = "";
                return result;
            }

            Type type = value.GetType();
            result["$Type"] = type.FullName;

            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Instance | BindingFlags.Public))
            {
                try
                {
                    object propertyValue = property.GetValue(value);
                    result[property.Name] = propertyValue?.ToString() ?? "<null>";
                }
                catch (Exception ex)
                {
                    result[property.Name] = $"<error: {ex.Message}>";
                }
            }

            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Instance | BindingFlags.Public))
            {
                try
                {
                    if (result.ContainsKey(field.Name))
                        continue;

                    object fieldValue = field.GetValue(value);
                    result[field.Name] = fieldValue?.ToString() ?? "<null>";
                }
                catch (Exception ex)
                {
                    result[field.Name] = $"<error: {ex.Message}>";
                }
            }

            return result;
        }

        private void LogDictionary(string title, Dictionary<string, string> values)
        {
            Logger.Information($"===== {title} =====");

            foreach (var item in values)
                Logger.Information($"{item.Key} = {item.Value}");

            Logger.Information("====================");
        }

        private void WriteResult()
        {
            try
            {
                bool toggleSucceeded =
                    _toggleSent &&
                    _gotPostToggleCloakInfo &&
                    !string.Equals(
                        _initialCloakState,
                        _postToggleCloakState,
                        StringComparison.OrdinalIgnoreCase);

                var result = new FlipperResult
                {
                    Character = Client.CharacterName,
                    InitToInPlayMs = _inPlayMs,
                    InitToControllerMs = _controllerMs,
                    InitToCityInfoMs = _cityInfoMs,
                    InitToCloakInfoMs = _cloakInfoMs,
                    InitToChargeInfoMs = _chargeInfoMs,
                    ControllerCharge = _controllerCharge,
                    CityInfo = _cityInfo,
                    CloakInfo = _cloakInfo,

                    ToggleRequested = _toggleRequested,
                    Canceled = _cancellationRequested,
                    ToggleSent = _toggleSent,
                    ToggleSucceeded = toggleSucceeded,
                    ToggleBlockedReason = _toggleBlockedReason,
                    InitToToggleSentMs = _toggleSentMs,
                    InitToPostToggleCloakInfoMs = _postToggleCloakInfoMs,
                    InitialCloakState = _initialCloakState,
                    InitialShieldTimerInSeconds = _initialShieldTimerInSeconds,
                    PostToggleCloakState = _postToggleCloakState,
                    PostToggleShieldTimerInSeconds = _postToggleShieldTimerInSeconds,
                    PostToggleCloakInfo = _postToggleCloakInfo
                };

                string resultPath = Path.Combine(_pluginDir, "cityflipper-result.json");
                string tempPath = resultPath + ".tmp";

                string json = JsonConvert.SerializeObject(result, Formatting.Indented);

                File.WriteAllText(tempPath, json);

                if (File.Exists(resultPath))
                    File.Delete(resultPath);

                File.Move(tempPath, resultPath);

                Logger.Information(
                    $"Flipper observation complete after {_timer.Elapsed.TotalMilliseconds:F0} ms.");
                Logger.Information("Waiting for Flipper.exe to unload client.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed writing flipper result: {ex}");
            }
        }

        private class FlipperResult
        {
            public string Character;

            public double InitToInPlayMs;
            public double InitToControllerMs;
            public double InitToCityInfoMs;
            public double InitToCloakInfoMs;
            public double InitToChargeInfoMs;

            public float ControllerCharge;

            public Dictionary<string, string> CityInfo;
            public Dictionary<string, string> CloakInfo;

            public bool ToggleRequested;
            public bool Canceled;
            public bool ToggleSent;
            public bool ToggleSucceeded;
            public string ToggleBlockedReason;
            public double InitToToggleSentMs;
            public double InitToPostToggleCloakInfoMs;
            public string InitialCloakState;
            public int InitialShieldTimerInSeconds;
            public string PostToggleCloakState;
            public int PostToggleShieldTimerInSeconds;
            public Dictionary<string, string> PostToggleCloakInfo;
        }
    }
}
