using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AOSharp.Clientless.Common;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    internal sealed class ProxyPresenceSettings
    {
        public bool Enabled;
        public string Host, Username, Password, Character;
        public int Port = 9993;
        public int ReconnectSeconds = 5;
    }

    internal sealed class ProxyPresenceEvent
    {
        public bool? Ready;
        public string Message, MissingName;
        public PlayerPresenceReport Report;
    }

    // A chat-only tool connection. It never logs a game character in, forwards
    // tells or modifies Client.Chat. The C proxy chooses every buddy's worker.
    internal sealed class ProxyPresenceClient : IDisposable
    {
        private sealed class Session
        {
            internal readonly TcpClient Socket = new TcpClient();
            internal readonly SemaphoreSlim WriteLock = new SemaphoreSlim(1);
            internal readonly SemaphoreSlim Work = new SemaphoreSlim(0);
            internal readonly ConcurrentQueue<PlayerSeenRecord> Outgoing = new ConcurrentQueue<PlayerSeenRecord>();
            internal readonly ConcurrentDictionary<string, byte> Requested =
                new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            internal readonly ConcurrentDictionary<uint, string> Names = new ConcurrentDictionary<uint, string>();
            internal long ReceivedTicks = DateTime.UtcNow.Ticks;
            internal volatile bool Ready, LoggedIn;
            internal bool Selected;
        }

        private readonly ProxyPresenceSettings _settings;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly BlockingCollection<ProxyPresenceEvent> _events = new BlockingCollection<ProxyPresenceEvent>(8192);
        private volatile Session _session;
        internal ProxyPresenceClient(ProxyPresenceSettings settings) { _settings = settings; }
        internal void Start() { Task.Run(Run); }
        internal bool TryRead(out ProxyPresenceEvent value) => _events.TryTake(out value);
        internal bool Connecting { get { var session = _session; return session != null && !session.Ready; } }

        internal void Track(PlayerSeenRecord record)
        {
            var session = _session;
            if (session == null) return;
            lock (session)
            {
                if (!session.Ready || !session.Requested.TryAdd(record.Name, 0)) return;
                session.Outgoing.Enqueue(record);
                session.Work.Release();
            }
        }

        private void Publish(ProxyPresenceEvent value, CancellationToken token) => _events.Add(value, token);

        private async Task Run()
        {
            while (!_stop.IsCancellationRequested)
            {
                var session = new Session();
                _session = session;
                string failure = "Proxy presence is disconnected.";
                using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                using (cancel.Token.Register(() => session.Socket.Close()))
                {
                    Task[] pumps = new Task[0];
                    try
                    {
                        // Deadline for connecting/authenticating, not a delay before work.
                        cancel.CancelAfter(TimeSpan.FromSeconds(20));
                        await session.Socket.ConnectAsync(_settings.Host, _settings.Port).ConfigureAwait(false);
                        session.Socket.NoDelay = true;
                        pumps = new[] { Receive(session, cancel), WriteRequests(session, cancel.Token), Ping(session, cancel.Token) };
                        var completed = await Task.WhenAny(pumps).ConfigureAwait(false);
                        await completed.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { failure = "Proxy connection or login timed out."; }
                    catch (Exception ex)
                    {
                        // Never include packets, credentials or the challenge response.
                        failure = ex is InvalidDataException ? ex.Message : "Proxy connection failed (" + ex.GetType().Name + ").";
                    }
                    finally
                    {
                        lock (session) session.Ready = false;
                        cancel.Cancel();
                        session.Socket.Close();
                        foreach (var pump in pumps)
                            try { await pump.ConfigureAwait(false); } catch { }
                        lock (session)
                        {
                            session.Ready = false;
                            _session = null;
                            session.WriteLock.Dispose();
                            session.Work.Dispose();
                        }
                    }
                }
                if (_stop.IsCancellationRequested) break;
                Publish(new ProxyPresenceEvent { Ready = false, Message = failure }, _stop.Token);
                try { await Task.Delay(TimeSpan.FromSeconds(_settings.ReconnectSeconds), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task Receive(Session session, CancellationTokenSource cancel)
        {
            var stream = session.Socket.GetStream();
            while (true)
            {
                byte[] header = await Read(stream, 4, cancel.Token).ConfigureAwait(false);
                int type = (header[0] << 8) | header[1];
                int length = (header[2] << 8) | header[3];
                byte[] body = await Read(stream, length, cancel.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref session.ReceivedTicks, DateTime.UtcNow.Ticks);
                var packet = new Packet(body);
                switch (type)
                {
                    case 0: // AO server challenge
                        if (session.LoggedIn) throw new InvalidDataException("Unexpected proxy login challenge.");
                        byte[] salt = packet.Bytes();
                        string key = LoginEncryption.MakeChallengeResponse(new Credentials(_settings.Username, _settings.Password), salt);
                        await Send(session, 2, Body(p => { p.UInt(0); p.Text(_settings.Username); p.Text(key); }), cancel.Token).ConfigureAwait(false);
                        break;
                    case 7: // Account character list: ID array, name array, level array, online array
                        int count = packet.Short();
                        var ids = new uint[count];
                        for (int i = 0; i < count; i++) ids[i] = packet.UInt();
                        int names = packet.Short();
                        uint selected = 0;
                        for (int i = 0; i < names; i++)
                        {
                            string name = packet.Text();
                            if (i < count && string.Equals(name, _settings.Character, StringComparison.OrdinalIgnoreCase)) selected = ids[i];
                        }
                        if (selected == 0) throw new InvalidDataException("Configured proxy chat character was not found on its account.");
                        session.Selected = true;
                        await Send(session, 3, Body(p => p.UInt(selected)), cancel.Token).ConfigureAwait(false);
                        break;
                    case 5:
                        // Extra empty LoginOK packets announce workers. They must
                        // not restart login, clear observations or replay tracking.
                        if (!session.Selected || session.LoggedIn) break;
                        session.LoggedIn = true;
                        await Send(session, 100, Body(p => p.Text("capabilities")), cancel.Token).ConfigureAwait(false);
                        break;
                    case 6:
                        throw new InvalidDataException("Proxy AO chat login was refused.");
                    case 100:
                        string text = packet.Text();
                        if (!session.LoggedIn || session.Ready || !text.StartsWith("{", StringComparison.Ordinal)) break;
                        JObject capabilities = JObject.Parse(text);
                        if ((string)capabilities["name"] != "kworker-nady-proxy" || (string)capabilities["type"] != "capabilities")
                            throw new InvalidDataException("Endpoint did not identify itself as kworker-nady-proxy.");
                        session.Ready = true;
                        cancel.CancelAfter(Timeout.Infinite);
                        Publish(new ProxyPresenceEvent { Ready = true, Message = "Proxy buddy tracking connected." }, cancel.Token);
                        break;
                    case 20:
                    case 21:
                        uint id = packet.UInt();
                        string lookedUp = packet.Text();
                        if (!session.Ready || !session.Requested.ContainsKey(lookedUp)) break;
                        if (id == 0 || id == uint.MaxValue)
                            Publish(new ProxyPresenceEvent { MissingName = lookedUp }, cancel.Token);
                        else
                        {
                            session.Names[id] = lookedUp;
                            await AddBuddy(session, id, cancel.Token).ConfigureAwait(false);
                        }
                        break;
                    case 40:
                        uint buddy = packet.UInt();
                        bool online = packet.UInt() != 0;
                        string reporter = packet.Remaining == 0 ? "leader" : packet.Text();
                        string tracked;
                        if (session.Ready && session.Names.TryGetValue(buddy, out tracked))
                            Publish(new ProxyPresenceEvent { Report = new PlayerPresenceReport {
                                Name = tracked, CharacterId = buddy, Online = online,
                                ObservedUtc = DateTime.UtcNow, Reporter = reporter.Length <= 256 ? reporter : reporter.Substring(0, 256)
                            } }, cancel.Token);
                        break;
                    case 41:
                        uint removed = packet.UInt();
                        string removedName;
                        if (session.Names.TryRemove(removed, out removedName))
                        {
                            byte ignored;
                            session.Requested.TryRemove(removedName, out ignored);
                            // Removal is not an offline report. Force a fresh add.
                            Publish(new ProxyPresenceEvent { MissingName = removedName, Message = "removed" }, cancel.Token);
                        }
                        break;
                }
            }
        }

        private async Task WriteRequests(Session session, CancellationToken token)
        {
            while (true)
            {
                await session.Work.WaitAsync(token).ConfigureAwait(false);
                PlayerSeenRecord record;
                if (!session.Outgoing.TryDequeue(out record)) continue;
                if (record.CharacterId != 0)
                {
                    session.Names[record.CharacterId] = record.Name;
                    await AddBuddy(session, record.CharacterId, token).ConfigureAwait(false);
                }
                else
                    // AO client lookup has ONLY a name; the server reply adds UID.
                    await Send(session, 21, Body(p => p.Text(record.Name)), token).ConfigureAwait(false);
            }
        }

        private static Task AddBuddy(Session session, uint id, CancellationToken token) =>
            Send(session, 40, Body(p => { p.UInt(id); p.Text("\u0001"); }), token);

        private static async Task Ping(Session session, CancellationToken token)
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
                if (DateTime.UtcNow.Ticks - Interlocked.Read(ref session.ReceivedTicks) > TimeSpan.FromSeconds(120).Ticks)
                    throw new IOException("Proxy heartbeat was not answered.");
                if (session.LoggedIn)
                    await Send(session, 100, Body(p => p.Text("presence")), token).ConfigureAwait(false);
            }
        }

        private static async Task<byte[]> Read(NetworkStream stream, int count, CancellationToken token)
        {
            byte[] buffer = new byte[count];
            for (int offset = 0; offset < count;)
            {
                int received = await stream.ReadAsync(buffer, offset, count - offset, token).ConfigureAwait(false);
                if (received == 0) throw new EndOfStreamException();
                offset += received;
            }
            return buffer;
        }

        private static async Task Send(Session session, int type, byte[] body, CancellationToken token)
        {
            byte[] frame = new byte[body.Length + 4];
            frame[0] = (byte)(type >> 8); frame[1] = (byte)type;
            frame[2] = (byte)(body.Length >> 8); frame[3] = (byte)body.Length;
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            await session.WriteLock.WaitAsync(token).ConfigureAwait(false);
            try { await session.Socket.GetStream().WriteAsync(frame, 0, frame.Length, token).ConfigureAwait(false); }
            finally { session.WriteLock.Release(); }
        }

        private static byte[] Body(Action<Packet> write)
        {
            var packet = new Packet(); write(packet); return packet.Output.ToArray();
        }

        // AO chat frames use big-endian integers and 16-bit string byte lengths.
        private sealed class Packet
        {
            private static readonly Encoding Encoding = System.Text.Encoding.GetEncoding(28591);
            private readonly byte[] _input;
            private int _offset;
            internal readonly MemoryStream Output = new MemoryStream();
            internal Packet(byte[] input = null) { _input = input; }
            internal int Remaining => _input.Length - _offset;
            internal int Short()
            {
                Require(2); int value = (_input[_offset] << 8) | _input[_offset + 1]; _offset += 2; return value;
            }
            internal uint UInt()
            {
                Require(4); uint value = ((uint)_input[_offset] << 24) | ((uint)_input[_offset + 1] << 16) |
                    ((uint)_input[_offset + 2] << 8) | _input[_offset + 3]; _offset += 4; return value;
            }
            internal byte[] Bytes()
            {
                int count = Short(); Require(count); var value = new byte[count];
                Buffer.BlockCopy(_input, _offset, value, 0, count); _offset += count; return value;
            }
            internal string Text() => Encoding.GetString(Bytes());
            internal void UInt(uint value)
            {
                Output.WriteByte((byte)(value >> 24)); Output.WriteByte((byte)(value >> 16));
                Output.WriteByte((byte)(value >> 8)); Output.WriteByte((byte)value);
            }
            internal void Text(string value)
            {
                byte[] bytes = Encoding.GetBytes(value);
                if (bytes.Length > ushort.MaxValue) throw new InvalidDataException("AO chat field is too long.");
                Output.WriteByte((byte)(bytes.Length >> 8)); Output.WriteByte((byte)bytes.Length);
                Output.Write(bytes, 0, bytes.Length);
            }
            private void Require(int count)
            {
                if (_input == null || count > _input.Length - _offset) throw new InvalidDataException("Incomplete proxy packet.");
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _session?.Socket.Close();
        }
    }
}
