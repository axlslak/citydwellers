using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using AOSharp.Common.GameData;
using CityBankers.Shared;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

namespace CityBankers
{
    // Observe before native cache mutation and finish at the next message/update.
    // Observers do not modify inventory. Move wrappers record intent and invoke
    // the original SDK operation once; they never infer which duplicate is physical.
    internal static class BagOriginTrace
    {
        private sealed class BagRow
        {
            public string Location;
            public int Slot;
            public string Identity;
            public int LowId;
            public int HighId;
        }
        private sealed class Observation
        {
            public long Sequence;
            public DateTime ReceivedUtc;
            public string MessageType;
            public long LastMoveSequence;
            public object Incoming;
            public string PacketBase64;
            public List<BagRow> Before;
            public List<BagRow> After;
        }
        private static readonly Queue<Observation> Recent = new Queue<Observation>();
        private sealed class MoveRequest
        {
            public long Sequence;
            public DateTime RequestedUtc;
            public string Caller;
            public string Operation;
            public string Source;
            public int SourceType;
            public int SourceInstance;
            public string ItemIdentity;
            public string Target;
            public int TargetType;
            public int TargetInstance;
            public int? InventoryDestinationSlot;
            public string ErrorType;
        }
        private static readonly Queue<MoveRequest> Moves = new Queue<MoveRequest>();
        private static long moveSequence;
        private static Observation pending;
        private static string directory;
        private static string token;
        private static string connection;
        private static long sequence;
        private static bool installed;
        private static bool duplicateWritten;
        private static bool fullWritten;
        private static bool bankWritten;
        private static bool errorReported;

        public static void Install(string settingsDir)
        {
            if (installed) return;
            directory = Path.Combine(RuntimeStateStore.GetDataDirectory(settingsDir), "diagnostic-dumps");
            token = new string((Client.CharacterName ?? "unknown").ToLowerInvariant()
                .Select(c => CityDwellers.Shared.CharacterNames.IsCharacter(c) ? c : '_').ToArray());
            connection = Guid.NewGuid().ToString("N");
            Client.MessageReceived += Receive;
            Client.PacketReceived += Packet;
            Client.OnUpdate += Tick;
            installed = true;
        }
        public static void Stop()
        {
            if (!installed) return;
            Client.MessageReceived -= Receive;
            Client.PacketReceived -= Packet;
            Client.OnUpdate -= Tick;
            pending = null;
            Recent.Clear();
            Moves.Clear();
            installed = false;
        }
        // Record API intent, not a claim that bytes were sent or AO accepted a move.
        // Keep the original SDK calls and their exceptions unchanged. In particular,
        // never rebuild a source identity from only its numeric slot component.
        public static void MoveToInventory(Item item,
            [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
        {
            MoveRequest request = RecordMove(item, Identity.None, 0x6F, "MoveToInventory", caller, file);
            try { item.MoveToInventory(); }
            catch (Exception ex) { RecordMoveFailure(request, ex); throw; }
        }
        public static void MoveToBank(Item item,
            [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
        {
            MoveRequest request = null;
            try { request = RecordMove(item, new Identity(IdentityType.Bank, Client.LocalDynelId),
                null, "MoveToBank", caller, file); }
            catch (Exception ex) { ReportFailure(ex); }
            try { item.MoveToBank(); }
            catch (Exception ex) { RecordMoveFailure(request, ex); throw; }
        }
        public static void MoveToContainer(Item item, Container target,
            [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
        {
            MoveRequest request = null;
            try { request = RecordMove(item, target.Identity, null, "MoveToContainer", caller, file); }
            catch (Exception ex) { ReportFailure(ex); }
            try { item.MoveToContainer(target); }
            catch (Exception ex) { RecordMoveFailure(request, ex); throw; }
        }
        public static void MoveToContainer(Item item, Identity target,
            [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
        {
            MoveRequest request = RecordMove(item, target, null, "MoveToContainer", caller, file);
            try { item.MoveToContainer(target); }
            catch (Exception ex) { RecordMoveFailure(request, ex); throw; }
        }
        private static MoveRequest RecordMove(Item item, Identity target, int? slot,
            string operation, string caller, string file)
        {
            try
            {
                if (!installed) return null;
                var source = item.Slot;
                var request = new MoveRequest { Sequence = ++moveSequence, RequestedUtc = DateTime.UtcNow,
                    Caller = Path.GetFileName(file) + ":" + caller, Operation = operation,
                    Source = source.ToString(), SourceType = (int)source.Type, SourceInstance = source.Instance,
                    ItemIdentity = item.UniqueIdentity.ToString(), Target = target.ToString(),
                    TargetType = (int)target.Type, TargetInstance = target.Instance,
                    InventoryDestinationSlot = slot };
                Moves.Enqueue(request);
                while (Moves.Count > 32) Moves.Dequeue();
                WriteMoves();
                return request;
            }
            catch (Exception ex) { ReportFailure(ex); return null; }
        }
        private static void RecordMoveFailure(MoveRequest request, Exception error)
        {
            try
            {
                if (request == null) return;
                request.ErrorType = error.GetType().Name;
                WriteMoves();
            }
            catch (Exception ex) { ReportFailure(ex); }
        }
        private static void WriteMoves()
        {
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(directory, "bag-origin-" + token + "-moves.json"),
                new { format = "citybankers-bag-moves-v1", character = Client.CharacterName,
                    generation = connection, observedUtc = DateTime.UtcNow,
                    clientless = typeof(Client).Assembly.ManifestModule.ModuleVersionId,
                    plugin = typeof(BagOriginTrace).Assembly.ManifestModule.ModuleVersionId,
                    boundary = "Last 32 instrumented SDK move requests, recorded before invocation. Not an outbound packet capture or server acknowledgement. A missing ErrorType does not prove success. InventoryDestinationSlot belongs to ClientMoveItemToInventory; Target is unused for that operation.",
                    moves = Moves.ToArray() });
        }
        private static List<BagRow> Snapshot()
        {
            return (Inventory.Items ?? new List<Item>()).Where(i => i != null)
                .Select(i => Row(i, "inventory"))
                .Concat((Inventory.Bank.Items ?? new List<Item>()).Where(i => i != null)
                    .Select(i => Row(i, "bank")))
                .Where(i => i != null).ToList();
        }
        private static BagRow Row(Item item, string location)
        {
            if (item.UniqueIdentity.Type != IdentityType.Container) return null;
            return new BagRow { Location = location, Slot = item.Slot.Instance,
                Identity = item.UniqueIdentity.ToString(), LowId = item.Id, HighId = item.HighId };
        }
        private static object Slots(IEnumerable<InventorySlot> slots)
        {
            return (slots ?? new InventorySlot[0]).Select(s => new
            {
                slot = s.Placement, identity = s.Identity.ToString(),
                lowId = s.ItemLowId, highId = s.ItemHighId, ql = s.Quality,
                count = (ushort)s.Count
            }).ToArray();
        }
        private static void Receive(object sender, Message message)
        {
            try
            {
                Flush();
                var body = message?.Body;
                object incoming;
                if (body is FullCharacterMessage full)
                {
                    // A full snapshot starts a new evidence generation, even after
                    // reconnect in the same domain. Old bank cache is labelled Before.
                    connection = Guid.NewGuid().ToString("N");
                    duplicateWritten = fullWritten = bankWritten = false;
                    Recent.Clear();
                    Moves.Clear();
                    moveSequence = 0;
                    incoming = Slots(full.InventorySlots);
                }
                else if (body is BankMessage bank && DynelManager.LocalPlayer != null &&
                    bank.Identity == DynelManager.LocalPlayer.Identity)
                    incoming = new { owner = bank.Identity.ToString(), slots = Slots(bank.BankSlots) };
                else if (body is ContainerAddItem move)
                    incoming = new { owner = move.Identity.ToString(), source = move.Source.ToString(),
                        target = move.Target.ToString(), slot = move.Slot };
                else if (body is InventoryUpdateMessage update)
                    incoming = new { container = update.InventoryIdentity.ToString(),
                        handle = update.Handle, slots = Slots(update.Items) };
                else if (body is CharacterActionMessage action)
                    incoming = new { owner = action.Identity.ToString(), action = action.Action.ToString(),
                        target = action.Target.ToString(), parameter1 = action.Parameter1,
                        parameter2 = action.Parameter2 };
                else if (body is AddTemplateMessage add)
                    incoming = new { lowId = add.LowId, highId = add.HighId, ql = add.Quality, count = add.Count };
                else return;
                pending = new Observation { Sequence = ++sequence, ReceivedUtc = DateTime.UtcNow,
                    MessageType = body.GetType().Name, LastMoveSequence = moveSequence,
                    Incoming = incoming, Before = Snapshot() };
            }
            catch (Exception ex) { pending = null; ReportFailure(ex); }
        }
        private static void Packet(object sender, byte[] packet)
        {
            try
            {
                // Only these selected N3 inventory messages, never authentication,
                // tells, chat or credentials. Raw bytes distinguish decoder errors
                // from already-duplicated received slot arrays.
                if (pending != null && packet != null && packet.Length <= 262144 &&
                    (pending.MessageType == nameof(FullCharacterMessage) ||
                     pending.MessageType == nameof(BankMessage) ||
                     pending.MessageType == nameof(ContainerAddItem)))
                    pending.PacketBase64 = Convert.ToBase64String(packet);
            }
            catch (Exception ex) { ReportFailure(ex); }
        }
        private static void Tick(object sender, double delta)
        {
            try { Flush(); }
            catch (Exception ex) { pending = null; ReportFailure(ex); }
        }
        private static void Flush()
        {
            var observed = pending;
            pending = null;
            if (observed == null) return;
            observed.After = Snapshot();
            Recent.Enqueue(observed);
            while (Recent.Count > 32) Recent.Dequeue();
            if (!fullWritten && observed.MessageType == nameof(FullCharacterMessage))
            {
                Write("login", new[] { observed });
                fullWritten = true;
            }
            if (!bankWritten && observed.MessageType == nameof(BankMessage))
            {
                Write("bank", new[] { observed });
                bankWritten = true;
            }
            if (!duplicateWritten && observed.After.Where(r => r.Location != "bank" || bankWritten)
                .GroupBy(r => r.Identity).Any(g => g.Count() > 1))
            {
                Write("duplicate", Recent.ToArray());
                duplicateWritten = true;
                Logger.Warning("[CityBankers] BAG ORIGIN evidence saved; duplicate identity observed after " +
                    observed.MessageType + ". This does not establish which slot is physical.");
            }
        }
        private static void Write(string kind, Observation[] observations)
        {
            // Fixed paths per character: bounded disk usage. Generation ties
            // files together; mismatched generations must not be compared as one run.
            RuntimeStateStore.WriteJsonAtomic(Path.Combine(directory, "bag-origin-" + token + "-" + kind + ".json"),
                new { format = "citybankers-bag-origin-v1", character = Client.CharacterName,
                    generation = connection, observedUtc = DateTime.UtcNow,
                    clientless = typeof(Client).Assembly.ManifestModule.ModuleVersionId,
                    plugin = typeof(BagOriginTrace).Assembly.ManifestModule.ModuleVersionId,
                    boundary = "Before is at MessageReceived; After is before the next message or on update. Includes native processing and synchronous event subscribers.",
                    moveRequests = Moves.ToArray(), observations });
        }
        private static void ReportFailure(Exception ex)
        {
            if (errorReported) return;
            errorReported = true;
            try { Logger.Warning("[CityBankers] Bag origin evidence unavailable: " + ex.GetType().Name); }
            catch { /* Diagnostic failure must not change the original operation. */ }
        }
    }
}
