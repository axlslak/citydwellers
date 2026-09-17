using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Shared
{
    // Only this background consumer performs DNS or network I/O. The game/logging
    // threads only enqueue; a broken receiver cannot hold up a bot.
    internal sealed class SyslogSender : IDisposable
    {
        private readonly SyslogSettings _settings;
        private readonly Action<string> _status;
        private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(1024);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly string _hostname = Header(Environment.MachineName, 255);
        private readonly string _pid = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
        private readonly Task _worker;
        private long _dropped;
        private TcpClient _tcp;
        private UdpClient _udp;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        public static SyslogSender Create(string runtimeDirectory, Action<string> status)
        {
            string path = Path.Combine(runtimeDirectory, "citydwellers.json");
            if (!File.Exists(path)) return null;
            var token = JObject.Parse(File.ReadAllText(path)).GetValue("Syslog", StringComparison.OrdinalIgnoreCase);
            var settings = token == null || token.Type == JTokenType.Null ? new SyslogSettings() : token.ToObject<SyslogSettings>();
            if (settings == null || !settings.Enabled) return null;
            if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port < 1 || settings.Port > 65535 ||
                (!string.Equals(settings.Transport, "tcp", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(settings.Transport, "udp", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Enabled Syslog requires Host, Port 1..65535 and Transport tcp or udp.");
            settings.Host = settings.Host.Trim();
            return new SyslogSender(settings, status);
        }

        private SyslogSender(SyslogSettings settings, Action<string> status)
        {
            _settings = settings;
            _status = status;
            _worker = Task.Run(() => Run());
        }

        // Only Manager calls this after accepting a structured report.
        public void Enqueue(ServiceEvent report)
        {
            if (report == null || _queue.IsAddingCompleted) return;
            int severity = report.Severity == "error" ? 3 : report.Severity == "warning" ? 4 : 6;
            string message = "<" + (128 + severity) + ">1 " + report.TimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) +
                " " + _hostname + " citydwellers " + _pid + " " + Header(report.Event, 32) + " - (" +
                report.Character + "[" + (report.CharacterId > 0 ? report.CharacterId.ToString(CultureInfo.InvariantCulture) : "unknown") +
                "]) " + report.Severity + " " + report.Event + " " +
                Newtonsoft.Json.JsonConvert.SerializeObject(report);
            try { if (!_queue.TryAdd(message)) Interlocked.Increment(ref _dropped); }
            catch (InvalidOperationException) { Interlocked.Increment(ref _dropped); }
        }

        private async Task Run()
        {
            string pending = null;
            bool failed = false;
            var report = Stopwatch.StartNew();
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    if (report.ElapsedMilliseconds >= 30000)
                    {
                        long lost = Interlocked.Exchange(ref _dropped, 0);
                        if (lost > 0) _status("WARNING: Syslog queue full; " + lost + " remote lines dropped. Manager event file retained.");
                        report.Restart();
                    }
                    if (pending == null && !_queue.TryTake(out pending, 250, _stop.Token))
                    {
                        if (_queue.IsCompleted) break;
                        continue;
                    }
                    try
                    {
                        byte[] payload = Utf8.GetBytes(pending);
                        if (string.Equals(_settings.Transport, "tcp", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_tcp == null)
                            {
                                _tcp = new TcpClient();
                                await Bounded(_tcp.ConnectAsync(_settings.Host, _settings.Port)).ConfigureAwait(false);
                            }
                            // RFC6587 octet counting uses UTF-8 BYTES, not characters.
                            byte[] prefix = Encoding.ASCII.GetBytes(payload.Length.ToString(CultureInfo.InvariantCulture) + " ");
                            byte[] frame = new byte[prefix.Length + payload.Length];
                            Buffer.BlockCopy(prefix, 0, frame, 0, prefix.Length);
                            Buffer.BlockCopy(payload, 0, frame, prefix.Length, payload.Length);
                            await Bounded(_tcp.GetStream().WriteAsync(frame, 0, frame.Length, _stop.Token)).ConfigureAwait(false);
                        }
                        else
                        {
                            if (payload.Length > 60000)
                            {
                                _status("WARNING: Syslog event exceeds UDP size limit; retained in Manager event file. Use TCP for large events.");
                                pending = null;
                                continue;
                            }
                            if (_udp == null)
                            {
                                _udp = new UdpClient();
                                // DNS resolution is on this consumer, never on a game thread.
                                _udp.Connect(_settings.Host, _settings.Port);
                            }
                            await Bounded(_udp.SendAsync(payload, payload.Length)).ConfigureAwait(false);
                        }
                        pending = null;
                        if (failed) _status("Syslog transport recovered; queued forwarding resumed.");
                        failed = false;
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        CloseTransport();
                        if (!failed)
                        {
                            var socket = ex as SocketException;
                            string detail = socket == null ? ex.GetType().Name + ": " + ex.Message :
                                socket.SocketErrorCode + " (native " + socket.NativeErrorCode + "): " + socket.Message;
                            _status("WARNING: Syslog " + _settings.Transport + " " + _settings.Host + ":" +
                                _settings.Port + " unavailable: " + detail +
                                ". Retrying in background; Manager event file continues.");
                        }
                        failed = true;
                        await Task.Delay(5000, _stop.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally { CloseTransport(); }
        }

        private async Task Bounded(Task operation)
        {
            if (await Task.WhenAny(operation, Task.Delay(3000, _stop.Token)).ConfigureAwait(false) != operation)
            {
                // A closed timed-out socket may fault later: observe that task too.
                _ = operation.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                _stop.Token.ThrowIfCancellationRequested();
                throw new TimeoutException("Syslog transport timed out.");
            }
            await operation.ConfigureAwait(false);
        }

        private void CloseTransport()
        {
            _tcp?.Close(); _tcp = null;
            _udp?.Close(); _udp = null;
        }

        private static string Header(string value, int maximum) => new string(
            value.Where(c => c >= 33 && c <= 126).Take(maximum).ToArray());

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (!_worker.Wait(1500)) _stop.Cancel();
            // Worker owns socket disposal. Never block shutdown on DNS/network.
        }
    }
}
