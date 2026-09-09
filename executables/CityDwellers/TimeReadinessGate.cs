using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace CityDwellers.Host
{
    internal sealed class HostSettings
    {
        public bool RequireTrustedTime = true;
        public List<string> NtpServers = new List<string>();
        public int MinimumNtpResponses = 1;
        public int MaximumClockSkewSeconds = 120;
        public int NtpTimeoutMilliseconds = 3000;
        public int RetrySeconds = 30;
        public int WindowsResyncEverySeconds = 300;
        public global::ManagerHost.Config Manager;
        public global::FlipperLoader.Config Flipper;
        public global::BuddiesHost.Config Buddies;
        public global::BankerLoader.BankerConfig Bankers;

        public static HostSettings CreateDefault()
        {
            return new HostSettings
            {
                RequireTrustedTime = true,
                NtpServers = new List<string>
                {
                    "time.cloudflare.com",
                    "time.google.com",
                    "time.windows.com"
                },
                MinimumNtpResponses = 1,
                MaximumClockSkewSeconds = 120,
                NtpTimeoutMilliseconds = 3000,
                RetrySeconds = 30,
                WindowsResyncEverySeconds = 300,
                Manager = new global::ManagerHost.Config
                {
                    Accounts = new List<global::ManagerHost.AccountInfo>
                    {
                        new global::ManagerHost.AccountInfo
                        {
                            Username = "user1",
                            Password = "pass1",
                            Character = "char1"
                        }
                    },
                    Bot = null
                },
                Flipper = new global::FlipperLoader.Config
                {
                    Accounts = new List<global::FlipperLoader.AccountInfo>
                    {
                        new global::FlipperLoader.AccountInfo
                        {
                            Username = "user1",
                            Password = "pass1",
                            Character = "char1"
                        }
                    },
                    ProbeTimeoutMs = 20000,
                    DelayBetweenPassesMs = 5000,
                    CacheFreshSeconds = 60
                },
                Buddies = new global::BuddiesHost.Config
                {
                    AccountPrefix = "user",
                    AccountCount = 13,
                    ActiveLimit = 12,
                    MaxParallelLogins = 4,
                    Password = "pass1"
                },
                Bankers = new global::BankerLoader.BankerConfig
                {
                    Password = "pass1",
                    MaxParallelLogins = 32,
                    DiagnosticTimeoutMs = 30000,
                    AcceptancePolicy = new global::BankerLoader.AcceptancePolicyConfig
                    {
                        SymbiantMaxCopies = 10,
                        SpiritMaxCopies = 5,
                        Items = new Dictionary<string, global::BankerLoader.AcceptanceItemConfig>()
                    },
                    Roles = new Dictionary<string, global::BankerLoader.AccountMapping>
                    {
                        { "central", NewBankerPlaceholder("central") },
                        { "artillery", NewBankerPlaceholder("artillery") },
                        { "infantry", NewBankerPlaceholder("infantry") },
                        { "control", NewBankerPlaceholder("control") },
                        { "support", NewBankerPlaceholder("support") },
                        { "extermination", NewBankerPlaceholder("extermination") },
                        { "spirit", NewBankerAccount("kbspirit", "Kbspirit") },
                        { "dyna", NewBankerAccount("kbdyna", "Kbdyna") },
                        { "phatz", NewBankerAccount("kbphatz", "Kbphatz") }
                    }
                }
            };
        }

        private static global::BankerLoader.AccountMapping NewBankerPlaceholder(
            string role)
        {
            return new global::BankerLoader.AccountMapping
            {
                Username = "account-" + role,
                Character = "character-" + role
            };
        }

        private static global::BankerLoader.AccountMapping NewBankerAccount(
            string username,
            string character)
        {
            return new global::BankerLoader.AccountMapping
            {
                Username = username,
                Character = character
            };
        }

        public static bool TryValidate(HostSettings settings, out string error)
        {
            if (settings == null)
            {
                error = "the file is empty";
                return false;
            }

            if (settings.Manager == null ||
                settings.Flipper == null ||
                settings.Buddies == null ||
                settings.Bankers == null)
            {
                error =
                    "Manager, Flipper, Buddies, and Bankers sections are all required";
                return false;
            }

            if (!settings.RequireTrustedTime)
            {
                error = null;
                return true;
            }

            if (settings.NtpServers == null || settings.NtpServers.Count == 0)
            {
                error = "NtpServers requires at least one server";
                return false;
            }

            if (settings.NtpServers.Any(string.IsNullOrWhiteSpace))
            {
                error = "NtpServers cannot contain an empty name";
                return false;
            }

            if (settings.MinimumNtpResponses <= 0 ||
                settings.MinimumNtpResponses > settings.NtpServers.Count)
            {
                error = "MinimumNtpResponses must be between 1 and the NtpServers count";
                return false;
            }

            if (settings.MaximumClockSkewSeconds < 1)
            {
                error = "MaximumClockSkewSeconds must be positive";
                return false;
            }

            if (settings.NtpTimeoutMilliseconds < 250 ||
                settings.NtpTimeoutMilliseconds > 30000)
            {
                error = "NtpTimeoutMilliseconds must be between 250 and 30000";
                return false;
            }

            if (settings.RetrySeconds < 1 || settings.RetrySeconds > 3600)
            {
                error = "RetrySeconds must be between 1 and 3600";
                return false;
            }

            if (settings.WindowsResyncEverySeconds < 30 ||
                settings.WindowsResyncEverySeconds > 86400)
            {
                error = "WindowsResyncEverySeconds must be between 30 and 86400";
                return false;
            }

            error = null;
            return true;
        }
    }

    internal sealed class TimeReadinessGate
    {
        private static readonly DateTime NtpEpochUtc =
            new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly HostSettings _settings;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private TimeSpan _nextWindowsResync = TimeSpan.Zero;

        public TimeReadinessGate(HostSettings settings)
        {
            _settings = settings;
        }

        public bool WaitUntilReady(WaitHandle stopSignal)
        {
            RuntimeLog.Write(
                "Waiting for independent network-time confirmation before AO startup.");

            while (!stopSignal.WaitOne(0))
            {
                DateTime networkUtc;
                string detail;
                if (TryReadNetworkTime(out networkUtc, out detail))
                {
                    DateTime systemUtc = DateTime.UtcNow;
                    double skewSeconds = Math.Abs(
                        (systemUtc - networkUtc).TotalSeconds);

                    if (skewSeconds <= _settings.MaximumClockSkewSeconds)
                    {
                        RuntimeLog.Write(
                            "NTP confirmed UTC; system skew=" +
                            skewSeconds.ToString("F1") + "s; " + detail + ".");
                        return true;
                    }

                    RuntimeLog.Write(
                        "System UTC is not trustworthy yet; skew=" +
                        skewSeconds.ToString("F1") + "s; " + detail + ".");
                }
                else
                {
                    RuntimeLog.Write(
                        "Independent network time is unavailable: " + detail + ".");
                }

                if (_elapsed.Elapsed >= _nextWindowsResync)
                {
                    RequestWindowsTimeResync();
                    _nextWindowsResync = _elapsed.Elapsed.Add(
                        TimeSpan.FromSeconds(
                            _settings.WindowsResyncEverySeconds));
                }

                if (stopSignal.WaitOne(
                    TimeSpan.FromSeconds(_settings.RetrySeconds)))
                {
                    return false;
                }
            }

            return false;
        }

        private bool TryReadNetworkTime(
            out DateTime networkUtc,
            out string detail)
        {
            var observations = new List<NtpObservation>();
            var failures = new List<string>();

            foreach (string server in _settings.NtpServers)
            {
                DateTime observedUtc;
                string error;
                if (TryQueryNtp(server, out observedUtc, out error))
                {
                    observations.Add(
                        new NtpObservation
                        {
                            Server = server,
                            Utc = observedUtc
                        });
                }
                else
                {
                    failures.Add(server + ": " + error);
                }
            }

            if (observations.Count < _settings.MinimumNtpResponses)
            {
                networkUtc = default(DateTime);
                detail =
                    observations.Count + "/" + _settings.NtpServers.Count +
                    " NTP responses; " + string.Join("; ", failures);
                return false;
            }

            observations.Sort((left, right) => left.Utc.CompareTo(right.Utc));
            networkUtc = observations[observations.Count / 2].Utc;
            detail =
                observations.Count + "/" + _settings.NtpServers.Count +
                " NTP responses via " +
                string.Join(", ", observations.Select(item => item.Server));
            return true;
        }

        private bool TryQueryNtp(
            string server,
            out DateTime observedUtc,
            out string error)
        {
            observedUtc = default(DateTime);
            error = null;

            try
            {
                IPAddress address = Dns.GetHostAddresses(server)
                    .FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork);
                if (address == null)
                {
                    error = "no IPv4 address";
                    return false;
                }

                byte[] request = new byte[48];
                request[0] = 0x1B;

                using (var client = new UdpClient(AddressFamily.InterNetwork))
                {
                    client.Client.ReceiveTimeout = _settings.NtpTimeoutMilliseconds;
                    client.Connect(new IPEndPoint(address, 123));
                    client.Send(request, request.Length);

                    IPEndPoint remote = null;
                    byte[] response = client.Receive(ref remote);
                    if (response == null || response.Length < 48)
                    {
                        error = "short response";
                        return false;
                    }

                    int leapIndicator = (response[0] >> 6) & 0x03;
                    int mode = response[0] & 0x07;
                    int stratum = response[1];
                    if (leapIndicator == 3 ||
                        (mode != 4 && mode != 5) ||
                        stratum == 0 ||
                        stratum > 15)
                    {
                        error = "server reported unsynchronized time";
                        return false;
                    }

                    ulong wholeSeconds = ReadUInt32(response, 40);
                    ulong fractional = ReadUInt32(response, 44);
                    double seconds = wholeSeconds +
                        fractional / 4294967296.0;
                    observedUtc = NtpEpochUtc.AddSeconds(seconds);
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) |
                   ((uint)bytes[offset + 1] << 16) |
                   ((uint)bytes[offset + 2] << 8) |
                   bytes[offset + 3];
        }

        private static void RequestWindowsTimeResync()
        {
            try
            {
                RuntimeLog.Write("Requesting Windows Time rediscovery and resynchronization.");
                using (Process process = Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = "w32tm.exe",
                        Arguments = "/resync /rediscover",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }))
                {
                    if (process == null)
                        return;

                    if (!process.WaitForExit(15000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        RuntimeLog.Write("Windows Time resync command timed out.");
                        return;
                    }

                    string output = process.StandardOutput.ReadToEnd().Trim();
                    string error = process.StandardError.ReadToEnd().Trim();
                    RuntimeLog.Write(
                        "Windows Time resync exit=" + process.ExitCode +
                        (string.IsNullOrWhiteSpace(output) ? string.Empty : "; " + output) +
                        (string.IsNullOrWhiteSpace(error) ? string.Empty : "; " + error));
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Unable to request Windows Time resync: " + ex.Message);
            }
        }

        private sealed class NtpObservation
        {
            public string Server;
            public DateTime Utc;
        }
    }
}
