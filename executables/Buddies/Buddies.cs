using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;

using AOSharp.Clientless;
using AOSharp.Clientless.Common;

using Newtonsoft.Json;

using Serilog;
using Serilog.Core;
using CityDwellers.Shared;

public class PluginLoader
{
    private const string PipeName = "citydwellers-buddies";
    private const int WakeupTimeoutMs = 20000;
    private const int DefaultDemoLeaseSeconds = 60;
    private const int DefaultRaidSafetyLeaseSeconds = 1365;

    private static readonly object ActiveLock = new object();
    private static readonly Dictionary<int, ActiveBuddy> ActiveBuddies =
        new Dictionary<int, ActiveBuddy>();

    private static Config _config;
    private static string _baseDir;
    private static long _nextStartSequence;
    private static Timer _leaseTimer;

    static void Main(string[] args)
    {
        _baseDir = AppDomain.CurrentDomain.BaseDirectory;

        string settingsDirectory;
        string settingsError;

        if (!SettingsPaths.TryEnsureDirectory(out settingsDirectory, out settingsError))
        {
            StopForConfiguration(settingsError);
            Environment.Exit(1);
            return;
        }

        string configPath = SettingsPaths.GetFilePath(settingsDirectory, "buddies.json");

        if (!File.Exists(configPath))
        {
            string templateError;
            if (!SettingsPaths.TryCreateFile(
                    configPath,
                    BuildDefaultConfig(),
                    out templateError))
            {
                StopForConfiguration(templateError);
                Environment.Exit(1);
                return;
            }

            StopForConfiguration(
                $"Created a Buddies configuration template at '{configPath}'.\n" +
                "The user account prefix and pass1 password are examples and cannot log in. " +
                "Replace them, then start Buddies again.");
            return;
        }

        try
        {
            string configText = File.ReadAllText(configPath);
            _config = JsonConvert.DeserializeObject<Config>(configText);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load '{configPath}'.");
            Console.WriteLine(ex);
            StopForConfiguration("Correct buddies.json, then start Buddies again.");
            Environment.Exit(1);
            return;
        }

        if (!ValidateConfig(_config))
        {
            StopForConfiguration($"Correct '{configPath}', then start Buddies again.");
            Environment.Exit(1);
            return;
        }

        Console.WriteLine("======================================");
        Console.WriteLine(" City Dwellers - Buddies Service");
        Console.WriteLine("======================================");
        Console.WriteLine();
        Console.WriteLine(
            $"Accounts: {_config.AccountPrefix}0 .. " +
            $"{_config.AccountPrefix}{_config.AccountCount - 1}");
        Console.WriteLine("Character scheme: Apcr{level:000}{index:00}");
        Console.WriteLine($"Pipe: {PipeName}");
        Console.WriteLine();
        Console.WriteLine("Buddies service idle. Zero buddy AO sessions are started automatically.");
        Console.WriteLine("Waiting for Manager requests.");
        Console.WriteLine("Press ENTER to stop Buddies and unload any active buddies.");
        Console.WriteLine();

        Thread pipeThread = new Thread(RunPipeServer)
        {
            IsBackground = true,
            Name = "CityDwellers.Buddies.Pipe"
        };

        pipeThread.Start();

        _leaseTimer = new Timer(
            ExpireBuddyLeases,
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        Console.ReadLine();

        Console.WriteLine();
        Console.WriteLine("Stopping Buddies service...");
        _leaseTimer?.Dispose();
        _leaseTimer = null;
        ShutdownAll();
        Console.WriteLine("Buddies service stopped.");
    }

    private static string BuildDefaultConfig()
    {
        var config = new Config
        {
            AccountPrefix = "user",
            AccountCount = 12,
            Password = "pass1"
        };

        return JsonConvert.SerializeObject(config, Formatting.Indented);
    }

    private static void StopForConfiguration(string message)
    {
        Console.WriteLine(message);
        Console.WriteLine();
        Console.WriteLine("Press ENTER to exit.");
        Console.ReadLine();
    }

    private static bool ValidateConfig(Config config)
    {
        if (config == null)
        {
            Console.WriteLine("buddies.json is empty or invalid.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.AccountPrefix))
        {
            Console.WriteLine("buddies.json requires AccountPrefix.");
            return false;
        }

        if (config.AccountCount <= 0)
        {
            Console.WriteLine("buddies.json requires AccountCount > 0.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.Password))
        {
            Console.WriteLine("buddies.json requires Password.");
            return false;
        }

        if (string.Equals(config.AccountPrefix, "user", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(config.Password, "pass1", StringComparison.Ordinal))
        {
            Console.WriteLine(
                "buddies.json still contains the user/pass1 defaults. " +
                "Replace them with the Buddies account prefix and password.");
            return false;
        }

        return true;
    }

    private static void RunPipeServer()
    {
        while (true)
        {
            try
            {
                using (var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None))
                {
                    pipe.WaitForConnection();

                    var reader = new StreamReader(pipe);
                    var writer = new StreamWriter(pipe) { AutoFlush = true };

                    string line = reader.ReadLine();

                    WorkerResponse response;

                    try
                    {
                        WorkerRequest request =
                            JsonConvert.DeserializeObject<WorkerRequest>(line ?? string.Empty);

                        response = HandleRequest(request);
                    }
                    catch (Exception ex)
                    {
                        response = new WorkerResponse
                        {
                            Ok = false,
                            Message = $"Invalid Buddies request: {ex.Message}"
                        };
                    }

                    writer.WriteLine(JsonConvert.SerializeObject(response));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Buddies pipe server error: {ex}");
                Thread.Sleep(500);
            }
        }
    }

    private static WorkerResponse HandleRequest(WorkerRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Command))
        {
            return Fail(request, "Missing command.");
        }

        string command = request.Command.Trim().ToLowerInvariant();
        string quantityLabel =
            command == "spinup" || command == "spindown"
                ? $"count={request.Index}"
                : $"index={request.Index}";

        Console.WriteLine(
            $"IPC request {request.Id ?? "<no-id>"}: " +
            $"{command} level={request.Level} {quantityLabel}");

        switch (command)
        {
            case "wakeup":
                return Wakeup(request);

            case "sleep":
                return Sleep(request);

            case "spinup":
                return Spinup(request);

            case "spindown":
                return Spindown(request);

            case "ping":
                return Ok(request, "Buddies service is running.");

            default:
                return Fail(request, $"Unknown Buddies command '{request.Command}'.");
        }
    }

    private static WorkerResponse Wakeup(WorkerRequest request)
    {
        if (!request.Level.HasValue || request.Level.Value <= 0)
            return Fail(request, "wakeup requires a positive level.");

        if (!request.Index.HasValue)
            return Fail(request, "wakeup requires an account index.");

        int level = request.Level.Value;
        int index = request.Index.Value;

        if (index < 0 || index >= _config.AccountCount)
        {
            return Fail(
                request,
                $"Account index {index} is outside 0..{_config.AccountCount - 1}.");
        }

        lock (ActiveLock)
        {
            return WakeupLocked(request, level, index);
        }
    }

    private static WorkerResponse WakeupLocked(
        WorkerRequest request,
        int level,
        int index)
    {
        ActiveBuddy existing;

        if (ActiveBuddies.TryGetValue(index, out existing))
        {
            if (existing.Level == level)
            {
                ApplyRequestPurpose(existing, request);

                return Ok(
                    request,
                    $"{existing.Character} is already active.",
                    existing.Character,
                    level,
                    index);
            }

            return Fail(
                request,
                $"Account index {index} is already running {existing.Character}. " +
                "Sleep it before selecting another level.");
        }

        string username = _config.AccountPrefix + index;
        string character = BuildCharacterName(level, index);
        string readyPath = GetReadyPath(character);

        DeleteReadyMarker(readyPath);

        Logger logger =
            new LoggerConfiguration()
                .WriteTo.Console()
                .MinimumLevel.Debug()
                .CreateLogger();

        ClientDomain domain = null;

        try
        {
            Console.WriteLine(
                $"Starting buddy {character} on {username}...");

            domain = Client.CreateInstance(
                username,
                _config.Password,
                character,
                Dimension.RubiKa,
                logger);

            foreach (string pluginPath in GetPluginPaths())
                domain.LoadPlugin(pluginPath);

            domain.Start();

            Console.WriteLine(
                $"Buddy domain started; waiting for {character} to reach InPlay...");

            if (!WaitForReady(character, readyPath, WakeupTimeoutMs))
            {
                Console.WriteLine(
                    $"TIMEOUT waiting for {character} to reach InPlay.");

                try
                {
                    domain.Unload();
                }
                catch
                {
                }

                domain = null;
                DeleteReadyMarker(readyPath);

                return Fail(
                    request,
                    $"{character} did not reach InPlay within {WakeupTimeoutMs / 1000} seconds.");
            }

            var activeBuddy = new ActiveBuddy
            {
                Index = index,
                Level = level,
                Character = character,
                Domain = domain,
                StartedSequence = ++_nextStartSequence
            };

            ApplyRequestPurpose(activeBuddy, request);
            ActiveBuddies[index] = activeBuddy;

            domain = null;

            Console.WriteLine(
                $"Buddy ready: {character} reached InPlay (index {index}).");

            string leaseText = DescribeLease(activeBuddy);

            return Ok(
                request,
                $"Started {character} on account index {index}.{leaseText}",
                character,
                level,
                index);
        }
        catch (Exception ex)
        {
            if (domain != null)
            {
                try
                {
                    domain.Unload();
                }
                catch
                {
                }
            }

            DeleteReadyMarker(readyPath);

            Console.WriteLine(
                $"Failed starting buddy {character}: {ex}");

            return Fail(
                request,
                $"Failed starting {character}: {ex.Message}");
        }
    }

    private static WorkerResponse Sleep(WorkerRequest request)
    {
        if (!request.Index.HasValue)
            return Fail(request, "sleep requires an account index.");

        int index = request.Index.Value;

        lock (ActiveLock)
        {
            return SleepLocked(request, index);
        }
    }

    private static WorkerResponse SleepLocked(WorkerRequest request, int index)
    {
        ActiveBuddy buddy;

        if (!ActiveBuddies.TryGetValue(index, out buddy))
        {
            return Ok(
                request,
                $"Account index {index} is already sleeping.",
                null,
                null,
                index);
        }

        Console.WriteLine(
            $"Sleeping {buddy.Character} (index {index})...");

        try
        {
            buddy.Domain.Unload();
            ActiveBuddies.Remove(index);
            DeleteReadyMarker(GetReadyPath(buddy.Character));

            Console.WriteLine(
                $"Buddy unloaded: {buddy.Character}.");

            return Ok(
                request,
                $"Slept {buddy.Character}.",
                buddy.Character,
                buddy.Level,
                index);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Failed unloading {buddy.Character}: {ex}");

            return Fail(
                request,
                $"Failed sleeping {buddy.Character}: {ex.Message}");
        }
    }

    private static WorkerResponse Spinup(WorkerRequest request)
    {
        if (!request.Level.HasValue || request.Level.Value <= 0)
            return Fail(request, "spinup requires a positive level.");

        if (!request.Index.HasValue || request.Index.Value <= 0)
            return Fail(request, "spinup requires a positive count.");

        int level = request.Level.Value;
        int requested = request.Index.Value;

        lock (ActiveLock)
        {
            var started = new List<string>();
            var startedIndexes = new List<int>();
            var failures = new List<string>();
            int activeSkipped = 0;
            int claimedExisting = 0;

            if (IsRaidRequest(request))
            {
                for (int index = 0;
                     index < _config.AccountCount && started.Count < requested;
                     index++)
                {
                    ActiveBuddy active;
                    if (!ActiveBuddies.TryGetValue(index, out active) ||
                        active.Level != level)
                    {
                        continue;
                    }

                    ApplyRequestPurpose(active, request);
                    started.Add(active.Character);
                    startedIndexes.Add(index);
                    claimedExisting++;
                }
            }

            for (int index = 0;
                 index < _config.AccountCount && started.Count < requested;
                 index++)
            {
                if (ActiveBuddies.ContainsKey(index))
                {
                    if (!startedIndexes.Contains(index))
                        activeSkipped++;
                    continue;
                }

                WorkerResponse attempt = WakeupLocked(request, level, index);

                if (attempt.Ok &&
                    !string.IsNullOrWhiteSpace(attempt.Character) &&
                    attempt.Message != null &&
                    attempt.Message.StartsWith("Started ", StringComparison.OrdinalIgnoreCase))
                {
                    started.Add(attempt.Character);
                    if (attempt.Index.HasValue)
                        startedIndexes.Add(attempt.Index.Value);
                    continue;
                }

                failures.Add($"{index}:{CompactFailure(attempt.Message)}");
            }

            // Give every member of a group the same full lease starting when
            // the complete spinup attempt finishes. This prevents the first
            // character from expiring while later accounts are still loading.
            foreach (int index in startedIndexes)
            {
                ActiveBuddy active;
                if (ActiveBuddies.TryGetValue(index, out active))
                    ApplyRequestPurpose(active, request);
            }

            string startedText =
                started.Count > 0
                    ? string.Join(",", started)
                    : "none";

            string detail =
                $"Spinup {(started.Count == requested ? "complete" : "partial")}: " +
                $"started {started.Count}/{requested} level {level} [{startedText}]";

            if (claimedExisting > 0)
                detail += $"; raid claimed existing={claimedExisting}";

            detail += IsRaidRequest(request)
                ? $"; raid-owned; safety lease=" +
                  $"{GetLeaseSeconds(request, DefaultRaidSafetyLeaseSeconds)}s"
                : $"; demo lease={DefaultDemoLeaseSeconds}s";

            if (activeSkipped > 0)
                detail += $"; active skipped={activeSkipped}";

            if (failures.Count > 0)
                detail += $"; failed [{string.Join(",", failures)}]";

            if (started.Count < requested &&
                failures.Count == 0 &&
                ActiveBuddies.Count >= _config.AccountCount)
            {
                detail += "; no free accounts remain";
            }

            WorkerResponse result = started.Count == requested
                ? Ok(request, detail, null, level, null)
                : Fail(request, detail);

            result.Characters = started;
            result.Indexes = startedIndexes;
            result.Count = started.Count;
            result.Level = level;
            return result;
        }
    }

    private static void ApplyRequestPurpose(
        ActiveBuddy buddy,
        WorkerRequest request)
    {
        if (buddy == null)
            return;

        if (IsRaidRequest(request))
        {
            buddy.Purpose = "raid";
            buddy.LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(
                GetLeaseSeconds(request, DefaultRaidSafetyLeaseSeconds));
            return;
        }

        // A manual debug command must never demote a buddy that an active raid
        // already owns.
        if (string.Equals(
                buddy.Purpose,
                "raid",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int leaseSeconds = Math.Min(
            GetLeaseSeconds(request, DefaultDemoLeaseSeconds),
            DefaultDemoLeaseSeconds);

        buddy.Purpose = "demo";
        buddy.LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(leaseSeconds);
    }

    private static bool IsRaidRequest(WorkerRequest request)
    {
        return string.Equals(
            request?.Purpose,
            "raid",
            StringComparison.OrdinalIgnoreCase);
    }

    private static int GetLeaseSeconds(WorkerRequest request, int fallback)
    {
        int leaseSeconds = request?.LeaseSeconds ?? fallback;
        return leaseSeconds > 0 ? leaseSeconds : fallback;
    }

    private static string DescribeLease(ActiveBuddy buddy)
    {
        int remainingSeconds = buddy.LeaseExpiresUtc.HasValue
            ? Math.Max(
                0,
                (int)Math.Ceiling(
                    (buddy.LeaseExpiresUtc.Value - DateTime.UtcNow).TotalSeconds))
            : 0;

        return string.Equals(
                buddy.Purpose,
                "raid",
                StringComparison.OrdinalIgnoreCase)
            ? $" Raid-owned; safety lease={remainingSeconds}s."
            : $" Demo lease={remainingSeconds}s.";
    }

    private static void ExpireBuddyLeases(object state)
    {
        try
        {
            lock (ActiveLock)
            {
                DateTime now = DateTime.UtcNow;
                var expiredIndexes = new List<int>();

                foreach (ActiveBuddy buddy in ActiveBuddies.Values)
                {
                    if (buddy.LeaseExpiresUtc.HasValue &&
                        now >= buddy.LeaseExpiresUtc.Value)
                    {
                        expiredIndexes.Add(buddy.Index);
                    }
                }

                foreach (int index in expiredIndexes)
                {
                    ActiveBuddy buddy;
                    if (!ActiveBuddies.TryGetValue(index, out buddy) ||
                        !buddy.LeaseExpiresUtc.HasValue ||
                        now < buddy.LeaseExpiresUtc.Value)
                    {
                        continue;
                    }

                    Console.WriteLine(
                        $"{buddy.Purpose ?? "Buddy"} lease expired for " +
                        $"{buddy.Character}; logging it out.");

                    SleepLocked(
                        new WorkerRequest
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Command = "sleep",
                            Index = index,
                            Purpose = "demo-expiry"
                        },
                        index);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Buddy lease cleanup failed: {ex.Message}");
        }
    }

    private static WorkerResponse Spindown(WorkerRequest request)
    {
        if (!request.Index.HasValue || request.Index.Value <= 0)
            return Fail(request, "spindown requires a positive count.");

        int requested = request.Index.Value;

        lock (ActiveLock)
        {
            var slept = new List<string>();
            var sleptIndexes = new List<int>();
            var failures = new List<string>();
            var attemptedIndexes = new HashSet<int>();

            while (slept.Count < requested)
            {
                ActiveBuddy newest = null;

                foreach (ActiveBuddy buddy in ActiveBuddies.Values)
                {
                    if (attemptedIndexes.Contains(buddy.Index))
                        continue;

                    if (newest == null || buddy.StartedSequence > newest.StartedSequence)
                        newest = buddy;
                }

                if (newest == null)
                    break;

                attemptedIndexes.Add(newest.Index);

                WorkerResponse attempt = SleepLocked(request, newest.Index);
                if (attempt.Ok)
                {
                    if (!string.IsNullOrWhiteSpace(attempt.Character))
                        slept.Add(attempt.Character);
                    sleptIndexes.Add(newest.Index);
                }
                else
                {
                    failures.Add($"{newest.Index}:{CompactFailure(attempt.Message)}");
                }
            }

            string sleptText =
                slept.Count > 0
                    ? string.Join(",", slept)
                    : "none";

            string detail =
                $"Spindown {(slept.Count == requested ? "complete" : "partial")}: " +
                $"slept {slept.Count}/{requested} [{sleptText}]";

            if (failures.Count > 0)
                detail += $"; failed [{string.Join(",", failures)}]";

            if (slept.Count < requested && ActiveBuddies.Count == 0)
                detail += "; no City Dwellers-owned buddies remain";

            WorkerResponse result = slept.Count == requested
                ? Ok(request, detail)
                : Fail(request, detail);

            result.Characters = slept;
            result.Indexes = sleptIndexes;
            result.Count = slept.Count;
            return result;
        }
    }

    private static string CompactFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "failed";

        if (message.IndexOf("did not reach InPlay", StringComparison.OrdinalIgnoreCase) >= 0)
            return "timeout";

        string compact = message.Replace("\r", " ").Replace("\n", " ").Trim();
        const int maxLength = 80;

        return compact.Length <= maxLength
            ? compact
            : compact.Substring(0, maxLength) + "...";
    }

    private static IEnumerable<string> GetPluginPaths()
    {
        if (_config.Plugins != null && _config.Plugins.Count > 0)
        {
            foreach (string configuredPath in _config.Plugins)
            {
                yield return Path.GetFullPath(
                    Path.IsPathRooted(configuredPath)
                        ? configuredPath
                        : Path.Combine(_baseDir, configuredPath));
            }

            yield break;
        }

        yield return Path.Combine(_baseDir, "CityBuddies.dll");
    }

    private static string BuildCharacterName(int level, int index)
    {
        return $"Apcr{level:D3}{index:D2}";
    }

    private static string GetReadyPath(string character)
    {
        return Path.Combine(
            _baseDir,
            $"citybuddies-ready-{character}.ready");
    }

    private static bool WaitForReady(string character, string readyPath, int timeoutMs)
    {
        var timer = Stopwatch.StartNew();

        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (File.Exists(readyPath))
                {
                    string marker = File.ReadAllText(readyPath);
                    if (marker.StartsWith(character + "|", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static void DeleteReadyMarker(string readyPath)
    {
        try
        {
            if (File.Exists(readyPath))
                File.Delete(readyPath);
        }
        catch
        {
        }
    }

    private static void ShutdownAll()
    {
        lock (ActiveLock)
        {
            foreach (ActiveBuddy buddy in ActiveBuddies.Values)
            {
                try
                {
                    Console.WriteLine($"Unloading {buddy.Character}...");
                    buddy.Domain.Unload();
                    DeleteReadyMarker(GetReadyPath(buddy.Character));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Failed unloading {buddy.Character}: {ex.Message}");
                }
            }

            ActiveBuddies.Clear();
        }
    }

    private static WorkerResponse Ok(
        WorkerRequest request,
        string message,
        string character = null,
        int? level = null,
        int? index = null)
    {
        return new WorkerResponse
        {
            Id = request?.Id,
            Ok = true,
            Message = message,
            Character = character,
            Level = level,
            Index = index
        };
    }

    private static WorkerResponse Fail(WorkerRequest request, string message)
    {
        return new WorkerResponse
        {
            Id = request?.Id,
            Ok = false,
            Message = message
        };
    }

    public class Config
    {
        public string AccountPrefix;
        public int AccountCount;
        public string Password;
        public List<string> Plugins;
    }

    private class ActiveBuddy
    {
        public int Index;
        public int Level;
        public string Character;
        public ClientDomain Domain;
        public long StartedSequence;
        public string Purpose;
        public DateTime? LeaseExpiresUtc;
    }

    private class WorkerRequest
    {
        public string Id;
        public string Command;
        public int? Level;
        public int? Index;
        public string Purpose;
        public int? LeaseSeconds;
    }

    private class WorkerResponse
    {
        public string Id;
        public bool Ok;
        public string Message;
        public string Character;
        public int? Level;
        public int? Index;
        public List<string> Characters;
        public List<int> Indexes;
        public int? Count;
    }
}
