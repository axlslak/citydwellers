using AOSharp.Clientless;
using AOSharp.Clientless.Common;
using AOSharp.Clientless.Logging;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
// These are used by the original clientless cloak code.
// Depending on exactly which AOSharp.Common revision your project
// references, Visual Studio may already have one or both namespaces.
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace CityFlipper
{
    public class CityFlipper : ClientlessPluginEntry
    {
        private string _pluginDir;

        private readonly Stopwatch _timer =
            new Stopwatch();

        private readonly object _sync =
            new object();

        private bool _charInPlay;
        private bool _controllerFound;

        private bool _gotCityInfo;
        private bool _gotCloakInfo;

        private double _inPlayMs;
        private double _controllerMs;
        private double _cityInfoMs;
        private double _cloakInfoMs;

        private Dictionary<string, string> _cityInfo =
            new Dictionary<string, string>();

        private Dictionary<string, string> _cloakInfo =
            new Dictionary<string, string>();

        private bool _resultWritten;

        public override void Init(string pluginDir)
        {
            _pluginDir = pluginDir;

            Logger.Information(
                "CityFlipper diagnostic probe initialized.");

            _timer.Start();

            /*
             * Do NOT set AutoReconnect=false.
             *
             * We already proved that the normal ClientDomain.Unload()
             * path behaves correctly with the clientless defaults.
             */

            Client.MessageReceived += (sender, message) =>
            {
                HandleMessage(message);
            };
        }

        private void HandleMessage(object message)
        {
            try
            {
                /*
                 * First important milestone:
                 * our own character has entered the world.
                 */
                if (message is CharInPlayMessage charInPlay)
                {
                    if (charInPlay.Identity.Instance ==
                        Client.LocalDynelId)
                    {
                        OnCharInPlay();
                    }

                    return;
                }

                /*
                 * City Controller information arrives as
                 * AOTransportSignal messages.
                 */
                if (message is AOTransportSignalMessage signal)
                {
                    HandleTransportSignal(signal);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"Exception processing CityFlipper message: {ex}");
            }
        }

        private void OnCharInPlay()
        {
            if (_charInPlay)
                return;

            _charInPlay = true;
            _inPlayMs = _timer.Elapsed.TotalMilliseconds;

            Logger.Information(
                $"CharInPlay after {_inPlayMs:F0} ms.");

            /*
             * Give the playfield/dynel list a short moment to populate.
             *
             * For the POC this is intentionally simple. Once we know
             * actual timing, we can replace this with a state-driven
             * retry loop.
             */
            Task.Run(async () =>
            {
                for (int attempt = 1; attempt <= 20; attempt++)
                {
                    await Task.Delay(100);

                    if (TryOpenCityController())
                        return;
                }

                Logger.Error(
                    "Could not find City Controller after 20 attempts.");
            });
        }

        private bool TryOpenCityController()
        {
            try
            {
                var controller =
                    DynelManager.AllDynels
                        .FirstOrDefault(
                            d => d.Name == "City Controller");

                if (controller == null)
                    return false;

                if (!_controllerFound)
                {
                    _controllerFound = true;
                    _controllerMs =
                        _timer.Elapsed.TotalMilliseconds;

                    Logger.Information(
                        $"City Controller found after " +
                        $"{_controllerMs:F0} ms.");

                    Logger.Information(
                        $"Controller identity: {controller.Identity}");
                }

                /*
                 * This is the actual AO-specific part borrowed from the
                 * existing cloak-bot approach:
                 *
                 * look at the controller, then issue USE.
                 *
                 * If your checked-out Server Rack CloakBot uses a
                 * slightly different message class for GenericCmd,
                 * keep its exact two send calls here. Everything else
                 * in this POC can remain unchanged.
                 */

                Client.Send(
                    new LookAtMessage
                    {
                        Target = controller.Identity
                    });

                Client.Send(
                    new GenericCmdMessage
                    {
                        Target = controller.Identity,
                        Action = GenericCmdAction.Use
                    });

                Logger.Information(
                    "Requested City Controller open.");

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"Failed to open City Controller: {ex}");

                return false;
            }
        }

        private void HandleTransportSignal(
            AOTransportSignalMessage signal)
        {
            try
            {
                switch (signal.Action)
                {
                    case AOSignalAction.CityInfo:
                        {
                            double now =
                                _timer.Elapsed.TotalMilliseconds;

                            object value =
                                signal.TransportSignalMessage;

                            lock (_sync)
                            {
                                _cityInfoMs = now;

                                _cityInfo =
                                    DumpObject(value);

                                _gotCityInfo = true;
                            }

                            Logger.Information(
                                $"CityInfo received after {now:F0} ms.");

                            LogDictionary(
                                "CITY INFO",
                                _cityInfo);

                            TryFinish();
                            break;
                        }

                    case AOSignalAction.CloakInfo:
                        {
                            double now =
                                _timer.Elapsed.TotalMilliseconds;

                            object value =
                                signal.TransportSignalMessage;

                            lock (_sync)
                            {
                                _cloakInfoMs = now;

                                _cloakInfo =
                                    DumpObject(value);

                                _gotCloakInfo = true;
                            }

                            Logger.Information(
                                $"CloakInfo received after {now:F0} ms.");

                            LogDictionary(
                                "CLOAK INFO",
                                _cloakInfo);

                            TryFinish();
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"Error processing AOTransportSignal: {ex}");
            }
        }

        /*
         * Reflection is intentional here.
         *
         * For this first probe we want AO to tell us what fields are
         * actually available. We don't yet hard-code assumptions such
         * as ChargePercentage, ShieldTimerInSeconds, CanToggle, etc.
         */
        private Dictionary<string, string> DumpObject(
            object value)
        {
            var result =
                new Dictionary<string, string>();

            if (value == null)
            {
                result["<null>"] = "";
                return result;
            }

            Type type = value.GetType();

            result["$Type"] =
                type.FullName;

            foreach (PropertyInfo property
                in type.GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public))
            {
                try
                {
                    object propertyValue =
                        property.GetValue(value);

                    result[property.Name] =
                        propertyValue?.ToString()
                        ?? "<null>";
                }
                catch (Exception ex)
                {
                    result[property.Name] =
                        $"<error: {ex.Message}>";
                }
            }

            foreach (FieldInfo field
                in type.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public))
            {
                try
                {
                    /*
                     * Don't overwrite a property of the same name.
                     */
                    if (result.ContainsKey(field.Name))
                        continue;

                    object fieldValue =
                        field.GetValue(value);

                    result[field.Name] =
                        fieldValue?.ToString()
                        ?? "<null>";
                }
                catch (Exception ex)
                {
                    result[field.Name] =
                        $"<error: {ex.Message}>";
                }
            }

            return result;
        }

        private void LogDictionary(
            string title,
            Dictionary<string, string> values)
        {
            Logger.Information($"===== {title} =====");

            foreach (var item in values)
            {
                Logger.Information(
                    $"{item.Key} = {item.Value}");
            }

            Logger.Information("====================");
        }

        private void TryFinish()
        {
            lock (_sync)
            {
                if (_resultWritten)
                    return;

                /*
                 * For this probe we want BOTH observations.
                 */
                if (!_gotCityInfo || !_gotCloakInfo)
                    return;

                _resultWritten = true;

                WriteResult();
            }
        }

        private void WriteResult()
        {
            try
            {
                var result =
                    new FlipperResult
                    {
                        Character =
                            Client.CharacterName,

                        InitToInPlayMs =
                            _inPlayMs,

                        InitToControllerMs =
                            _controllerMs,

                        InitToCityInfoMs =
                            _cityInfoMs,

                        InitToCloakInfoMs =
                            _cloakInfoMs,

                        CityInfo =
                            _cityInfo,

                        CloakInfo =
                            _cloakInfo
                    };

                string resultPath =
                    Path.Combine(
                        _pluginDir,
                        "cityflipper-result.json");

                string tempPath =
                    resultPath + ".tmp";

                string json =
                    JsonConvert.SerializeObject(
                        result,
                        Formatting.Indented);

                File.WriteAllText(
                    tempPath,
                    json);

                if (File.Exists(resultPath))
                    File.Delete(resultPath);

                File.Move(
                    tempPath,
                    resultPath);

                Logger.Information(
                    $"Flipper observation complete after " +
                    $"{_timer.Elapsed.TotalMilliseconds:F0} ms.");

                Logger.Information(
                    "Waiting for Flipper.exe to unload client.");
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"Failed writing flipper result: {ex}");
            }
        }

        private class FlipperResult
        {
            public string Character;

            public double InitToInPlayMs;
            public double InitToControllerMs;
            public double InitToCityInfoMs;
            public double InitToCloakInfoMs;

            public Dictionary<string, string> CityInfo;
            public Dictionary<string, string> CloakInfo;
        }
    }
}
