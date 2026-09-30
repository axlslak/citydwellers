using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using AOSharp.Common.SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MalisBuffBots
{
    public class RebuffProcessor
    {
        public RebuffJson _rebuffInfo;
        private double _initDelay;
        private bool _initialized;
        private readonly Dictionary<int, int> _startupRemovalAttempts = new Dictionary<int, int>();

        public RebuffProcessor(RebuffJson rebuffInfo, float initDelay = 1f)
        {
            _initDelay = initDelay;
            _rebuffInfo = rebuffInfo;
            Client.OnUpdate += OnUpdate;
            Logger.Information("Rebuff tracking initiated.");
        }

        private void OnUpdate(object sender, double deltaTime)
        {
            try
            {
                if (!Client.InPlay || DynelManager.LocalPlayer == null) return;
                _initDelay -= deltaTime;

                if (_initDelay > 0)
                    return;

                if (!_initialized && !RemoveUnconfiguredStartupBuffs())
                { _initDelay = 1; return; }

                if (!TryFindBuffs(_rebuffInfo.LocalPlayerRebuffTags()))
                { _initDelay = 1; return; }

                if (!_initialized)
                {
                    BuffStatus.BuffChanged += OnBuffChanged;
                    _initialized = true;
                }
                // A rejected cast never enters NCU and therefore never expires.
                // Recheck missing configured effects as well as listening for expiry.
                _initDelay = 30;
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
                Logger.Error($"RebuffProcessorOnUpdate");
            }
        }

        private bool RemoveUnconfiguredStartupBuffs()
        {
            bool waiting = false;
            // Login restores NCU without necessarily emitting SetNanoDuration.
            // Only manage catalogue buffs here, never arbitrary hostile/unknown effects.
            foreach (int id in DynelManager.LocalPlayer.Buffs.Select(b => b.Id).ToArray())
            {
                if (!Main.BuffsJson.FindById(id, out var known) || _rebuffInfo.Contains(known.Item2.Tags))
                    continue;
                _startupRemovalAttempts.TryGetValue(id, out int attempts);
                if (attempts >= 3)
                {
                    if (attempts == 3)
                    {
                        Logger.Warning("Startup buff cancellation not confirmed: " + known.Item2.Name +
                            " (" + id + "). Continuing configured requests; this effect may conflict.");
                        _startupRemovalAttempts[id] = 4;
                    }
                    continue;
                }
                _startupRemovalAttempts[id] = attempts + 1;
                if (attempts == 0)
                    Logger.Information("Removing startup buff excluded by RebuffInfo: " + known.Item2.Name + " (" + id + ").");
                DynelManager.LocalPlayer.ForceRemoveBuff(id);
                waiting = true;
            }
            return !waiting;
        }


        private void OnBuffChanged(object sender, BuffChangedArgs buffArgs)
        {
            try
            {
                if (buffArgs.Identity != DynelManager.LocalPlayer.Identity)
                    return;

                Logger.Information($"Buff change triggered: {buffArgs.Id}");
                ProcessBuffArgs(buffArgs);
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
                Logger.Error($"OnBuffChanged");
            }
        }

        private void ProcessBuffArgs(BuffChangedArgs buffArgs)
        {
            if (DynelManager.LocalPlayer.Buffs.Find(buffArgs.Id, out Buff buff) && buff.Cooldown.RemainingTime > 0.05f * buff.NanoItem.TotalTime)
                return;

            if (!Contains(buffArgs.Id, out (Profession, NanoEntry) expiredNano))
                return;

            Main.QueueProcessor.FinalizeBuffRequest(expiredNano.Item1, expiredNano.Item2, DynelManager.LocalPlayer);
        }

        public bool Contains(int id, out (Profession, NanoEntry) expiredNano)
        {
            if (!Main.BuffsJson.FindById(id, out expiredNano))
            {
                Logger.Information($"Couldn't find buff with id: {id}");
                return false;
            }

            if (!_rebuffInfo.Contains(expiredNano.Item2.Tags))
            {
                Logger.Information($"Buff with id {id} not found in local RebuffInfo. Removing from ncu.");
                return false;
            }

            return true;
        }

        private bool TryFindBuffs(IEnumerable<string> buffTags)
        {
            if (buffTags.Count() == 0)
                return true;

            if (!Main.BuffsJson.FindMissingBuffs(buffTags, out Dictionary<Profession, List<NanoEntry>> missingBuffs))
                return true;

            if (_initialized)
            {
                // FindMissingBuffs deliberately always includes team requests.
                // Maintenance must not repeatedly invite for an effect already present.
                foreach (var entries in missingBuffs.Values)
                    entries.RemoveAll(entry => DynelManager.LocalPlayer.Buffs.Any(buff => entry.ContainsId(buff.Id)));
            }

            return Main.QueueProcessor.RequestBuffs(missingBuffs, DynelManager.LocalPlayer);
        }
    }
}
