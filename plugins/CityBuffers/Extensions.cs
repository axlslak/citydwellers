using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using AOSharp.Common.Unmanaged.DataTypes;
using AOSharp.Common.Unmanaged.Imports;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MalisBuffBots
{
    public static class Extensions
    {
        public static bool IsPvpFlagged(this SimpleChar simpleChar) => simpleChar.Buffs.Any(x => Enum.GetValues(typeof(PvpFlagId)).Cast<int>().ToList().Contains(x.Id));

        public static bool CanUseSitKit(this LocalPlayer localPlayer, out Item item) => Inventory.Items.Find(Main.SettingsJson.Data.SitKitItemId, out item) && !localPlayer.Cooldowns.ContainsKey(Stat.Treatment);

        public static void TryRemoveBuffs(this LocalPlayer localPlayer, IEnumerable<int> ids)
        {
            foreach (var id in ids)
                TryRemoveBuff(localPlayer, id);
        }

        private static void TryRemoveBuff(this LocalPlayer localPlayer, int id)
        {
            if (!DynelManager.LocalPlayer.Buffs.Contains(id))
                return;

            ForceRemoveBuff(localPlayer, id);
        }

        public static void ForceRemoveBuff(this LocalPlayer localPlayer, int id)
        {
            Client.Send(new CharacterActionMessage
            {
                Action = CharacterActionType.RemoveFriendlyNano,
                Parameter1 = (int)IdentityType.NanoProgram,
                Parameter2 = id,
            });

            Logger.Information($"Removing buff with id: {id}.");
        }

        public static bool DequeueIfNull<T>(this Queue<T> queue, ref T currentValue)
        {
            if (currentValue != null)
                return true;

            if (queue.Count == 0)
                return false;
            else
            {
                currentValue = queue.Dequeue();
                return true;
            }
        }

        public static string ToTitleCase(this string text) => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower());
    }
}
