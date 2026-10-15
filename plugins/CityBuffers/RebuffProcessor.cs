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
using CityDwellers.Shared;

namespace MalisBuffBots
{
    public class RebuffProcessor
    {
        private readonly Dictionary<Profession, List<NanoEntry>> _configured =
            new Dictionary<Profession, List<NanoEntry>>();
        private double _initDelay;
        private bool _initialized;
        private readonly Dictionary<int, int> _startupRemovalAttempts = new Dictionary<int, int>();

        public RebuffProcessor(IEnumerable<string> selections, float initDelay = 1f)
        {
            _initDelay = initDelay;
            foreach (string selection in selections)
            {
                int id;
                bool numeric = int.TryParse(selection, out id);
                int castId = numeric ? PaidBufferCatalogue.FindEffect(id)?.Id ?? id : 0;
                if (!Main.BuffsJson.FindByTags(new[] { numeric ? castId.ToString() : selection }, out var definitions))
                {
                    Logger.Warning("Unknown preparation buff in Buffers entry for " + Client.CharacterName + ": " + selection);
                    continue;
                }
                foreach (var pair in definitions)
                foreach (var definition in pair.Value)
                {
                    var entry = definition;
                    if (numeric)
                    {
                        entry = JsonConvert.DeserializeObject<NanoEntry>(JsonConvert.SerializeObject(definition));
                        entry.LevelToId = entry.LevelToId.Where(n => n.Id == castId).ToArray();
                    }
                    if (!_configured.TryGetValue(pair.Key, out var entries))
                        _configured.Add(pair.Key, entries = new List<NanoEntry>());
                    if (!entries.Contains(entry)) entries.Add(entry);
                }
            }
            Client.OnUpdate += OnUpdate;
            Logger.Information("Preparation tracking initiated from Buffers entry for " + Client.CharacterName + ".");
        }

        public void Stop()
        {
            Client.OnUpdate -= OnUpdate;
            if (_initialized) BuffStatus.BuffChanged -= OnBuffChanged;
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

                if (!TryFindBuffs())
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
            if (Main.PaidPilot) return true; // Paid toons retain their existing preparation.
            bool waiting = false;
            // Login restores NCU without necessarily emitting SetNanoDuration.
            // Only manage catalogue buffs here, never arbitrary hostile/unknown effects.
            foreach (int id in DynelManager.LocalPlayer.Buffs.Select(b => b.Id).ToArray())
            {
                if (!Main.BuffsJson.FindById(id, out var known) || KeepEffect(id))
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
                    Logger.Information("Removing startup buff outside this character's Buffs list: " + known.Item2.Name + " (" + id + ").");
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
            foreach (var pair in _configured)
            foreach (var entry in pair.Value)
            {
                if (!entry.ContainsId(id) && !(entry.RemoveNanoIdUponCast ?? new int[0]).Contains(id)) continue;
                expiredNano = (pair.Key, entry);
                return true;
            }
            expiredNano = default;
            return false;
        }

        internal bool KeepEffect(int id)
        {
            if (_configured.Count == 0) return true;
            if (Contains(id, out _)) return true;
            if (PaidBufferCatalogue.FindEffect(id)?.Id == 275043) return true;
            return Main.BuffsJson.FindById(id, out var nano) &&
                nano.Item2.Tags.Contains("ncu", StringComparer.OrdinalIgnoreCase);
        }

        private bool TryFindBuffs()
        {
            var missingBuffs = _configured.ToDictionary(pair => pair.Key, pair => pair.Value.Where(entry =>
                !DynelManager.LocalPlayer.Buffs.Any(buff =>
                    (entry.ContainsId(buff.Id) || (entry.RemoveNanoIdUponCast ?? new int[0]).Contains(buff.Id)) &&
                    (buff.Cooldown == null || buff.Cooldown.RemainingTime > 0.05f * buff.NanoItem.TotalTime))).ToList());
            if (!missingBuffs.Values.Any(entries => entries.Count != 0)) return true;
            return Main.QueueProcessor.RequestBuffs(missingBuffs, DynelManager.LocalPlayer);
        }
    }
}
