using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Common;
using Serilog;
using SmokeLounge.AOtomation.Messaging.Messages.SystemMessages;

namespace CityDwellers.LoginTry
{
    internal static class Program
    {
        private const int Attempts = 9;

        private static int Main(string[] args)
        {
            if (ClientlessGameDataBootstrap.IsRestoreCommand(args))
                return ClientlessGameDataBootstrap.Run(args);
            if (args.Length != 3 || args.Any(string.IsNullOrWhiteSpace))
            {
                Console.WriteLine("Usage: logintry <aoaccount> <aopass> <aochar>");
                return 2;
            }
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            foreach (string file in new[] { "PlayfieldNames.json", "StaticDynelData.bin", "SkillTrickle.json", "ItemData.bin", "ItemData.idx" })
            {
                if (File.Exists(Path.Combine("GameData", file))) continue;
                Console.WriteLine("Missing GameData/" + file + ". Rebuild LoginTry to restore game data.");
                return 2;
            }

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "logintry-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                "-" + Process.GetCurrentProcess().Id + ".log");
            using (var log = new ProbeLog(path))
            {
                Console.CancelKeyPress += (sender, e) => { e.Cancel = true; log.Cancelled = true; };
                log.Write(0, "CONFIG attempts=9 dimension=RubiKa single_serial_driver=true plugins=none chat=false " +
                    "auto_reconnect=false reconnect_delay_ms=0 cooldown_ms=0 in_world_dwell_ms=0 login_timeout_ms=120000");
                log.Write(0, "SDK " + typeof(Client).Assembly.GetName().Version + "; file=" + path);
                log.Write(0, "LOGOUT means local Client.Disconnect plus domain teardown, not server logout acknowledgement. " +
                    "The next successful login tests server acceptance. SDK socket I/O may use its own threads.");
                var results = new List<AttemptResult>();
                bool cleanupFailed = false;
                long previousLogout = 0;
                for (int cycle = 1; cycle <= Attempts && !log.Cancelled; cycle++)
                {
                    AppDomain domain = null;
                    var result = new AttemptResult { Outcome = "setup-error" };
                    long creation = Stopwatch.GetTimestamp();
                    log.Write(cycle, "ATTEMPT_START");
                    try
                    {
                        // Fresh SDK static state each time, but never two live clients.
                        domain = AppDomain.CreateDomain("LoginTry-" + cycle, null, new AppDomainSetup
                        {
                            ApplicationBase = AppDomain.CurrentDomain.BaseDirectory,
                            ConfigurationFile = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile
                        });
                        var runner = (AttemptRunner)domain.CreateInstanceAndUnwrap(
                            typeof(AttemptRunner).Assembly.FullName, typeof(AttemptRunner).FullName);
                        result = runner.Run(args[0], args[1], args[2], cycle, log);
                    }
                    catch (Exception ex)
                    {
                        // No raw exception text or SDK logs: either can contain account data.
                        log.Write(cycle, "ATTEMPT_ERROR type=" + ex.GetType().Name);
                    }
                    finally
                    {
                        long unload = Stopwatch.GetTimestamp();
                        log.Write(cycle, "DOMAIN_UNLOAD_START");
                        try { if (domain != null) AppDomain.Unload(domain); }
                        catch (Exception ex)
                        {
                            cleanupFailed = true;
                            log.Write(cycle, "DOMAIN_UNLOAD_FAILED type=" + ex.GetType().Name + "; stopping to avoid overlapping clients");
                        }
                        result.UnloadMs = Milliseconds(unload, Stopwatch.GetTimestamp());
                        log.Write(cycle, "DOMAIN_UNLOAD_END elapsed_ms=" + Number(result.UnloadMs));
                    }
                    if (result.LoginStart > 0)
                    {
                        result.SetupMs = Milliseconds(creation, result.LoginStart);
                        if (previousLogout > 0) result.GapMs = Milliseconds(previousLogout, result.LoginStart);
                    }
                    if (result.LogoutEnd > 0) previousLogout = result.LogoutEnd;
                    results.Add(result);
                    log.Write(cycle, "RESULT outcome=" + result.Outcome +
                        " login_succeeded=" + result.LoginSucceeded +
                        " client_error=" + result.ClientError + " disconnect_error=" + result.DisconnectError +
                        " setup_ms=" + Number(result.SetupMs) + " login_ms=" + Number(result.LoginMs) +
                        " disconnect_call_ms=" + Number(result.LogoutMs) + " unload_ms=" + Number(result.UnloadMs) +
                        " previous_disconnect_to_login_start_ms=" + Number(result.GapMs));
                    if (cleanupFailed) break;
                    // Deliberately NO delay before the next attempt.
                }
                var successful = results.Where(r => r.LoginSucceeded).ToList();
                int clientErrors = results.Count(r => r.ClientError);
                int disconnectErrors = results.Count(r => r.DisconnectError);
                log.Write(0, "SUMMARY attempted=" + results.Count + " login_successful=" + successful.Count +
                    " login_failed=" + (results.Count - successful.Count) +
                    " client_errors=" + clientErrors + " disconnect_errors=" + disconnectErrors +
                    " cancelled=" + log.Cancelled +
                    " cleanup_failed=" + cleanupFailed);
                if (successful.Count > 0)
                {
                    double[] times = successful.Select(r => r.LoginMs).OrderBy(t => t).ToArray();
                    double median = (times[(times.Length - 1) / 2] + times[times.Length / 2]) / 2;
                    log.Write(0, "LOGIN_MS min=" + Number(times.First()) + " median=" + Number(median) +
                        " mean=" + Number(times.Average()) + " max=" + Number(times.Last()));
                }
                return successful.Count == Attempts && clientErrors == 0 && disconnectErrors == 0 &&
                    !cleanupFailed && !log.Cancelled ? 0 : 1;
            }
        }

        internal static double Milliseconds(long start, long end) => (end - start) * 1000.0 / Stopwatch.Frequency;
        internal static string Number(double value) => value < 0 ? "n/a" : value.ToString("F3", CultureInfo.InvariantCulture);
    }

    [Serializable]
    public sealed class AttemptResult
    {
        public string Outcome;
        public bool LoginSucceeded, ClientError, DisconnectError;
        public long LoginStart, LogoutEnd;
        public double SetupMs = -1, LoginMs = -1, LogoutMs = -1, UnloadMs = -1, GapMs = -1;
    }

    // Remoting helper inside the executable, not an AO# or clientless plugin.
    public sealed class AttemptRunner : MarshalByRefObject
    {
        private volatile bool _inPlay, _disconnected, _rejected;
        private long _inPlayAt;
        public override object InitializeLifetimeService() => null;

        public AttemptResult Run(string account, string password, string character, int cycle, ProbeLog log)
        {
            var result = new AttemptResult { Outcome = "login-timeout" };
            Client.Config.AutoReconnect = false;
            Client.Config.ReconnectDelay = 0;
            var updateMethod = typeof(Client).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(double) }, null);
            if (updateMethod == null) throw new MissingMethodException("Client.Update(double)");
            var update = (Action<double>)Delegate.CreateDelegate(typeof(Action<double>), updateMethod);
            Client.CharacterInPlay += first =>
            {
                if (_inPlay) return;
                _inPlayAt = Stopwatch.GetTimestamp();
                _inPlay = true;
                log.Write(cycle, "CHARACTER_IN_PLAY");
            };
            Client.Disconnected += () => { _disconnected = true; log.Write(cycle, "DISCONNECTED_EVENT"); };
            Client.MessageReceived += (sender, message) =>
            {
                string type = message?.Body?.GetType().Name;
                if (type == "ServerSaltMessage" || type == "CharacterListMessage" || type == "FullCharacterMessage")
                    log.Write(cycle, "RECEIVED " + type);
                if (message?.Body is CharacterListMessage characters &&
                    !characters.Characters.Any(c => string.Equals(c.Name, character, StringComparison.Ordinal)))
                {
                    _rejected = true;
                    log.Write(cycle, "CHARACTER_NOT_FOUND exact_name_match_required=true");
                }
                if (type == "LoginErrorMessage")
                {
                    _rejected = true;
                    var error = message.Body as LoginErrorMessage;
                    log.Write(cycle, "LOGIN_REJECTED code=" +
                        (error == null ? "unknown" : error.Error.ToString()));
                }
            };
            // Discard SDK text rather than risk writing passwords or auth challenge material.
            var logger = new LoggerConfiguration().CreateLogger();
            result.LoginStart = Stopwatch.GetTimestamp();
            log.Write(cycle, "LOGIN_START");
            try
            {
                Client.UseCurrentDomain(account, password, character, Dimension.RubiKa, logger,
                    useBuiltInLooper: false, useChat: false);
                long last = Stopwatch.GetTimestamp();
                while (!_inPlay && !_disconnected && !_rejected && !log.Cancelled &&
                    Program.Milliseconds(result.LoginStart, Stopwatch.GetTimestamp()) < 120000)
                {
                    long now = Stopwatch.GetTimestamp();
                    update((now - last) / (double)Stopwatch.Frequency);
                    last = now;
                    if (!_inPlay && !_disconnected && !_rejected) Thread.Sleep(1); // pump pacing, not relog cooldown
                }
                result.Outcome = _inPlay ? "in-play" : log.Cancelled ? "cancelled" :
                    _rejected ? "login-rejected" : _disconnected ? "disconnected-before-in-play" : "login-timeout";
                if (_inPlay) result.LoginMs = Program.Milliseconds(result.LoginStart, _inPlayAt);
                log.Write(cycle, "LOGIN_END outcome=" + result.Outcome +
                    " elapsed_ms=" + Program.Number(Program.Milliseconds(result.LoginStart, Stopwatch.GetTimestamp())));
            }
            catch (Exception ex)
            {
                result.ClientError = true;
                result.Outcome = "client-error-" + ex.GetType().Name;
                log.Write(cycle, "CLIENT_ERROR type=" + ex.GetType().Name);
            }
            finally
            {
                // Reaching in-play is independent of later pump/disconnect errors.
                result.LoginSucceeded = _inPlay;
                if (_inPlay) result.LoginMs = Program.Milliseconds(result.LoginStart, _inPlayAt);
                long start = Stopwatch.GetTimestamp();
                log.Write(cycle, "LOGOUT_START");
                try { Client.Disconnect(); }
                catch (Exception ex)
                {
                    result.DisconnectError = true;
                    result.Outcome += "/disconnect-error-" + ex.GetType().Name;
                    log.Write(cycle, "DISCONNECT_ERROR type=" + ex.GetType().Name);
                }
                result.LogoutEnd = Stopwatch.GetTimestamp();
                result.LogoutMs = Program.Milliseconds(start, result.LogoutEnd);
                log.Write(cycle, "LOGOUT_LOCAL_END elapsed_ms=" + Program.Number(result.LogoutMs));
                logger.Dispose();
            }
            return result;
        }
    }

    public sealed class ProbeLog : MarshalByRefObject, IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _sync = new object();
        private volatile bool _cancelled;
        public bool Cancelled { get => _cancelled; set => _cancelled = value; }
        public ProbeLog(string path) { _writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true }; }
        public override object InitializeLifetimeService() => null;
        public void Write(int cycle, string message)
        {
            lock (_sync)
            {
                string line = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " +" +
                    _clock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) +
                    "ms cycle=" + cycle + " " + message;
                _writer.WriteLine(line);
                Console.WriteLine(line);
            }
        }
        public void Dispose() { lock (_sync) _writer.Dispose(); }
    }
}
