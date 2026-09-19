using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AOSharp.Common.GameData;
using AOSharp.Core;
using AOSharp.Core.Inventory;
using AOSharp.Core.UI;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityDwellers.Diagnostics
{
    // Passive full-client observation only. Never sends or moves an item.
    public sealed class CruSplitProbe : AOPluginEntry
    {
        private bool loaded, armed;
        private Stopwatch age;
        private string previous;
        private int changes;
        private readonly Dictionary<string, int> received = new Dictionary<string, int>();

        public override void Run()
        {
            loaded = true;
            Chat.RegisterCommand("splitprobe", Command);
            Network.N3MessageSent += Sent;
            Network.N3MessageReceived += Received;
            Game.OnUpdate += Update;
            Say("Loaded. /splitprobe arms one manual split; /splitprobe snap records inventory.");
        }

        private static void Say(string text)
        {
            // Keep each System/chat line short enough for the game log.
            for (int offset = 0; offset < text.Length; offset += 700)
                Chat.WriteLine("[SPLITPROBE] " + (offset == 0 ? "" : "CONT ") +
                    text.Substring(offset, Math.Min(700, text.Length - offset)));
        }

        private static string Snapshot()
        {
            var items = Inventory.Items.Where(i => i.Slot.Type == IdentityType.Inventory)
                .OrderBy(i => i.Slot.Instance).ToList();
            var free = Enumerable.Range(0x40, 30).Except(items.Select(i => i.Slot.Instance)).ToList();
            return "free=" + string.Join(",", free.Select(i => i.ToString("X4"))) + " | " +
                string.Join("; ", items.Select(i => i.Slot.Instance.ToString("X4") + ":" +
                    i.Id + "/" + i.HighId + "/ql=" + i.QualityLevel + "/count=" + i.Charges));
        }

        private void Command(string command, string[] args, ChatWindow window)
        {
            if (!loaded) return;
            try
            {
                if (args.Length > 0 && args[0].Equals("snap", StringComparison.OrdinalIgnoreCase))
                { Say("SNAP " + Snapshot()); return; }
                if (age != null) { Say("Wait for END before arming again."); return; }
                previous = Snapshot();
                armed = true;
                Say("ARM " + previous);
                Say("Manually split one stack now; leave items still until END (3 seconds).");
            }
            catch (Exception ex) { Stop(ex); }
        }

        private void Sent(object sender, N3Message message)
        {
            if (!armed) return;
            var action = message as CharacterActionMessage;
            if (action == null || (int)action.Action != 52) return;
            armed = false;
            try
            {
                changes = 0;
                received.Clear();
                age = Stopwatch.StartNew();
                Say("LAST POLL " + previous);
                Say("SENT source=" + action.Target + "; quantity=" + action.Parameter2);
                previous = Snapshot();
                Say("SEND CALLBACK " + previous);
            }
            catch (Exception ex) { Stop(ex); }
        }

        private void Received(object sender, N3Message message)
        {
            if (age == null || message == null) return;
            // Type totals only; no chat contents or arbitrary packet payloads.
            string kind = message.N3MessageType.ToString();
            int count;
            received.TryGetValue(kind, out count);
            received[kind] = count + 1;
        }

        private void Update(object sender, float delta)
        {
            if (!armed && age == null) return;
            try
            {
                string now = Snapshot();
                if (age == null) { previous = now; return; }
                if (now != previous && changes < 8)
                {
                    changes++;
                    Say("AFTER " + age.ElapsedMilliseconds + "ms " + now);
                    previous = now;
                }
                if (age.Elapsed.TotalSeconds < 3) return;
                Say("END " + now);
                Say("RECEIVED TYPES " + string.Join(";", received.Select(p => p.Key + "=" + p.Value)));
                age = null;
            }
            catch (Exception ex) { Stop(ex); }
        }

        private void Stop(Exception ex)
        {
            armed = false;
            age = null;
            Say("Stopped: " + ex.Message);
        }

        public override void Teardown()
        {
            loaded = armed = false;
            age = null;
            Network.N3MessageSent -= Sent;
            Network.N3MessageReceived -= Received;
            Game.OnUpdate -= Update;
        }
    }
}
