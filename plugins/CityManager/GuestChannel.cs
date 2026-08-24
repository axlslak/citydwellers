using System;
using System.Collections.Generic;
using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.ChatMessages;

namespace CityManager
{
    public class GuestChannel : ClientlessPluginEntry
    {
        private const int LookupTimeoutSeconds = 5;
        private const ushort PrivateGroupKickPacketType = 51;

        private static readonly HashSet<string> Admins =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Kavem",
                "Doczy"
            };

        private readonly object _pendingSync = new object();
        private readonly List<PendingGuestAction> _pending =
            new List<PendingGuestAction>();

        public override void Init(string pluginDir)
        {
            if (Client.Chat == null)
                return;

            Client.Chat.NetworkMessageReceived += HandleRawChatMessage;
            Client.OnUpdate += Tick;

            Logger.Information(
                "Guest channel controls initialized: invite/kick for admins, leave for guests.");
        }

        public override void Teardown()
        {
            try
            {
                if (Client.Chat != null)
                    Client.Chat.NetworkMessageReceived -= HandleRawChatMessage;

                Client.OnUpdate -= Tick;

                lock (_pendingSync)
                    _pending.Clear();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Guest channel teardown failed: {ex.Message}");
            }
        }

        private void HandleRawChatMessage(object sender, ChatMessage e)
        {
            try
            {
                if (Client.Chat == null || e?.Body == null)
                    return;

                var msg = e.Body as PrivateGroupMessage;
                if (msg == null)
                    return;

                if (msg.ChannelId != Client.Chat.CharId)
                    return;

                // Apcmanager receives its own private-channel messages back from
                // the chat server. Other clients already saw them; blank only our
                // local copy so CityManager does not try to parse its own output.
                if (msg.Sender == Client.Chat.CharId)
                {
                    msg.Text = string.Empty;
                    return;
                }

                string senderName = ResolveName(msg.Sender);
                string commandText = (msg.Text ?? string.Empty).Trim();

                if (commandText.StartsWith("#", StringComparison.Ordinal))
                    commandText = commandText.Substring(1).TrimStart();

                string[] parts = commandText.Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 0)
                    return;

                string command = parts[0].ToLowerInvariant();

                if (command == "leave" && parts.Length == 1)
                {
                    // Nadybot uses the same semantics: leaving this bot's private
                    // channel is implemented by the channel owner kicking you.
                    SendGuestMessage($"{senderName} left the guest channel.");
                    SendPrivateGroupKick(msg.Sender);
                    Logger.Information(
                        $"Guest channel leave: {senderName} ({msg.Sender}).");

                    msg.Text = string.Empty;
                    return;
                }

                if (command == "invite" || command == "kick")
                {
                    msg.Text = string.Empty;

                    if (!Admins.Contains(senderName ?? string.Empty))
                    {
                        SendGuestMessage(
                            $"{senderName}: {command} is an admin-only command.");
                        return;
                    }

                    if (parts.Length != 2)
                    {
                        SendGuestMessage($"Usage: {command} [character]");
                        return;
                    }

                    QueueGuestAction(command, parts[1]);
                    return;
                }

                // Guests are welcome to talk in the channel. Their normal chat
                // must not be fed into CityManager's developer-command parser.
                if (!Admins.Contains(senderName ?? string.Empty))
                    msg.Text = string.Empty;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Guest channel message handling failed: {ex.Message}");
            }
        }

        private void QueueGuestAction(string action, string rawName)
        {
            if (Client.Chat == null)
                return;

            string targetName = NormalizeName(rawName);
            if (string.IsNullOrWhiteSpace(targetName))
            {
                SendGuestMessage($"Usage: {action} [character]");
                return;
            }

            uint targetId;
            if (Client.Chat.NameToIdMap.TryGetValue(targetName, out targetId))
            {
                ExecuteGuestAction(action, targetName, targetId);
                return;
            }

            lock (_pendingSync)
            {
                _pending.Add(new PendingGuestAction
                {
                    Action = action,
                    TargetName = targetName,
                    ExpiresUtc = DateTime.UtcNow.AddSeconds(LookupTimeoutSeconds)
                });
            }

            try
            {
                Client.Chat.RequestCharacterId(targetName);
                Logger.Information(
                    $"Guest channel {action}: looking up {targetName}.");
            }
            catch (Exception ex)
            {
                RemovePending(action, targetName);
                SendGuestMessage(
                    $"Unable to {action} {targetName}: lookup failed ({ex.Message}).");
            }
        }

        private void Tick(object sender, double e)
        {
            if (Client.Chat == null)
                return;

            List<PendingGuestAction> ready = new List<PendingGuestAction>();
            List<PendingGuestAction> expired = new List<PendingGuestAction>();
            DateTime now = DateTime.UtcNow;

            lock (_pendingSync)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    PendingGuestAction pending = _pending[i];
                    uint targetId;

                    if (Client.Chat.NameToIdMap.TryGetValue(
                            pending.TargetName,
                            out targetId))
                    {
                        pending.TargetId = targetId;
                        ready.Add(pending);
                        _pending.RemoveAt(i);
                    }
                    else if (now >= pending.ExpiresUtc)
                    {
                        expired.Add(pending);
                        _pending.RemoveAt(i);
                    }
                }
            }

            foreach (PendingGuestAction pending in ready)
            {
                ExecuteGuestAction(
                    pending.Action,
                    pending.TargetName,
                    pending.TargetId);
            }

            foreach (PendingGuestAction pending in expired)
            {
                SendGuestMessage(
                    $"Unable to {pending.Action} {pending.TargetName}: character lookup timed out.");
            }
        }

        private void ExecuteGuestAction(
            string action,
            string targetName,
            uint targetId)
        {
            if (Client.Chat == null)
                return;

            if (targetId == Client.Chat.CharId)
            {
                SendGuestMessage("Apcmanager cannot remove itself from its own guest channel.");
                return;
            }

            try
            {
                if (string.Equals(action, "invite", StringComparison.OrdinalIgnoreCase))
                {
                    Client.Chat.InvitePrivateGroup(targetId);
                    SendGuestMessage($"Guest invite sent to {targetName}.");
                    Logger.Information(
                        $"Guest channel invite sent to {targetName} ({targetId}).");
                    return;
                }

                if (string.Equals(action, "kick", StringComparison.OrdinalIgnoreCase))
                {
                    SendPrivateGroupKick(targetId);
                    SendGuestMessage($"{targetName} was kicked from the guest channel.");
                    Logger.Information(
                        $"Guest channel kick sent for {targetName} ({targetId}).");
                }
            }
            catch (Exception ex)
            {
                SendGuestMessage(
                    $"Unable to {action} {targetName}: {ex.Message}");
                Logger.Warning(
                    $"Guest channel {action} failed for {targetName}: {ex.Message}");
            }
        }

        private void SendPrivateGroupKick(uint targetId)
        {
            // AO chat packet PRIVGRP_KICK = 51, payload = one uint32 character id.
            // ChatClient.Send(byte[]) expects the normal AO chat header:
            // packet type (u16), payload length (u16), payload (network endian).
            byte[] packet = new byte[8];
            packet[0] = (byte)(PrivateGroupKickPacketType >> 8);
            packet[1] = (byte)PrivateGroupKickPacketType;
            packet[2] = 0;
            packet[3] = 4;
            packet[4] = (byte)(targetId >> 24);
            packet[5] = (byte)(targetId >> 16);
            packet[6] = (byte)(targetId >> 8);
            packet[7] = (byte)targetId;

            Client.Chat.Send(packet);
        }

        private void SendGuestMessage(string text)
        {
            if (Client.Chat == null || string.IsNullOrWhiteSpace(text))
                return;

            Client.Chat.SendPrivateGroupMessage(Client.Chat.CharId, text);
        }

        private string ResolveName(uint id)
        {
            if (Client.Chat != null)
            {
                string name;
                if (Client.Chat.IdToNameMap.TryGetValue(id, out name) &&
                    !string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }

            return $"#{id}";
        }

        private string NormalizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string name = value.Trim();
            if (name.Length == 1)
                return name.ToUpperInvariant();

            return char.ToUpperInvariant(name[0]) +
                   name.Substring(1).ToLowerInvariant();
        }

        private void RemovePending(string action, string targetName)
        {
            lock (_pendingSync)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(
                            _pending[i].Action,
                            action,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            _pending[i].TargetName,
                            targetName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _pending.RemoveAt(i);
                    }
                }
            }
        }

        private class PendingGuestAction
        {
            public string Action;
            public string TargetName;
            public uint TargetId;
            public DateTime ExpiresUtc;
        }
    }
}
