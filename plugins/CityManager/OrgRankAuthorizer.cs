using System;
using System.Collections.Generic;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityManager
{
    internal static class OrgRankAuthorizer
    {
        private const int RankCacheSeconds = 300;
        private const int LookupTimeoutMs = 5000;

        private static readonly object Sync = new object();
        private static readonly Dictionary<uint, CachedRank> RankCache =
            new Dictionary<uint, CachedRank>();
        private static readonly Dictionary<uint, PendingLookup> PendingLookups =
            new Dictionary<uint, PendingLookup>();

        private static bool _initialized;

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_initialized)
                    return;

                Client.MessageReceived += MessageReceived;
                _initialized = true;
            }
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                if (_initialized)
                    Client.MessageReceived -= MessageReceived;

                _initialized = false;
                RankCache.Clear();
                PendingLookups.Clear();
            }
        }

        public static void Authorize(
            uint senderId,
            string senderName,
            Action<OrgRankAuthorization> callback)
        {
            if (callback == null)
                return;

            CachedRank cached = null;

            lock (Sync)
            {
                CachedRank candidate;
                if (RankCache.TryGetValue(senderId, out candidate))
                {
                    if (candidate.ExpiresUtc > DateTime.UtcNow)
                        cached = candidate;
                    else
                        RankCache.Remove(senderId);
                }
            }

            if (cached != null)
            {
                callback(BuildResult(cached.Rank, true));
                return;
            }

            bool shouldRequest = false;

            lock (Sync)
            {
                PendingLookup pending;
                if (!PendingLookups.TryGetValue(senderId, out pending))
                {
                    pending = new PendingLookup
                    {
                        SenderId = senderId,
                        SenderName = senderName,
                        RequestedUtc = DateTime.UtcNow
                    };
                    PendingLookups[senderId] = pending;
                    shouldRequest = true;
                }

                pending.Callbacks.Add(callback);
            }

            if (!shouldRequest)
                return;

            try
            {
                Logger.Information(
                    $"Requesting org rank for {senderName} ({senderId}) for #cloak authorization.");

                Client.InfoRequest(
                    new Identity(IdentityType.SimpleChar, unchecked((int)senderId)));
            }
            catch (Exception ex)
            {
                CompleteFailure(
                    senderId,
                    $"Org rank lookup failed: {ex.Message}");
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(LookupTimeoutMs);
                Timeout(senderId);
            });
        }

        private static void MessageReceived(object sender, Message e)
        {
            try
            {
                if (e?.Body == null || e.Body.PacketType != PacketType.N3Message)
                    return;

                var n3 = (N3Message)e.Body;
                if (n3.N3MessageType != N3MessageType.InfoPacket)
                    return;

                uint senderId = unchecked((uint)n3.Identity.Instance);

                lock (Sync)
                {
                    if (!PendingLookups.ContainsKey(senderId))
                        return;
                }

                var infoPacket = (InfoPacketMessage)e.Body;
                if (infoPacket.Type != InfoPacketType.CharacterOrg &&
                    infoPacket.Type != InfoPacketType.CharacterOrgSite &&
                    infoPacket.Type != InfoPacketType.CharacterOrgSiteTower)
                {
                    return;
                }

                var characterInfo = infoPacket.Info as CharacterInfoPacket;
                if (characterInfo == null ||
                    string.IsNullOrWhiteSpace(characterInfo.OrganizationRank))
                {
                    return;
                }

                string rank = characterInfo.OrganizationRank.Trim();

                lock (Sync)
                {
                    RankCache[senderId] = new CachedRank
                    {
                        Rank = rank,
                        ExpiresUtc = DateTime.UtcNow.AddSeconds(RankCacheSeconds)
                    };
                }

                Logger.Information(
                    $"Org rank resolved for {senderId}: {rank}.");

                Complete(senderId, BuildResult(rank, false));
            }
            catch (Exception ex)
            {
                Logger.Warning($"Org rank packet handling failed: {ex.Message}");
            }
        }

        private static OrgRankAuthorization BuildResult(string rank, bool fromCache)
        {
            return new OrgRankAuthorization
            {
                Allowed = IsSquadCommanderOrHigher(rank),
                Rank = rank,
                FromCache = fromCache
            };
        }

        private static bool IsSquadCommanderOrHigher(string rank)
        {
            return string.Equals(rank, "President", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(rank, "General", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(rank, "Squad Commander", StringComparison.OrdinalIgnoreCase);
        }

        private static void Timeout(uint senderId)
        {
            PendingLookup pending;

            lock (Sync)
            {
                if (!PendingLookups.TryGetValue(senderId, out pending))
                    return;

                if ((DateTime.UtcNow - pending.RequestedUtc).TotalMilliseconds < LookupTimeoutMs)
                    return;
            }

            CompleteFailure(senderId, "Org rank lookup timed out.");
        }

        private static void CompleteFailure(uint senderId, string error)
        {
            Complete(
                senderId,
                new OrgRankAuthorization
                {
                    Allowed = false,
                    Error = error
                });
        }

        private static void Complete(uint senderId, OrgRankAuthorization result)
        {
            List<Action<OrgRankAuthorization>> callbacks = null;

            lock (Sync)
            {
                PendingLookup pending;
                if (!PendingLookups.TryGetValue(senderId, out pending))
                    return;

                PendingLookups.Remove(senderId);
                callbacks = new List<Action<OrgRankAuthorization>>(pending.Callbacks);
            }

            foreach (Action<OrgRankAuthorization> callback in callbacks)
            {
                try
                {
                    callback(result);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Org rank authorization callback failed: {ex.Message}");
                }
            }
        }

        private class CachedRank
        {
            public string Rank;
            public DateTime ExpiresUtc;
        }

        private class PendingLookup
        {
            public uint SenderId;
            public string SenderName;
            public DateTime RequestedUtc;
            public readonly List<Action<OrgRankAuthorization>> Callbacks =
                new List<Action<OrgRankAuthorization>>();
        }
    }

    internal class OrgRankAuthorization
    {
        public bool Allowed;
        public string Rank;
        public string Error;
        public bool FromCache;
    }
}
