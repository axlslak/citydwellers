using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using CityDwellers.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.ChatMessages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityManager
{
    public partial class CityManager
    {
        private ChatClient _serviceMessageChat;
        private readonly CancellationTokenSource _nativeServiceStop = new CancellationTokenSource();
        private Task _nativeServiceWorker;
        private string _nativeServiceConsumer;
        private string _unknownServiceDay;
        private readonly HashSet<string> _unknownServiceSamples = new HashSet<string>(StringComparer.Ordinal);

        private void InitializeServiceMessages()
        {
            _nativeServiceConsumer = ManagerMemory.Current.BeginNativeServiceConsumer();
            _nativeServiceWorker = Task.Run(async () =>
            {
                bool failed = false;
                try
                {
                    while (!_nativeServiceStop.IsCancellationRequested && ManagerMemory.Current.IsNativeServiceConsumer(_nativeServiceConsumer))
                    {
                        string payload = ManagerMemory.Current.ReadNativeServiceMessage(_nativeServiceConsumer, 250);
                        if (payload == null) continue;
                        try
                        {
                            ArchiveServiceMessage(payload);
                        }
                        catch (Exception error) when (!_nativeServiceStop.IsCancellationRequested)
                        {
                            if (!failed) Logger.Warning("AO service message retained for archive retry: " + error.Message);
                            failed = true;
                            await Task.Delay(2000, _nativeServiceStop.Token).ConfigureAwait(false);
                            continue;
                        }
                        if (failed) Logger.Information("AO service message archiving resumed.");
                        failed = false;
                        if (_nativeServiceStop.IsCancellationRequested ||
                            !ManagerMemory.Current.IsNativeServiceConsumer(_nativeServiceConsumer)) continue;
                        try { ObserveServiceMessage(JsonConvert.DeserializeObject<ServiceEvent>(payload)); }
                        catch (Exception error)
                        {
                            // The original is already durable. A decoder failure
                            // must not prevent later messages reaching the archive.
                            // SQL failures still invoke the host's fatal DB policy.
                            Logger.Warning("AO service message archived but processing failed: " + error.Message);
                        }
                        ManagerMemory.Current.CompleteNativeServiceMessage(_nativeServiceConsumer, payload);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        private void ShutdownServiceMessages()
        {
            UnsubscribeServiceMessages();
            _nativeServiceStop.Cancel();
            ManagerMemory.Current.EndNativeServiceConsumer(_nativeServiceConsumer);
            if (_nativeServiceWorker != null) try { _nativeServiceWorker.Wait(1000); } catch (AggregateException) { }
        }

        private void SubscribeServiceMessages()
        {
            if (ReferenceEquals(_serviceMessageChat, Client.Chat)) return;
            UnsubscribeServiceMessages();
            _serviceMessageChat = Client.Chat;
            if (_serviceMessageChat != null) _serviceMessageChat.NetworkMessageReceived += HandleNativeServiceChat;
        }

        private void UnsubscribeServiceMessages()
        {
            if (_serviceMessageChat != null) _serviceMessageChat.NetworkMessageReceived -= HandleNativeServiceChat;
            _serviceMessageChat = null;
        }

        // One always-online observer, Manager, rather than one copy per banker or
        // buffer. This callback only queues immutable text/metadata; no SQL or files.
        private void HandleNativeServiceChat(object sender, SmokeLounge.AOtomation.Messaging.Messages.ChatMessage message)
        {
            try
            {
                switch (message?.Body)
                {
                    case GroupMsgMessage group when group.SenderId == 0:
                        QueueServiceMessage(new NativeServiceMessage {
                            Source = "AO.Group", RawText = group.Text, GroupType = (int)group.MessageType,
                            ChannelId = group.ChannelId, Organization = Client.OrgName,
                            Fields = new JObject { ["PacketType"] = (int)group.PacketType, ["SenderId"] = group.SenderId }
                        });
                        break;
                    case VicinityMessage vicinity when vicinity.Sender == 0:
                        QueueServiceMessage(new NativeServiceMessage {
                            Source = "AO.Vicinity", RawText = vicinity.Text, Organization = Client.OrgName,
                            Fields = new JObject { ["PacketType"] = (int)vicinity.PacketType, ["SenderId"] = vicinity.Sender,
                                ["Unknown1"] = vicinity.Unk1, ["Unknown2"] = vicinity.Unk2 }
                        });
                        break;
                    case NpcMessage npc:
                        QueueServiceMessage(new NativeServiceMessage {
                            Source = "AO.Npc", RawText = npc.Text, Organization = Client.OrgName,
                            Fields = new JObject { ["PacketType"] = (int)npc.PacketType, ["Unknown1"] = npc.Unk1, ["Unknown2"] = npc.Unk2 }
                        });
                        break;
                    case PrivateMsgMessage direct when direct.Sender == 0:
                        QueueServiceMessage(new NativeServiceMessage {
                            Source = "AO.SystemTell", RawText = direct.Text, Organization = Client.OrgName,
                            Fields = new JObject { ["PacketType"] = (int)direct.PacketType, ["SenderId"] = direct.Sender, ["Unknown1"] = direct.Unk1 }
                        });
                        break;
                    case PrivateGroupMessage guest when guest.Sender == 0:
                        QueueServiceMessage(new NativeServiceMessage {
                            Source = "AO.SystemGuest", RawText = guest.Text, Organization = Client.OrgName,
                            Fields = new JObject { ["PacketType"] = (int)guest.PacketType, ["SenderId"] = guest.Sender, ["ChannelId"] = guest.ChannelId }
                        });
                        break;
                }
            }
            catch (Exception error)
            {
                // The SDK calls raw observers before normal chat dispatch. Never
                // let a diagnostic capture failure suppress that dispatch.
                Logger.Warning("AO service message could not be queued: " + error.Message);
            }
        }

        private void ObserveNativeServiceText(ChatTextMessage message)
        {
            try
            {
                QueueServiceMessage(new NativeServiceMessage {
                    Source = "AO.ChatText", RawText = message.Text, Organization = Client.OrgName,
                    Fields = new JObject { ["PacketType"] = (uint)message.N3MessageType,
                        ["Identity"] = JObject.FromObject(message.Identity), ["Unknown1"] = message.Unknown1, ["Unknown2"] = message.Unknown2 }
                });
            }
            catch (Exception error) { Logger.Warning("AO service text could not be queued: " + error.Message); }
        }

        private static void QueueServiceMessage(NativeServiceMessage message)
        {
            var report = new ServiceEvent { Id = Guid.NewGuid().ToString("N"), TimeUtc = DateTime.UtcNow,
                Character = Client.CharacterName, Role = "manager", Event = "ao.service", Severity = "info",
                Message = "Native AO service message", Data = JObject.FromObject(message) };
            ManagerMemory.Current.EnqueueNativeServiceMessage(JsonConvert.SerializeObject(report));
        }

        private void ArchiveServiceMessage(string payload)
        {
            string day = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string folder = Path.Combine(_settingsDir, "data", "service-messages");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "all-" + day + ".jsonl");
            // Keep the original SDK-text envelope, including known messages,
            // repeats and empty text. No parser output, deduplication or pruning.
            // A leading LF isolates any interrupted append; stable report IDs
            // identify whole-record retry duplicates without discarding evidence.
            byte[] bytes = new UTF8Encoding(false).GetBytes("\n" + payload + "\n");
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        // Runs in Manager's dedicated native-message consumer, with stable identity
        // and receipt time. No chat broadcasts or game actions originate here.
        private void ObserveServiceMessage(ServiceEvent report)
        {
            if (report == null || report.Event != "ao.service" || report.Role != "manager" || report.Data == null)
                throw new InvalidDataException("Native service observation is invalid.");
            var notice = report.Data.ToObject<NativeServiceMessage>();
            if (notice == null || string.IsNullOrWhiteSpace(notice.RawText)) return;
            LandControlEvent row;
            if (TryParseLandControl(notice, out row))
            {
                row.Id = report.Id; row.ObservedUtc = report.TimeUtc; row.RawMessage = notice.RawText;
                row.Source = notice.Source; row.Observer = report.Character;
                row.GroupType = notice.GroupType; row.ChannelId = notice.ChannelId;
                row.Category = notice.Category; row.Instance = notice.Instance;
                ManagerMemory.Current.RecordLandControlEvent(row);
                return;
            }

            string boss, phase, text;
            int immunity;
            if (TryParseWorldBossAnnouncement(notice.RawText, out boss, out phase, out immunity, out text))
            {
                // Existing group/vicinity/N3 paths retain their #wb behavior.
                // NpcMessage previously had no high-level callback.
                if (notice.Source == "AO.Npc") ObserveWorldBossText(notice.RawText, notice.Source);
                return;
            }
            string city;
            if (notice.Source == "AO.Group" && notice.GroupType == 3 &&
                CityExtendedMessageParser.TryDecode(notice.RawText, out city)) return;

            if (notice.RawText.StartsWith("~&", StringComparison.Ordinal) && !notice.Category.HasValue)
            {
                int category, instance;
                List<object> arguments;
                string error;
                if (CityExtendedMessageParser.TryReadExtended(notice.RawText, out category, out instance, out arguments, out error))
                { notice.Category = category; notice.Instance = instance; notice.Arguments = arguments; }
                else notice.DecodeError = error;
            }

            SaveUnknownServiceMessage(report, notice);
        }

        private void SaveUnknownServiceMessage(ServiceEvent report, NativeServiceMessage notice)
        {
            string day = report.TimeUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string path = Path.Combine(_settingsDir, "data", "service-messages", "unknown-" + day + ".jsonl");
            if (_unknownServiceDay != day)
            {
                _unknownServiceSamples.Clear();
                if (DiskFiles.Exists(path))
                    foreach (string line in DiskFiles.ReadLines(path))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            string saved = (string)JObject.Parse(line)["Signature"];
                            if (saved != null) _unknownServiceSamples.Add(saved);
                        }
                        catch (JsonException) { /* Preserve an incomplete diagnostic line; append on a new line. */ }
                    }
                _unknownServiceDay = day;
            }

            var settings = new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeNonAscii };
            string payload = JsonConvert.SerializeObject(notice, settings);
            string signature;
            using (var sha = SHA256.Create())
                signature = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).Replace("-", "");
            if (_unknownServiceSamples.Contains(signature)) return;
            string record = JsonConvert.SerializeObject(new {
                Signature = signature, Id = report.Id, FirstObservedUtc = report.TimeUtc,
                Observer = report.Character, TextRepresentation = "SDK-decoded chat text; original wire bytes are unavailable", Message = notice
            }, settings);
            // The leading newline also isolates any incomplete final write from
            // a prior interruption. No samples are deleted or marked understood.
            DiskFiles.AppendAllText(path, "\n" + record + "\n");
            _unknownServiceSamples.Add(signature);
        }
    }
}
