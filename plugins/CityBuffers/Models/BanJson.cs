using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using Newtonsoft.Json;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using SmokeLounge.AOtomation.Messaging.Serialization;
using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using CityDwellers.Shared;

namespace MalisBuffBots
{
    public class BanJson : JsonFile<List<string>>
    {
        public readonly List<string> Entries;
        private readonly object _sync = new object();
        private static readonly Mutex PersistMutex = new Mutex(
            false,
            "CityDwellers.BufferBans." + Process.GetCurrentProcess().Id);

        public BanJson(string jsonPath) : base(jsonPath)
        {
            Entries = _data;
            try
            {
                ManagerMemory.Current.MergeBufferBans(_data);
                SyncLocal();
            }
            catch (Exception ex)
            {
                Logger.Warning("Buffer ban memory unavailable during startup: " + ex.Message);
            }
        }

        private void SyncLocal()
        {
            List<string> shared = ManagerMemory.Current.BufferBans();
            _data.Clear();
            _data.AddRange(shared);
        }

        private void PersistShared()
        {
            string json = JsonConvert.SerializeObject(ManagerMemory.Current.BufferBans());
            string currentDirectory = System.IO.Path.GetDirectoryName(Path.BAN_JSON);
            string buffersDirectory = System.IO.Path.GetDirectoryName(currentDirectory);
            if (string.IsNullOrWhiteSpace(buffersDirectory))
                throw new InvalidOperationException("Buffer ban directory is unavailable.");

            bool held = false;
            try
            {
                held = PersistMutex.WaitOne(TimeSpan.FromSeconds(5));
                if (!held) throw new TimeoutException("Timed out serializing buffer ban persistence.");

                var targets = Directory.Exists(buffersDirectory)
                    ? Directory.GetDirectories(buffersDirectory)
                        .Select(directory => System.IO.Path.Combine(directory, "BanList.json"))
                        .ToList()
                    : new List<string>();

                if (!targets.Any(path => string.Equals(
                        path, Path.BAN_JSON, StringComparison.OrdinalIgnoreCase)))
                    targets.Add(Path.BAN_JSON);

                foreach (string target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
                    DiskFiles.WriteAllText(target, json);
            }
            finally
            {
                if (held) PersistMutex.ReleaseMutex();
            }
        }

        public bool Contains(string name)
        {
            try { return ManagerMemory.Current.BufferBanContains(name); }
            catch
            {
                lock (_sync)
                    return _data.Any(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
            }
        }

        public bool TryAdd(string name, bool save = true)
        {
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return false;

                bool added;
                try { added = ManagerMemory.Current.TryAddBufferBan(name); }
                catch { added = !Contains(name); }
                if (!added) return false;

                try
                {
                    SyncLocal();
                    if (save) PersistShared();
                    return true;
                }
                catch (Exception ex)
                {
                    try { ManagerMemory.Current.TryRemoveBufferBan(name); SyncLocal(); } catch { }
                    Logger.Information("Ban was not changed: " + ex.Message);
                    return false;
                }
            }
        }

        public bool TryRemove(string name, bool save = true)
        {
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return false;

                bool removed;
                try { removed = ManagerMemory.Current.TryRemoveBufferBan(name); }
                catch { removed = Contains(name); }
                if (!removed) return false;

                try
                {
                    SyncLocal();
                    if (save) PersistShared();
                    return true;
                }
                catch (Exception ex)
                {
                    try { ManagerMemory.Current.TryAddBufferBan(name); SyncLocal(); } catch { }
                    Logger.Information("Ban was not changed: " + ex.Message);
                    return false;
                }
            }
        }
    }
}