using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Remoting.Lifetime;
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
    // Match Manager's measured general-only presence window: buddies enter at
    // +975s and leave at +1125s after the city-targeted event.
    private const int DefaultDemoLeaseSeconds = 150;
    private const int DefaultRaidSafetyLeaseSeconds = 1365;
    private const int ClientDomainLeaseMinutes = 60;
    private const int FailedCleanupRetrySeconds = 30;
    // ClientDomain unload is immediate locally, but AO can leave the avatar
    // visible and attackable for roughly 30 seconds. Keep the account slot
    // reserved beyond that blind server-side interval.
    private const int ServerLogoutLingerSeconds = 35;
    private const int HomeNavigationTimeoutSeconds = 600;
    private const int AbsoluteMaxRaidBuddies = 12;
    private static readonly int[] SupportedHomeLevels =
        { 25, 50, 75, 100, 125, 150, 175, 200 };

    private static readonly object ActiveLock = new object();
    private static readonly Dictionary<int, ActiveBuddy> ActiveBuddies =
        new Dictionary<int, ActiveBuddy>();
    private static BuddySlotWorker[] _slotWorkers;
    private static SemaphoreSlim _loginGate;
    private static DateTime[] _slotLingeringUntilUtc;
    private static readonly object HomeMaintenanceLock = new object();
    private static HomeMaintenanceState _homeMaintenance;

    // AOSharp.Clientless 1.0.16 keeps these private. Its PluginProxy uses the
    // default .NET Remoting lease, so it expires during a normal city raid and
    // prevents ClientDomain.Unload() from reaching AppDomain.Unload().
    private static readonly FieldInfo ClientDomainPluginProxyField =
        typeof(ClientDomain).GetField(
            "_pluginProxy",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo ClientDomainAppDomainField =
        typeof(ClientDomain).GetField(
            "_appDomain",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static Config _config;
    private static string _baseDir;
    private static long _nextStartSequence;
    private static Timer _leaseTimer;
    private static volatile bool _stopping;

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

        bool configChanged = false;
        if (_config != null &&
            !_config.ActiveLimit.HasValue &&
            _config.AccountCount > 0)
        {
            _config.ActiveLimit = Math.Min(
                AbsoluteMaxRaidBuddies,
                _config.AccountCount);
            configChanged = true;
        }

        if (_config != null &&
            !_config.MaxParallelLogins.HasValue &&
            _config.AccountCount > 0)
        {
            _config.MaxParallelLogins = Math.Min(4, _config.AccountCount);
            configChanged = true;
        }

        if (!ValidateConfig(_config))
        {
            StopForConfiguration($"Correct '{configPath}', then start Buddies again.");
            Environment.Exit(1);
            return;
        }

        if (configChanged)
        {
            try
            {
                File.WriteAllText(
                    configPath,
                    JsonConvert.SerializeObject(_config, Formatting.Indented));
                Console.WriteLine(
                    $"Updated optional concurrency settings in '{configPath}'.");
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Unable to save optional concurrency settings to " +
                    $"'{configPath}': {ex.Message}");
            }
        }

        Console.WriteLine("======================================");
        Console.WriteLine(" City Dwellers - Buddies Service");
        Console.WriteLine("======================================");
        Console.WriteLine();
        Console.WriteLine(
            $"Account pool: {_config.AccountPrefix}0 .. " +
            $"{_config.AccountPrefix}{_config.AccountCount - 1} " +
            $"({_config.AccountCount} configured)");
        Console.WriteLine(
            $"Raid limit: {_config.ActiveLimit.Value}; " +
            $"admin manual capacity: {_config.AccountCount}; " +
            $"raid spare capacity: {_config.AccountCount - _config.ActiveLimit.Value}");
        Console.WriteLine(
            $"Buddy workers: {_config.AccountCount}; " +
            $"parallel AO logins: {_config.MaxParallelLogins.Value}");
        Console.WriteLine("Character scheme: Apcr{level:000}{index:00}");
        Console.WriteLine($"Pipe: {PipeName}");
        Console.WriteLine();
        Console.WriteLine("Buddies service idle. Zero buddy AO sessions are started automatically.");
        Console.WriteLine("Waiting for Manager requests.");
        Console.WriteLine("Press ENTER to stop Buddies and unload any active buddies.");
        Console.WriteLine();

        InitializeSlotWorkers();

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
        _stopping = true;
        _leaseTimer?.Dispose();
        _leaseTimer = null;
        ShutdownAll();
        StopSlotWorkers();
        Console.WriteLine("Buddies service stopped.");
    }

    private static string BuildDefaultConfig()
    {
        var config = new Config
        {
            AccountPrefix = "user",
            AccountCount = 13,
            ActiveLimit = 12,
            MaxParallelLogins = 4,
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

        if (!config.ActiveLimit.HasValue ||
            config.ActiveLimit.Value <= 0 ||
            config.ActiveLimit.Value > AbsoluteMaxRaidBuddies)
        {
            Console.WriteLine(
                $"buddies.json requires ActiveLimit between 1 and " +
                $"{AbsoluteMaxRaidBuddies}.");
            return false;
        }

        if (config.ActiveLimit.Value > config.AccountCount)
        {
            Console.WriteLine(
                "buddies.json ActiveLimit cannot exceed AccountCount.");
            return false;
        }

        if (!config.MaxParallelLogins.HasValue ||
            config.MaxParallelLogins.Value <= 0 ||
            config.MaxParallelLogins.Value > config.AccountCount)
        {
            Console.WriteLine(
                "buddies.json MaxParallelLogins must be between 1 and AccountCount.");
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

    private static void InitializeSlotWorkers()
    {
        _loginGate = new SemaphoreSlim(
            _config.MaxParallelLogins.Value,
            _config.MaxParallelLogins.Value);
        _slotWorkers = new BuddySlotWorker[_config.AccountCount];
        _slotLingeringUntilUtc = new DateTime[_config.AccountCount];

        for (int index = 0; index < _slotWorkers.Length; index++)
            _slotWorkers[index] = new BuddySlotWorker(index);
    }

    private static void StopSlotWorkers()
    {
        if (_slotWorkers == null)
            return;

        foreach (BuddySlotWorker worker in _slotWorkers)
            worker.StopAccepting();

        foreach (BuddySlotWorker worker in _slotWorkers)
            worker.Join();

        _slotWorkers = null;
        _loginGate?.Dispose();
        _loginGate = null;
    }

    private static SlotOperation QueueSlotOperation(
        int index,
        Func<WorkerResponse> action)
    {
        if (_slotWorkers == null || index < 0 || index >= _slotWorkers.Length)
            throw new InvalidOperationException($"Buddy slot {index} is not available.");

        return _slotWorkers[index].Enqueue(action);
    }

    private static WorkerResponse AwaitSlotOperation(
        SlotOperation operation,
        WorkerRequest request,
        int index,
        string command)
    {
        operation.Completed.Wait();

        if (operation.Error != null)
        {
            return Fail(
                request,
                $"Buddy slot {index} failed during {command}: " +
                operation.Error.Message);
        }

        return operation.Response ??
            Fail(request, $"Buddy slot {index} returned no {command} result.");
    }

    private static void RunPipeServer()
    {
        while (!_stopping)
        {
            NamedPipeServerStream pipe = null;

            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                pipe.WaitForConnection();
                ThreadPool.QueueUserWorkItem(HandlePipeConnection, pipe);
                pipe = null;
            }
            catch (Exception ex)
            {
                if (!_stopping)
                {
                    Console.WriteLine($"Buddies pipe listener error: {ex}");
                    Thread.Sleep(500);
                }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private static void HandlePipeConnection(object state)
    {
        using (var pipe = (NamedPipeServerStream)state)
        {
            try
            {
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
            catch (Exception ex)
            {
                if (!_stopping)
                    Console.WriteLine($"Buddies pipe request error: {ex}");
            }
        }
    }

    private static WorkerResponse HandleRequest(WorkerRequest request)
    {
        if (_stopping)
            return Fail(request, "Buddies service is stopping.");

        if (request == null || string.IsNullOrWhiteSpace(request.Command))
        {
            return Fail(request, "Missing command.");
        }

        string command = request.Command.Trim().ToLowerInvariant();
        string quantityLabel =
            command == "spinup" || command == "spindown"
                ? $"count={request.Index}"
                : command == "sleepmany"
                    ? $"indexes={FormatIndexes(request.Indexes)}"
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

            case "sleepmany":
                return SleepMany(request);

            case "spinup":
                return Spinup(request);

            case "spindown":
                return Spindown(request);

            case "positions":
                return Positions(request);

            case "home":
                return Home(request);

            case "homestatus":
                return HomeStatus(request);

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

        SlotOperation operation = QueueSlotOperation(
            index,
            () => WakeupOnSlot(request, level, index));

        return AwaitSlotOperation(operation, request, index, "wakeup");
    }

    private static WorkerResponse WakeupOnSlot(
        WorkerRequest request,
        int level,
        int index)
    {
        ActiveBuddy activeBuddy;

        lock (ActiveLock)
        {
            if (_stopping)
                return Fail(request, "Buddies service is stopping.");

            DateTime lingeringUntil = _slotLingeringUntilUtc[index];
            if (DateTime.UtcNow < lingeringUntil)
            {
                int remaining = Math.Max(
                    1,
                    (int)Math.Ceiling(
                        (lingeringUntil - DateTime.UtcNow).TotalSeconds));
                return Fail(
                    request,
                    $"Account index {index} is quarantined for {remaining}s while " +
                    "its previous AO avatar finishes leaving the server.");
            }

            ActiveBuddy existing;

            if (ActiveBuddies.TryGetValue(index, out existing))
            {
                if (existing.Level == level && !existing.IsStopping)
                {
                    ApplyRequestPurpose(existing, request);
                    if (request.Home)
                        WriteHomeDirective(existing);

                    return Ok(
                        request,
                        $"{existing.Character} is already active.",
                        existing.Character,
                        level,
                        index);
                }

                return Fail(
                    request,
                    existing.IsStopping
                        ? $"Account index {index} is still unloading {existing.Character}."
                        : $"Account index {index} is already running {existing.Character}. " +
                          "Sleep it before selecting another level.");
            }

            int activeLimit = GetRequestActiveLimit(request);
            if (ActiveBuddies.Count >= activeLimit)
            {
                return Fail(
                    request,
                    UsesRaidCapacity(request)
                        ? $"The configured raid limit of {activeLimit} is already active."
                        : $"All {activeLimit} configured buddy accounts are already active.");
            }

            activeBuddy = new ActiveBuddy
            {
                Index = index,
                Level = level,
                Character = BuildCharacterName(level, index),
                StartedSequence = ++_nextStartSequence,
                IsStarting = true
            };

            ApplyRequestPurpose(activeBuddy, request);
            ActiveBuddies[index] = activeBuddy;
        }

        string username = _config.AccountPrefix + index;
        string character = activeBuddy.Character;
        string readyPath = GetReadyPath(character);

        DeleteReadyMarker(readyPath);
        DeletePositionSnapshot(character);
        DeleteHomeDirective(character);

        Logger logger =
            new LoggerConfiguration()
                .WriteTo.Console()
                .MinimumLevel.Debug()
                .CreateLogger();

        ClientDomain domain = null;
        bool loginGateEntered = false;

        try
        {
            _loginGate.Wait();
            loginGateEntered = true;

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
            RenewClientDomainLease(domain, character);

            Console.WriteLine(
                $"Buddy domain started; waiting for {character} to reach InPlay...");

            if (!WaitForReady(character, readyPath, WakeupTimeoutMs))
            {
                Console.WriteLine(
                    $"TIMEOUT waiting for {character} to reach InPlay.");

                string unloadError;
                bool unloaded = TryUnloadClientDomain(
                    domain,
                    character,
                    out unloadError);

                if (!unloaded)
                {
                    Console.WriteLine(unloadError);
                    RetainFailedStartup(activeBuddy, domain);
                }
                else
                {
                    RemoveStartupReservation(index, activeBuddy);
                    MarkSlotLingering(index, character);
                }

                domain = null;
                DeleteReadyMarker(readyPath);
                DeletePositionSnapshot(character);
                DeleteHomeDirective(character);

                return Fail(
                    request,
                    $"{character} did not reach InPlay within " +
                    $"{WakeupTimeoutMs / 1000} seconds." +
                    (unloaded ? string.Empty : " Cleanup retry was scheduled."));
            }

            lock (ActiveLock)
            {
                ActiveBuddy current;
                if (!ActiveBuddies.TryGetValue(index, out current) ||
                    !ReferenceEquals(current, activeBuddy))
                {
                    throw new InvalidOperationException(
                        $"Account index {index} lost its startup reservation.");
                }

                activeBuddy.Domain = domain;
                activeBuddy.IsStarting = false;
            }

            if (request.Home)
                WriteHomeDirective(activeBuddy);

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
            bool startupRetained = false;

            if (domain != null)
            {
                string unloadError;
                if (!TryUnloadClientDomain(domain, character, out unloadError))
                {
                    Console.WriteLine(unloadError);
                    RetainFailedStartup(activeBuddy, domain);
                    startupRetained = true;
                }
            }

            DeleteReadyMarker(readyPath);
            DeletePositionSnapshot(character);
            DeleteHomeDirective(character);

            if (!startupRetained)
            {
                RemoveStartupReservation(index, activeBuddy);
                if (domain != null)
                    MarkSlotLingering(index, character);
            }

            Console.WriteLine(
                $"Failed starting buddy {character}: {ex}");

            return Fail(
                request,
                $"Failed starting {character}: {ex.Message}");
        }
        finally
        {
            if (loginGateEntered)
                _loginGate.Release();
        }
    }

    private static void RemoveStartupReservation(int index, ActiveBuddy buddy)
    {
        lock (ActiveLock)
        {
            ActiveBuddy current;
            if (ActiveBuddies.TryGetValue(index, out current) &&
                ReferenceEquals(current, buddy))
            {
                ActiveBuddies.Remove(index);
            }
        }
    }

    private static void RetainFailedStartup(
        ActiveBuddy buddy,
        ClientDomain domain)
    {
        lock (ActiveLock)
        {
            buddy.Domain = domain;
            buddy.IsStarting = false;
            buddy.Purpose = "failed-start";
            buddy.LeaseExpiresUtc = DateTime.UtcNow;
            buddy.NavigationHold = false;
            buddy.NavigationState = "failed";
            buddy.NavigationDetail = "Login failed before navigation could start.";
            buddy.CleanupFailures++;
            buddy.NextCleanupAttemptUtc =
                DateTime.UtcNow.AddSeconds(FailedCleanupRetrySeconds);
            buddy.CleanupQueued = false;
        }
    }

    private static WorkerResponse Sleep(WorkerRequest request)
    {
        if (!request.Index.HasValue)
            return Fail(request, "sleep requires an account index.");

        int index = request.Index.Value;

        if (index < 0 || index >= _config.AccountCount)
        {
            return Fail(
                request,
                $"Account index {index} is outside 0..{_config.AccountCount - 1}.");
        }

        SlotOperation operation = QueueSlotOperation(
            index,
            () => SleepOnSlot(request, index));

        return AwaitSlotOperation(operation, request, index, "sleep");
    }

    private static WorkerResponse SleepOnSlot(WorkerRequest request, int index)
    {
        ActiveBuddy buddy;

        lock (ActiveLock)
        {
            if (!ActiveBuddies.TryGetValue(index, out buddy))
            {
                return Ok(
                    request,
                    $"Account index {index} is already sleeping.",
                    null,
                    null,
                    index);
            }

            if (string.Equals(
                    request?.Purpose,
                    "demo-expiry",
                    StringComparison.OrdinalIgnoreCase) &&
                ((buddy.LeaseExpiresUtc.HasValue &&
                  DateTime.UtcNow < buddy.LeaseExpiresUtc.Value) ||
                 buddy.NavigationHold))
            {
                buddy.CleanupQueued = false;
                return Ok(
                    request,
                    buddy.NavigationHold
                        ? $"{buddy.Character} is still completing home navigation."
                        : $"{buddy.Character} lease was renewed before cleanup.",
                    null,
                    buddy.Level,
                    index);
            }

            if (string.Equals(
                    request?.Purpose,
                    "home-complete",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    request.HomeJobId,
                    buddy.NavigationJobId,
                    StringComparison.Ordinal))
            {
                buddy.CleanupQueued = false;
                return Ok(
                    request,
                    $"{buddy.Character} received a newer ownership/navigation request " +
                    "before home cleanup.",
                    null,
                    buddy.Level,
                    index);
            }

            buddy.IsStopping = true;
            buddy.CleanupQueued = false;
        }

        Console.WriteLine(
            $"Sleeping {buddy.Character} (index {index})...");

        try
        {
            CancelHomeDirective(buddy);

            string unloadError;
            if (!TryUnloadClientDomain(
                    buddy.Domain,
                    buddy.Character,
                    out unloadError))
            {
                throw new InvalidOperationException(unloadError);
            }

            lock (ActiveLock)
            {
                ActiveBuddy current;
                if (ActiveBuddies.TryGetValue(index, out current) &&
                    ReferenceEquals(current, buddy))
                {
                    ActiveBuddies.Remove(index);
                    _slotLingeringUntilUtc[index] =
                        DateTime.UtcNow.AddSeconds(ServerLogoutLingerSeconds);
                }
            }
            DeleteReadyMarker(GetReadyPath(buddy.Character));
            DeletePositionSnapshot(buddy.Character);
            DeleteHomeDirective(buddy.Character);

            Console.WriteLine(
                $"Buddy unloaded: {buddy.Character}. Account index {index} is " +
                $"quarantined for {ServerLogoutLingerSeconds}s while AO removes " +
                "the server-side avatar.");

            return Ok(
                request,
                $"Slept {buddy.Character}.",
                buddy.Character,
                buddy.Level,
                index);
        }
        catch (Exception ex)
        {
            lock (ActiveLock)
            {
                buddy.IsStopping = false;
                buddy.CleanupQueued = false;
                buddy.CleanupFailures++;
                buddy.NextCleanupAttemptUtc =
                    DateTime.UtcNow.AddSeconds(FailedCleanupRetrySeconds);
            }

            Console.WriteLine(
                $"Failed unloading {buddy.Character}; retry {buddy.CleanupFailures} " +
                $"will be eligible in {FailedCleanupRetrySeconds}s: {ex}");

            return Fail(
                request,
                $"Failed sleeping {buddy.Character}: {ex.Message}");
        }
    }

    private static WorkerResponse SleepMany(WorkerRequest request)
    {
        if (request.Indexes == null || request.Indexes.Count == 0)
            return Fail(request, "sleepmany requires at least one account index.");

        var indexes = new List<int>();
        var seen = new HashSet<int>();

        foreach (int index in request.Indexes)
        {
            if (index < 0 || index >= _config.AccountCount)
            {
                return Fail(
                    request,
                    $"Account index {index} is outside 0..{_config.AccountCount - 1}.");
            }

            if (seen.Add(index))
                indexes.Add(index);
        }

        return SleepIndexes(request, indexes, "Group sleep");
    }

    private static WorkerResponse Spinup(WorkerRequest request)
    {
        if (!request.Level.HasValue || request.Level.Value <= 0)
            return Fail(request, "spinup requires a positive level.");

        if (!request.Index.HasValue || request.Index.Value <= 0)
            return Fail(request, "spinup requires a positive count.");

        int level = request.Level.Value;
        int requested = request.Index.Value;
        int requestLimit = GetRequestActiveLimit(request);

        if (requested > requestLimit)
        {
            return Fail(
                request,
                UsesRaidCapacity(request)
                    ? $"raid spinup count cannot exceed the configured " +
                      $"raid limit of {requestLimit}."
                    : $"manual spinup count cannot exceed the configured " +
                      $"account pool of {requestLimit}.");
        }

        var started = new List<string>();
        var startedIndexes = new List<int>();
        var failures = new List<string>();
        var attemptedIndexes = new HashSet<int>();
        int claimedExisting = 0;

        while (started.Count < requested)
        {
            var batchIndexes = new List<int>();

            lock (ActiveLock)
            {
                if (IsRaidRequest(request) || request.Home)
                {
                    for (int index = 0;
                         index < _config.AccountCount &&
                         started.Count + batchIndexes.Count < requested;
                         index++)
                    {
                        if (attemptedIndexes.Contains(index) ||
                            startedIndexes.Contains(index))
                        {
                            continue;
                        }

                        ActiveBuddy active;
                        if (!ActiveBuddies.TryGetValue(index, out active) ||
                            active.Level != level ||
                            active.IsStopping)
                        {
                            continue;
                        }

                        if (active.IsStarting)
                        {
                            batchIndexes.Add(index);
                            attemptedIndexes.Add(index);
                            continue;
                        }

                        ApplyRequestPurpose(active, request);
                        if (request.Home)
                            WriteHomeDirective(active);
                        started.Add(active.Character);
                        startedIndexes.Add(index);
                        claimedExisting++;
                    }
                }

                int needed = requested - started.Count - batchIndexes.Count;

                for (int index = 0;
                     index < _config.AccountCount && needed > 0;
                     index++)
                {
                    if (attemptedIndexes.Contains(index) ||
                        ActiveBuddies.ContainsKey(index))
                    {
                        continue;
                    }

                    batchIndexes.Add(index);
                    attemptedIndexes.Add(index);
                    needed--;
                }
            }

            if (started.Count >= requested || batchIndexes.Count == 0)
                break;

            var operations = new List<SlotOperation>();
            foreach (int index in batchIndexes)
            {
                int slotIndex = index;
                operations.Add(QueueSlotOperation(
                    slotIndex,
                    () => WakeupOnSlot(request, level, slotIndex)));
            }

            for (int operationIndex = 0;
                 operationIndex < operations.Count;
                 operationIndex++)
            {
                int index = batchIndexes[operationIndex];
                WorkerResponse attempt = AwaitSlotOperation(
                    operations[operationIndex],
                    request,
                    index,
                    "spinup");

                if (attempt.Ok && !string.IsNullOrWhiteSpace(attempt.Character))
                {
                    started.Add(attempt.Character);
                    startedIndexes.Add(index);

                    if (attempt.Message != null &&
                        attempt.Message.IndexOf(
                            "already active",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        claimedExisting++;
                    }
                }
                else
                {
                    failures.Add($"{index}:{CompactFailure(attempt.Message)}");
                }
            }
        }

        // Give every member of a group the same full lease starting when the
        // complete parallel spinup finishes. This prevents early slots from
        // expiring while slower AO sessions are still loading.
        lock (ActiveLock)
        {
            foreach (int index in startedIndexes)
            {
                ActiveBuddy active;
                if (ActiveBuddies.TryGetValue(index, out active))
                {
                    ApplyRequestPurpose(active, request);
                    if (request.Home)
                        WriteHomeDirective(active);
                }
            }
        }

        int activeSkipped;
        lock (ActiveLock)
        {
            activeSkipped = 0;
            foreach (int index in ActiveBuddies.Keys)
            {
                if (!startedIndexes.Contains(index))
                    activeSkipped++;
            }
        }

        string startedText =
            started.Count > 0
                ? string.Join(",", started)
                : "none";

        string detail =
            $"Spinup {(started.Count == requested ? "complete" : "partial")}: " +
            $"started {started.Count}/{requested} level {level} [{startedText}]";

        if (claimedExisting > 0)
            detail += $"; claimed existing={claimedExisting}";

        if (IsRaidRequest(request))
        {
            detail += $"; raid-owned; safety lease=" +
                      $"{GetLeaseSeconds(request, DefaultRaidSafetyLeaseSeconds)}s";
        }
        else if (string.Equals(
                     request.Purpose,
                     "raid-preflight",
                     StringComparison.OrdinalIgnoreCase))
        {
            detail += "; raid preflight; logout follows terminal home result";
        }
        else if (request.Home)
        {
            detail += "; home maintenance owns navigation time";
        }
        else
        {
            detail += $"; demo lease={DefaultDemoLeaseSeconds}s";
        }

        if (activeSkipped > 0)
            detail += $"; active skipped={activeSkipped}";

        if (failures.Count > 0)
            detail += $"; failed [{string.Join(",", failures)}]";

        int activeCount;
        lock (ActiveLock)
            activeCount = ActiveBuddies.Count;

        if (started.Count < requested &&
            failures.Count == 0 &&
            activeCount >= requestLimit)
        {
            detail += UsesRaidCapacity(request)
                ? "; raid buddy limit reached"
                : "; all configured buddy accounts are active";
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

    private static void ApplyRequestPurpose(
        ActiveBuddy buddy,
        WorkerRequest request)
    {
        if (buddy == null)
            return;

        buddy.CleanupFailures = 0;
        buddy.NextCleanupAttemptUtc = null;
        buddy.CleanupQueued = false;

        if (request != null && request.Home)
        {
            bool hasDifferentPreflightOwner =
                string.Equals(
                    buddy.Purpose,
                    "raid-preflight",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    buddy.NavigationJobId,
                    request.Id,
                    StringComparison.Ordinal);
            bool hasIndependentOwner =
                (string.Equals(
                     buddy.Purpose,
                     "raid",
                     StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(
                     buddy.Purpose,
                     "demo",
                     StringComparison.OrdinalIgnoreCase) ||
                 hasDifferentPreflightOwner) &&
                (!buddy.LeaseExpiresUtc.HasValue ||
                 DateTime.UtcNow < buddy.LeaseExpiresUtc.Value);

            buddy.NavigationHold = true;
            buddy.NavigationJobId = request.Id;
            buddy.NavigationStartedUtc = DateTime.UtcNow;
            buddy.NavigationLogoutWhenComplete =
                request.LogoutAfterHome &&
                (!hasIndependentOwner || hasDifferentPreflightOwner);
            buddy.NavigationState = "requested";
            buddy.NavigationDetail = "Waiting for CityBuddies navigation telemetry.";

            if (!hasIndependentOwner &&
                !string.Equals(
                    request.Purpose,
                    "raid",
                    StringComparison.OrdinalIgnoreCase))
            {
                buddy.Purpose = request.Purpose ?? "home";
                buddy.LeaseExpiresUtc = null;
            }

            if (!IsRaidRequest(request))
                return;
        }

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

    private static bool UsesRaidCapacity(WorkerRequest request)
    {
        return IsRaidRequest(request) ||
               string.Equals(
                   request?.Purpose,
                   "raid-preflight",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static int GetRequestActiveLimit(WorkerRequest request)
    {
        return UsesRaidCapacity(request)
            ? _config.ActiveLimit.Value
            : _config.AccountCount;
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
            : buddy.NavigationHold
                ? " Home navigation owns this session until it finishes."
                : $" Demo lease={remainingSeconds}s.";
    }

    private static void ExpireBuddyLeases(object state)
    {
        try
        {
            ReconcileHomeNavigation();

            var expiredIndexes = new List<int>();

            lock (ActiveLock)
            {
                DateTime now = DateTime.UtcNow;

                foreach (ActiveBuddy buddy in ActiveBuddies.Values)
                {
                    if (buddy.LeaseExpiresUtc.HasValue &&
                        now >= buddy.LeaseExpiresUtc.Value &&
                        !buddy.IsStarting &&
                        !buddy.IsStopping &&
                        !buddy.NavigationHold &&
                        !buddy.CleanupQueued &&
                        (!buddy.NextCleanupAttemptUtc.HasValue ||
                         now >= buddy.NextCleanupAttemptUtc.Value))
                    {
                        buddy.CleanupQueued = true;
                        expiredIndexes.Add(buddy.Index);
                    }
                }
            }

            foreach (int index in expiredIndexes)
            {
                ActiveBuddy buddy;
                lock (ActiveLock)
                {
                    if (!ActiveBuddies.TryGetValue(index, out buddy) ||
                        !buddy.LeaseExpiresUtc.HasValue ||
                        buddy.IsStarting ||
                        buddy.IsStopping)
                    {
                        continue;
                    }
                }

                Console.WriteLine(
                    $"{buddy.Purpose ?? "Buddy"} lease expired for " +
                    $"{buddy.Character}; queueing logout on slot {index}.");

                WorkerRequest request = new WorkerRequest
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Command = "sleep",
                    Index = index,
                    Purpose = "demo-expiry"
                };

                QueueSlotOperation(
                    index,
                    () => SleepOnSlot(request, index));
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
        var indexes = new List<int>();

        lock (ActiveLock)
        {
            var candidates = new List<ActiveBuddy>(ActiveBuddies.Values);
            candidates.Sort((left, right) =>
                right.StartedSequence.CompareTo(left.StartedSequence));

            foreach (ActiveBuddy buddy in candidates)
            {
                if (indexes.Count >= requested)
                    break;

                indexes.Add(buddy.Index);
            }
        }

        return SleepIndexes(request, indexes, "Spindown", requested);
    }

    private static WorkerResponse SleepIndexes(
        WorkerRequest request,
        List<int> indexes,
        string label,
        int? requestedCount = null)
    {
        int requested = requestedCount ?? indexes.Count;
        var operations = new List<SlotOperation>();

        foreach (int index in indexes)
        {
            int slotIndex = index;
            operations.Add(QueueSlotOperation(
                slotIndex,
                () => SleepOnSlot(request, slotIndex)));
        }

        var slept = new List<string>();
        var sleptIndexes = new List<int>();
        var failures = new List<string>();

        for (int operationIndex = 0;
             operationIndex < operations.Count;
             operationIndex++)
        {
            int index = indexes[operationIndex];
            WorkerResponse attempt = AwaitSlotOperation(
                operations[operationIndex],
                request,
                index,
                label.ToLowerInvariant());

            if (attempt.Ok)
            {
                if (!string.IsNullOrWhiteSpace(attempt.Character))
                    slept.Add(attempt.Character);
                sleptIndexes.Add(index);
            }
            else
            {
                failures.Add($"{index}:{CompactFailure(attempt.Message)}");
            }
        }

        string sleptText =
            slept.Count > 0
                ? string.Join(",", slept)
                : "none";

        string detail =
            $"{label} {(sleptIndexes.Count == requested ? "complete" : "partial")}: " +
            $"slept {sleptIndexes.Count}/{requested} [{sleptText}]";

        if (failures.Count > 0)
            detail += $"; failed [{string.Join(",", failures)}]";

        int activeCount;
        lock (ActiveLock)
            activeCount = ActiveBuddies.Count;

        if (sleptIndexes.Count < requested && activeCount == 0)
            detail += "; no City Dwellers-owned buddies remain";

        WorkerResponse result = sleptIndexes.Count == requested
            ? Ok(request, detail)
            : Fail(request, detail);

        result.Characters = slept;
        result.Indexes = sleptIndexes;
        result.Count = sleptIndexes.Count;
        return result;
    }

    private static WorkerResponse Home(WorkerRequest request)
    {
        var levels = new List<int>();

        if (request.Level.HasValue)
        {
            if (!IsSupportedHomeLevel(request.Level.Value))
            {
                return Fail(
                    request,
                    "home level must be 25, 50, 75, 100, 125, 150, 175, or 200.");
            }

            levels.Add(request.Level.Value);
        }
        else
        {
            levels.AddRange(SupportedHomeLevels);
        }

        HomeMaintenanceState state;

        lock (HomeMaintenanceLock)
        {
            if (_homeMaintenance != null && _homeMaintenance.Running)
            {
                return Fail(
                    request,
                    $"Home maintenance {_homeMaintenance.JobId} is already running: " +
                    _homeMaintenance.Detail);
            }

            state = new HomeMaintenanceState
            {
                JobId = request.Id ?? Guid.NewGuid().ToString("N"),
                Running = true,
                StartedUtc = DateTime.UtcNow,
                Detail = request.Level.HasValue
                    ? $"level {request.Level.Value} queued"
                    : "all configured levels queued"
            };
            state.Levels.AddRange(levels);
            _homeMaintenance = state;
        }

        WorkerResponse result = Ok(
            request,
            request.Level.HasValue
                ? $"Home verification started for all configured level-" +
                  $"{request.Level.Value} characters. It owns navigation time " +
                  "independently of demo leases."
                : "Home verification started for every configured level. " +
                  "Levels will rotate in the background, respecting AO's " +
                  $"{ServerLogoutLingerSeconds}s logout quarantine.");
        PopulateHomeResponse(result, state);
        ThreadPool.QueueUserWorkItem(_ => RunHomeMaintenance(state));
        return result;
    }

    private static WorkerResponse HomeStatus(WorkerRequest request)
    {
        lock (HomeMaintenanceLock)
        {
            if (_homeMaintenance == null)
                return Ok(request, "No home maintenance job has run in this process.");

            WorkerResponse result = Ok(
                request,
                BuildHomeStatusMessage(_homeMaintenance));
            PopulateHomeResponse(result, _homeMaintenance);
            return result;
        }
    }

    private static string BuildHomeStatusMessage(HomeMaintenanceState state)
    {
        int pending = Math.Max(0, state.Started - state.Terminal);
        int unavailable = Math.Max(0, state.Attempted - state.Started);
        string displayJobId = state.JobId != null && state.JobId.Length > 8
            ? state.JobId.Substring(0, 8)
            : state.JobId;
        string message = state.Running
            ? $"Home maintenance {displayJobId} is running: {state.Detail}; " +
              $"reached={state.Reached}, stopped={state.Stopped}, " +
              $"pending={pending}, unavailable={unavailable}."
            : $"Home maintenance {displayJobId} finished: " +
              $"{state.Reached}/{state.Started} reached the CT; " +
              $"{state.Stopped} stopped/gave up; " +
              $"{pending} unresolved; " +
              $"{unavailable} unavailable.";

        if (state.Failures.Count == 0)
            return message;

        int shown = Math.Min(8, state.Failures.Count);
        List<string> failures = state.Failures.GetRange(0, shown);
        string suffix = state.Failures.Count > shown
            ? $", +{state.Failures.Count - shown} more"
            : string.Empty;
        return message + " Failures: " + string.Join(", ", failures) + suffix + ".";
    }

    private static void PopulateHomeResponse(
        WorkerResponse response,
        HomeMaintenanceState state)
    {
        response.HomeJobId = state.JobId;
        response.HomeRunning = state.Running;
        response.HomeAttempted = state.Attempted;
        response.HomeStarted = state.Started;
        response.HomeTerminal = state.Terminal;
        response.HomeReached = state.Reached;
        response.HomeStopped = state.Stopped;
        response.HomeFailures = new List<string>(state.Failures);
    }

    private static void RunHomeMaintenance(HomeMaintenanceState state)
    {
        var summaries = new List<string>();

        try
        {
            foreach (int level in state.Levels)
            {
                if (_stopping)
                    break;

                lock (HomeMaintenanceLock)
                    state.Detail = $"checking level {level}";

                string levelJobId =
                    state.JobId + "-" + level.ToString("D3");
                var levelRequest = new WorkerRequest
                {
                    Id = levelJobId,
                    Command = "spinup",
                    Level = level,
                    Index = _config.AccountCount,
                    Purpose = "home",
                    Home = true,
                    LogoutAfterHome = true
                };

                WorkerResponse response = Spinup(levelRequest);
                int reached = response.Count ?? 0;

                lock (HomeMaintenanceLock)
                {
                    state.Attempted += _config.AccountCount;
                    state.Started += reached;
                    state.Detail =
                        $"level {level}: {reached}/{_config.AccountCount} sessions " +
                        "accepted; waiting for navigation";
                }

                WaitForHomeLevel(levelJobId);
                summaries.Add($"{level}:{reached}/{_config.AccountCount}");

                if (response.Indexes != null &&
                    state.Levels[state.Levels.Count - 1] != level)
                {
                    WaitForLogoutQuarantine(response.Indexes);
                }
            }

            lock (HomeMaintenanceLock)
            {
                state.Running = false;
                state.FinishedUtc = DateTime.UtcNow;
                state.Detail =
                    (_stopping ? "stopped; " : "complete; ") +
                    (summaries.Count == 0
                        ? "no levels processed"
                        : string.Join(", ", summaries));
            }

            Console.WriteLine(
                $"Home maintenance {state.JobId} {state.Detail}.");
        }
        catch (Exception ex)
        {
            lock (HomeMaintenanceLock)
            {
                state.Running = false;
                state.FinishedUtc = DateTime.UtcNow;
                state.Detail = "failed: " + ex.Message;
            }

            Console.WriteLine(
                $"Home maintenance {state.JobId} failed: {ex}");
        }
    }

    private static void WaitForHomeLevel(string levelJobId)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(
            HomeNavigationTimeoutSeconds + FailedCleanupRetrySeconds + 30);

        while (!_stopping && DateTime.UtcNow < deadline)
        {
            bool pending = false;

            lock (ActiveLock)
            {
                foreach (ActiveBuddy buddy in ActiveBuddies.Values)
                {
                    if (!string.Equals(
                            buddy.NavigationJobId,
                            levelJobId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (buddy.NavigationHold ||
                        (buddy.NavigationLogoutWhenComplete &&
                         string.Equals(
                             buddy.Purpose,
                             "home",
                             StringComparison.OrdinalIgnoreCase)))
                    {
                        pending = true;
                        break;
                    }
                }
            }

            if (!pending)
                return;

            Thread.Sleep(1000);
        }
    }

    private static void WaitForLogoutQuarantine(List<int> indexes)
    {
        if (indexes == null || indexes.Count == 0)
            return;

        while (!_stopping)
        {
            DateTime latest = DateTime.MinValue;

            lock (ActiveLock)
            {
                foreach (int index in indexes)
                {
                    if (index >= 0 &&
                        index < _slotLingeringUntilUtc.Length &&
                        _slotLingeringUntilUtc[index] > latest)
                    {
                        latest = _slotLingeringUntilUtc[index];
                    }
                }
            }

            if (latest <= DateTime.UtcNow)
                return;

            Thread.Sleep(1000);
        }
    }

    private static bool IsSupportedHomeLevel(int level)
    {
        foreach (int supported in SupportedHomeLevels)
        {
            if (level == supported)
                return true;
        }

        return false;
    }

    private static WorkerResponse Positions(WorkerRequest request)
    {
        List<ActiveBuddy> buddies;

        lock (ActiveLock)
            buddies = new List<ActiveBuddy>(ActiveBuddies.Values);

        var positions = new List<BuddyPositionSnapshot>();
        int reported = 0;

        foreach (ActiveBuddy buddy in buddies)
        {
            BuddyPositionSnapshot snapshot = ReadPositionSnapshot(buddy);
            if (snapshot.ObservedUtc != default(DateTime))
                reported++;

            positions.Add(snapshot);
        }

        positions.Sort((left, right) =>
        {
            int levelCompare = Nullable.Compare(left.Level, right.Level);
            return levelCompare != 0
                ? levelCompare
                : Nullable.Compare(left.Index, right.Index);
        });

        WorkerResponse response = Ok(
            request,
            $"Position snapshot: {reported}/{positions.Count} active buddies reported.");
        response.Positions = positions;
        response.Count = positions.Count;
        return response;
    }

    private static BuddyPositionSnapshot ReadPositionSnapshot(ActiveBuddy buddy)
    {
        var fallback = new BuddyPositionSnapshot
        {
            Character = buddy.Character,
            Level = buddy.Level,
            Index = buddy.Index,
            HomeJobId = buddy.NavigationJobId,
            HomeState = buddy.NavigationState,
            HomeDetail = buddy.NavigationDetail,
            Error = "Position snapshot is not available yet."
        };

        string path = GetPositionPath(buddy.Character);

        try
        {
            if (!File.Exists(path))
                return fallback;

            BuddyPositionSnapshot snapshot =
                JsonConvert.DeserializeObject<BuddyPositionSnapshot>(
                    File.ReadAllText(path));

            if (snapshot == null)
            {
                fallback.Error = "Position snapshot contains invalid JSON.";
                return fallback;
            }

            snapshot.Character = buddy.Character;
            snapshot.Level = buddy.Level;
            snapshot.Index = buddy.Index;
            if (string.IsNullOrWhiteSpace(snapshot.HomeJobId))
                snapshot.HomeJobId = buddy.NavigationJobId;
            if (string.IsNullOrWhiteSpace(snapshot.HomeState))
                snapshot.HomeState = buddy.NavigationState;
            if (string.IsNullOrWhiteSpace(snapshot.HomeDetail))
                snapshot.HomeDetail = buddy.NavigationDetail;
            return snapshot;
        }
        catch (Exception ex)
        {
            fallback.Error = "Unable to read position snapshot: " + ex.Message;
            return fallback;
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

    private static string FormatIndexes(List<int> indexes)
    {
        return indexes == null || indexes.Count == 0
            ? "none"
            : string.Join(",", indexes);
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

    private static string GetPositionPath(string character)
    {
        return Path.Combine(
            _baseDir,
            $"citybuddies-position-{character}.json");
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

    private static void DeletePositionSnapshot(string character)
    {
        try
        {
            string path = GetPositionPath(character);
            if (File.Exists(path))
                File.Delete(path);

            string tempPath = path + ".tmp";
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
        }
    }

    private static string GetHomeDirectivePath(string character)
    {
        return Path.Combine(
            _baseDir,
            $"citybuddies-home-{character}.json");
    }

    private static void WriteHomeDirective(ActiveBuddy buddy)
    {
        if (buddy == null || string.IsNullOrWhiteSpace(buddy.NavigationJobId))
            return;

        WriteHomeDirective(
            buddy.Character,
            new BuddyHomeDirective
            {
                JobId = buddy.NavigationJobId,
                RequestedUtc = buddy.NavigationStartedUtc,
                Cancel = false
            });
    }

    private static void CancelHomeDirective(ActiveBuddy buddy)
    {
        if (buddy == null || string.IsNullOrWhiteSpace(buddy.NavigationJobId))
            return;

        WriteHomeDirective(
            buddy.Character,
            new BuddyHomeDirective
            {
                JobId = buddy.NavigationJobId,
                RequestedUtc = buddy.NavigationStartedUtc,
                Cancel = true
            });
    }

    private static void WriteHomeDirective(
        string character,
        BuddyHomeDirective directive)
    {
        try
        {
            string path = GetHomeDirectivePath(character);
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonConvert.SerializeObject(directive));

            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Unable to write home directive for {character}: {ex.Message}");
        }
    }

    private static void DeleteHomeDirective(string character)
    {
        try
        {
            string path = GetHomeDirectivePath(character);
            if (File.Exists(path))
                File.Delete(path);

            string tempPath = path + ".tmp";
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
        }
    }

    private static void MarkSlotLingering(int index, string character)
    {
        lock (ActiveLock)
        {
            if (_slotLingeringUntilUtc != null &&
                index >= 0 &&
                index < _slotLingeringUntilUtc.Length)
            {
                _slotLingeringUntilUtc[index] =
                    DateTime.UtcNow.AddSeconds(ServerLogoutLingerSeconds);
            }
        }

        Console.WriteLine(
            $"Account index {index} is quarantined for " +
            $"{ServerLogoutLingerSeconds}s after unloading {character}.");
    }

    private static void ReconcileHomeNavigation()
    {
        List<ActiveBuddy> candidates;

        lock (ActiveLock)
        {
            candidates = new List<ActiveBuddy>();
            foreach (ActiveBuddy buddy in ActiveBuddies.Values)
            {
                if (buddy.NavigationHold &&
                    !buddy.IsStarting &&
                    !buddy.IsStopping)
                {
                    candidates.Add(buddy);
                }
            }
        }

        foreach (ActiveBuddy candidate in candidates)
        {
            BuddyPositionSnapshot snapshot = ReadPositionSnapshot(candidate);
            string terminalState = null;
            string detail = null;

            if (string.Equals(
                    snapshot.HomeJobId,
                    candidate.NavigationJobId,
                    StringComparison.Ordinal) &&
                IsTerminalHomeState(snapshot.HomeState))
            {
                terminalState = snapshot.HomeState;
                detail = snapshot.HomeDetail;
            }
            else if (
                DateTime.UtcNow - candidate.NavigationStartedUtc >=
                TimeSpan.FromSeconds(HomeNavigationTimeoutSeconds))
            {
                terminalState = "timeout";
                detail =
                    $"No terminal navigation result within " +
                    $"{HomeNavigationTimeoutSeconds}s.";
                CancelHomeDirective(candidate);
            }

            if (terminalState == null)
                continue;

            bool queueLogout = false;

            lock (ActiveLock)
            {
                ActiveBuddy current;
                if (!ActiveBuddies.TryGetValue(candidate.Index, out current) ||
                    !ReferenceEquals(current, candidate) ||
                    !candidate.NavigationHold ||
                    candidate.IsStopping)
                {
                    continue;
                }

                candidate.NavigationHold = false;
                candidate.NavigationState = terminalState;
                candidate.NavigationDetail = detail;
                queueLogout = candidate.NavigationLogoutWhenComplete;

                if (queueLogout)
                {
                    candidate.CleanupQueued = true;
                    candidate.LeaseExpiresUtc = DateTime.UtcNow;
                }
            }

            Console.WriteLine(
                $"Home navigation {terminalState} for {candidate.Character}: " +
                $"{detail ?? "no detail"}");
            RecordHomeNavigationResult(
                candidate.NavigationJobId,
                candidate.Character,
                terminalState);

            if (queueLogout)
            {
                var request = new WorkerRequest
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Command = "sleep",
                    Index = candidate.Index,
                    Purpose = "home-complete",
                    HomeJobId = candidate.NavigationJobId
                };

                QueueSlotOperation(
                    candidate.Index,
                    () => SleepOnSlot(request, candidate.Index));
            }
        }
    }

    private static void RecordHomeNavigationResult(
        string navigationJobId,
        string character,
        string terminalState)
    {
        lock (HomeMaintenanceLock)
        {
            HomeMaintenanceState state = _homeMaintenance;
            if (state == null ||
                string.IsNullOrWhiteSpace(navigationJobId) ||
                !navigationJobId.StartsWith(
                    state.JobId + "-",
                    StringComparison.Ordinal))
            {
                return;
            }

            string resultKey = navigationJobId + "|" + character;
            if (!state.ResultKeys.Add(resultKey))
                return;

            state.Terminal++;
            if (string.Equals(terminalState, "home", StringComparison.Ordinal))
            {
                state.Reached++;
            }
            else
            {
                state.Stopped++;
                state.Failures.Add(character + ":" + terminalState);
            }
        }
    }

    private static bool IsTerminalHomeState(string state)
    {
        return string.Equals(state, "home", StringComparison.Ordinal) ||
               string.Equals(state, "route-unavailable", StringComparison.Ordinal) ||
               string.Equals(state, "movement-unverified", StringComparison.Ordinal) ||
               string.Equals(state, "movement-diverged", StringComparison.Ordinal) ||
               string.Equals(state, "stuck", StringComparison.Ordinal) ||
               string.Equals(state, "failed", StringComparison.Ordinal) ||
               string.Equals(state, "canceled", StringComparison.Ordinal);
    }

    private static void RenewClientDomainLease(
        ClientDomain domain,
        string character)
    {
        try
        {
            if (ClientDomainPluginProxyField == null)
                throw new MissingFieldException("ClientDomain._pluginProxy");

            var proxy =
                ClientDomainPluginProxyField.GetValue(domain) as MarshalByRefObject;

            if (proxy == null)
                throw new InvalidOperationException("ClientDomain plugin proxy is unavailable.");

            var lease = proxy.GetLifetimeService() as ILease;
            if (lease == null)
            {
                Console.WriteLine(
                    $"Client-domain proxy for {character} already has an infinite lifetime.");
                return;
            }

            lease.Renew(TimeSpan.FromMinutes(ClientDomainLeaseMinutes));
            Console.WriteLine(
                $"Renewed client-domain proxy for {character} for " +
                $"{ClientDomainLeaseMinutes} minutes.");
        }
        catch (Exception ex)
        {
            // Login can continue because TryUnloadClientDomain has a direct
            // AppDomain fallback that does not depend on the proxy lease.
            Console.WriteLine(
                $"Unable to renew client-domain proxy for {character}: {ex.Message}. " +
                "Direct unload fallback remains available.");
        }
    }

    private static bool TryUnloadClientDomain(
        ClientDomain domain,
        string character,
        out string error)
    {
        error = null;

        if (domain == null)
            return true;

        Exception gracefulError;

        try
        {
            domain.Unload();
            return true;
        }
        catch (Exception ex)
        {
            gracefulError = ex;
            Console.WriteLine(
                $"Graceful unload proxy failed for {character}: {ex.Message}. " +
                "Trying direct AppDomain unload.");
        }

        try
        {
            if (ClientDomainAppDomainField == null)
                throw new MissingFieldException("ClientDomain._appDomain");

            var childDomain =
                ClientDomainAppDomainField.GetValue(domain) as AppDomain;

            if (childDomain == null)
                throw new InvalidOperationException("ClientDomain AppDomain is unavailable.");

            AppDomain.Unload(childDomain);
            Console.WriteLine(
                $"Direct AppDomain unload succeeded for {character}.");
            return true;
        }
        catch (AppDomainUnloadedException)
        {
            // The child domain is already gone, which is the desired state.
            Console.WriteLine(
                $"Client AppDomain for {character} was already unloaded.");
            return true;
        }
        catch (Exception forcedError)
        {
            error =
                $"Graceful unload failed: {gracefulError.Message}; " +
                $"direct AppDomain unload failed: {forcedError.Message}";
            return false;
        }
    }

    private static void ShutdownAll()
    {
        var indexes = new List<int>();

        lock (ActiveLock)
            indexes.AddRange(ActiveBuddies.Keys);

        var request = new WorkerRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Command = "sleepmany",
            Indexes = indexes,
            Purpose = "service-shutdown"
        };

        if (indexes.Count > 0)
        {
            WorkerResponse response = SleepIndexes(request, indexes, "Shutdown");
            Console.WriteLine(response.Message);
        }

        lock (ActiveLock)
            ActiveBuddies.Clear();
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
        public int? ActiveLimit;
        public int? MaxParallelLogins;
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
        public int CleanupFailures;
        public DateTime? NextCleanupAttemptUtc;
        public bool IsStarting;
        public bool IsStopping;
        public bool CleanupQueued;
        public bool NavigationHold;
        public string NavigationJobId;
        public DateTime NavigationStartedUtc;
        public bool NavigationLogoutWhenComplete;
        public string NavigationState;
        public string NavigationDetail;
    }

    private class WorkerRequest
    {
        public string Id;
        public string Command;
        public int? Level;
        public int? Index;
        public List<int> Indexes;
        public string Purpose;
        public int? LeaseSeconds;
        public bool Home;
        public bool LogoutAfterHome;
        public string HomeJobId;
    }

    private sealed class HomeMaintenanceState
    {
        public string JobId;
        public bool Running;
        public DateTime StartedUtc;
        public DateTime? FinishedUtc;
        public string Detail;
        public int Attempted;
        public int Started;
        public int Terminal;
        public int Reached;
        public int Stopped;
        public readonly List<int> Levels = new List<int>();
        public readonly HashSet<string> ResultKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Failures = new List<string>();
    }

    private sealed class SlotOperation
    {
        public Func<WorkerResponse> Action;
        public readonly ManualResetEventSlim Completed =
            new ManualResetEventSlim(false);
        public WorkerResponse Response;
        public Exception Error;
    }

    private sealed class BuddySlotWorker
    {
        private readonly BlockingCollection<SlotOperation> _queue =
            new BlockingCollection<SlotOperation>();
        private readonly Thread _thread;

        public BuddySlotWorker(int index)
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"CityDwellers.Buddy.{index:D2}"
            };
            _thread.Start();
        }

        public SlotOperation Enqueue(Func<WorkerResponse> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            var operation = new SlotOperation { Action = action };
            _queue.Add(operation);
            return operation;
        }

        public void StopAccepting()
        {
            _queue.CompleteAdding();
        }

        public void Join()
        {
            _thread.Join();
            _queue.Dispose();
        }

        private void Run()
        {
            foreach (SlotOperation operation in _queue.GetConsumingEnumerable())
            {
                try
                {
                    operation.Response = operation.Action();
                }
                catch (Exception ex)
                {
                    operation.Error = ex;
                }
                finally
                {
                    operation.Completed.Set();
                }
            }
        }
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
        public List<BuddyPositionSnapshot> Positions;
        public string HomeJobId;
        public bool HomeRunning;
        public int HomeAttempted;
        public int HomeStarted;
        public int HomeTerminal;
        public int HomeReached;
        public int HomeStopped;
        public List<string> HomeFailures;
    }
}
