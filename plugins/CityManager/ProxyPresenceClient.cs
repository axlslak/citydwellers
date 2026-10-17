using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CityDwellers.Shared;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    internal sealed class ProxyPresenceSettings
    {
        public bool Enabled;
        public string Host;
        public int Port = 9993;
        public int ReconnectSeconds = 5;
    }

    internal sealed class ProxyPresenceEvent
    {
        public bool? Ready;
        public string Message, MissingName;
        public PlayerPresenceReport Report;
    }

    // A worker-service TCP connection. All AO logins stay inside kworker.
    // This never forwards tells or modifies Client.Chat.
    internal sealed class ProxyPresenceClient : IDisposable
    {
        private sealed class Session
        {
            internal readonly TcpClient Socket = new TcpClient();
            internal readonly SemaphoreSlim WriteLock = new SemaphoreSlim(1);
            internal readonly SemaphoreSlim Work = new SemaphoreSlim(0);
            internal readonly ConcurrentQueue<string> Outgoing = new ConcurrentQueue<string>();
            internal readonly ConcurrentDictionary<string, byte> Requested =
                new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            internal readonly ConcurrentDictionary<uint, string> Names = new ConcurrentDictionary<uint, string>();
            internal long ReceivedTicks = DateTime.UtcNow.Ticks;
            internal volatile bool Ready;
        }

        private readonly ProxyPresenceSettings _settings;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly BlockingCollection<ProxyPresenceEvent> _events = new BlockingCollection<ProxyPresenceEvent>(8192);
        private volatile Session _session;
        internal ProxyPresenceClient(ProxyPresenceSettings settings) { _settings = settings; }
        internal void Start() { Task.Run(Run); }
        internal bool TryRead(out ProxyPresenceEvent value) => _events.TryTake(out value);
        internal bool Connecting { get { var session = _session; return session != null && !session.Ready; } }

        internal void Track(string name)
        {
            var session = _session;
            if (session == null) return;
            lock (session)
            {
                if (!session.Ready || !session.Requested.TryAdd(name, 0)) return;
                session.Outgoing.Enqueue(name);
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
                        // Connection/handshake deadline, not a delay before work.
                        cancel.CancelAfter(TimeSpan.FromSeconds(20));
                        await session.Socket.ConnectAsync(_settings.Host, _settings.Port).ConfigureAwait(false);
                        session.Socket.NoDelay = true;
                        await Send(session, 100, Body(p => p.Text("capabilities")), cancel.Token).ConfigureAwait(false);
                        pumps = new[] { Receive(session, cancel), WriteRequests(session, cancel.Token), Ping(session, cancel.Token) };
                        var completed = await Task.WhenAny(pumps).ConfigureAwait(false);
                        await completed.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { failure = "Proxy connection or handshake timed out."; }
                    catch (Exception ex)
                    {
                        // Never include raw packets in diagnostics.
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
                    case 0:
                    case 5:
                    case 6:
                    case 7:
                        throw new InvalidDataException("Proxy is in Nady leader mode; start kworker with KWORKER_PROXY_MODE=workers.");
                    case 100:
                        string text = packet.Text();
                        if (session.Ready || !text.StartsWith("{", StringComparison.Ordinal)) break;
                        JObject capabilities = JObject.Parse(text);
                        if ((string)capabilities["name"] != "kworker-nady-proxy" || (string)capabilities["type"] != "capabilities")
                            throw new InvalidDataException("Endpoint did not identify itself as kworker-nady-proxy.");
                        if ((string)capabilities["connection-mode"] != "workers")
                            throw new InvalidDataException("Proxy requires worker-only mode: KWORKER_PROXY_MODE=workers.");
                        session.Ready = true;
                        cancel.CancelAfter(Timeout.Infinite);
                        Publish(new ProxyPresenceEvent { Ready = true, Message = "Proxy buddy tracking connected." }, cancel.Token);
                        break;
                    case 21:
                        uint id = packet.UInt();
                        string lookedUp = packet.Text();
                        if (!session.Ready || !session.Requested.ContainsKey(lookedUp)) break;
                        if (id == 0 || id == uint.MaxValue)
                            Publish(new ProxyPresenceEvent { MissingName = lookedUp }, cancel.Token);
                        else
                        {
                            if (session.Names.TryAdd(id, lookedUp))
                                await AddBuddy(session, id, cancel.Token).ConfigureAwait(false);
                        }
                        break;
                    case 40:
                        uint buddy = packet.UInt();
                        bool online = packet.UInt() != 0;
                        string reporter = packet.Remaining == 0 ? "worker" : packet.Text();
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
                string name;
                if (!session.Outgoing.TryDequeue(out name)) continue;
                // Resolve once per connection, including saved names. A historical
                // UID must not answer for a name that has since been changed/reused.
                // AO client lookup has ONLY a name; the server reply adds UID.
                await Send(session, 21, Body(p => p.Text(name)), token).ConfigureAwait(false);
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
                if (session.Ready)
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
