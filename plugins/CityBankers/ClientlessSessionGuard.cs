using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityBankers
{
    // Compatibility guard for official Clientless 1.0.16. Keep its single
    // update pump and reconnect scheduler; never start a competing game loop.
    internal static class ClientlessSessionGuard
    {
        private static bool _installed;
        private static object _loop;
        private static FieldInfo _callbackField;
        private static Action<double> _original, _guarded;
        private static MethodInfo _setCharacterId;
        private static bool _bindFailureReported;
        private static readonly Stopwatch FaultLogAge = Stopwatch.StartNew();
        private static int _suppressed;
        private static bool _faultReported;
        internal static bool BankCacheTrusted { get; private set; } = true;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            _setCharacterId = typeof(Client).GetProperty("LocalDynelId")?.GetSetMethod(true);
            Client.Disconnected += Disconnected;
            Client.MessageReceived += MessageReceived;
            Client.OnUpdate += BindOnUpdate;
            TryBind(); // Plugin Init normally precedes Client.Init.
            if (_setCharacterId == null)
                Logger.Error("[CityBankers] Reconnect guard unavailable: Client.LocalDynelId setter was not found.");
        }

        internal static void Stop()
        {
            Client.Disconnected -= Disconnected;
            Client.MessageReceived -= MessageReceived;
            Client.OnUpdate -= BindOnUpdate;
            if (_loop != null && ReferenceEquals(_callbackField.GetValue(_loop), _guarded))
                _callbackField.SetValue(_loop, _original);
            _loop = null;
            _original = _guarded = null;
            _installed = false;
        }

        private static void BindOnUpdate(object sender, double delta) => TryBind();

        private static void TryBind()
        {
            if (_loop != null) return;
            try
            {
                var loopField = typeof(Client).GetField("_updateLoop", BindingFlags.Static | BindingFlags.NonPublic);
                if (loopField == null) throw new MissingFieldException("Client", "_updateLoop");
                var loop = loopField.GetValue(null);
                if (loop == null) return;
                var callback = loop.GetType().GetField("_callback", BindingFlags.Instance | BindingFlags.NonPublic);
                var original = callback?.GetValue(loop) as Action<double>;
                if (original == null) throw new MissingFieldException("UpdateLoop", "_callback");
                // Capture the delegate, not mutable teardown fields. An update
                // already in flight remains valid while the plugin unloads.
                Action<double> guarded = delta =>
                {
                    try { original(delta); }
                    catch (Exception ex)
                    {
                        // The SDK otherwise faults an unobserved Task forever:
                        // sockets reconnect, but authentication packets never drain.
                        // Do not log exception messages or packet bodies (credentials).
                        try
                        {
                            if (!_faultReported || FaultLogAge.ElapsedMilliseconds >= 30000)
                            {
                                Logger.Error("[CityBankers] Client update exception contained; update loop will continue next tick. The interrupted callback is not replayed. Type=" +
                                    ex.GetType().FullName + "; suppressed=" + _suppressed + "; stack=" + ex.StackTrace);
                                _faultReported = true;
                                _suppressed = 0;
                                FaultLogAge.Restart();
                            }
                            else _suppressed++;
                        }
                        catch { /* Logging must not kill the SDK pump either. */ }
                    }
                };
                callback.SetValue(loop, guarded);
                _callbackField = callback;
                _original = original;
                _guarded = guarded;
                _loop = loop;
                Logger.Information("[CityBankers] Client update guard installed; reconnect packet processing protected.");
            }
            catch (Exception ex)
            {
                if (_bindFailureReported) return;
                _bindFailureReported = true;
                Logger.Error("[CityBankers] Client update guard could not bind; SDK layout requires review. Type=" + ex.GetType().FullName);
            }
        }

        private static void Disconnected()
        {
            try
            {
                // UserLogin, UserCredentials and SelectCharacter must start with
                // the same sender identity as first login. Native SelectCharacter
                // installs the real ID again after selecting the character.
                _setCharacterId?.Invoke(null, new object[] { 0 });
                Logger.Information("[CityBankers] Reconnect pending; character is offline, awaiting fresh authentication.");
            }
            catch (Exception ex)
            {
                Logger.Error("[CityBankers] Reconnect login identity reset failed. Type=" + ex.GetType().FullName);
            }
        }

        private static void MessageReceived(object sender, Message message)
        {
            // Clientless invokes this event before its native Bank callback.
            // BankMessage is a complete snapshot; 1.0.16 RegisterItems appends
            // without clearing, including after reconnect in the same domain.
            // Do not swallow a compatibility failure and let that append proceed.
            if (message?.Body is BankMessage bank &&
                DynelManager.LocalPlayer != null && bank.Identity == DynelManager.LocalPlayer.Identity)
            {
                var items = Inventory.Bank.Items as IList<Item>;
                if (items == null || items.IsReadOnly)
                {
                    BankCacheTrusted = false;
                    Inventory.Bank.IsOpen = false;
                    StartupCensusGate.Block("Clientless bank snapshot replacement is unavailable; physical cache cannot be trusted.");
                    throw new InvalidOperationException("Unsupported Clientless bank cache; bank snapshot rejected.");
                }
                Inventory.Bank.IsOpen = false;
                items.Clear();
            }
            try
            {
                TryBind(); // First login packet arrives before normal InPlay updates.
                // Only protocol stage names, never salts, credentials or account lists.
                string type = message?.Body?.GetType().Name;
                if (type == "ServerSaltMessage" || type == "CharacterListMessage" || type == "LoginErrorMessage")
                    Logger.Information("[CityBankers] Game login progress: " + type + ".");
            }
            catch { /* Observability must not interrupt native authentication. */ }
        }
    }
}
