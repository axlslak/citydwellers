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

public class FlipperLoader
{
    private const string PipeName = "citydwellers-flipper";
    private const int FailedProbeCooldownMilliseconds = 90000;

    private static Config _config;
    private static AccountInfo _account;
    private static string _settingsDir;
    private static string _dataDir;
    private static string _pluginPath;
    private static string _pluginDir;
    private static string _toggleRequestPath;
    private static string _operationIdPath;
    private static string _cancelRequestPath;
    private static int _timeoutMs;
    private static bool _interactive;
    private static volatile bool _stopping;
    private static readonly ManualResetEvent ProbeIdle = new ManualResetEvent(true);
    private static long _failedProbeCooldownStartedTimestamp;

    static void Main(string[] args)
    {
        Environment.ExitCode = Run(args, null, true);
    }

    public static int Run(
        string[] args,
        WaitHandle stopSignal,
        bool interactive)
    {
        _interactive = interactive;
        _stopping = false;
        ProbeIdle.Set();
        Interlocked.Exchange(ref _failedProbeCooldownStartedTimestamp, 0L);

        if (ClientlessGameDataBootstrap.IsRestoreCommand(args))
        {
            return ClientlessGameDataBootstrap.Run(args);
        }

        string settingsError;
        if (!SettingsPaths.TryEnsureDirectories(
                out _settingsDir,
                out _dataDir,
                out settingsError))
        {
            StopForConfiguration(settingsError);
            return 1;
        }

        if (!LoadConfig())
        {
            return 1;
        }

        if (args.Length == 0)
        {
            return RunService(stopSignal);
        }

        if (args.Length == 1 &&
            string.Equals(args[0], "probe", StringComparison.OrdinalIgnoreCase))
        {
            RunManualProbe(false);
            return 0;
        }

        if (args.Length == 1 &&
            string.Equals(args[0], "login-test", StringComparison.OrdinalIgnoreCase))
        {
            RunManualLoginTest();
            return 0;
        }

        if (args.Length == 1 &&
            string.Equals(args[0], "toggle", StringComparison.OrdinalIgnoreCase))
        {
            RunManualProbe(true);
            return 0;
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  CityDwellers.exe             # all services, interactive");
        Console.WriteLine("  CityDwellers.exe flipper-probe");
        Console.WriteLine("  CityDwellers.exe flipper-login-test");
        Console.WriteLine("  CityDwellers.exe flipper-toggle");
        return 1;
    }

    private static bool LoadConfig()
    {
        string configPath = SettingsPaths.GetFilePath(_settingsDir, "citydwellers.json");
        string configError;
        if (!SettingsPaths.TryReadSettingsSection(
                _settingsDir,
                "Flipper",
                out _config,
                out configError))
        {
            StopForConfiguration(configError);
            return false;
        }

        if (_config == null ||
            _config.Accounts == null ||
            _config.Accounts.Count != 1)
        {
            StopForConfiguration(
                $"'{configPath}' must contain exactly one account.");
            return false;
        }

        _account = _config.Accounts[0];

        if (_account == null ||
            string.IsNullOrWhiteSpace(_account.Username) ||
            string.IsNullOrWhiteSpace(_account.Password) ||
            string.IsNullOrWhiteSpace(_account.Character))
        {
            StopForConfiguration(
                $"The account in '{configPath}' requires Username, Password, and Character.");
            return false;
        }

        if (IsDefaultAccount(_account))
        {
            StopForConfiguration(
                $"'{configPath}' still contains the user1/pass1/char1 defaults. " +
                "Replace them with the Flipper account before starting the service.");
            return false;
        }

        _timeoutMs = _config.ProbeTimeoutMs > 0
            ? _config.ProbeTimeoutMs
            : 20000;

        _pluginPath = Path.Combine(_settingsDir, "CityFlipper.dll");
        if (!File.Exists(_pluginPath))
        {
            StopForConfiguration(
                $"Required Flipper plugin was not found at '{_pluginPath}'.");
            return false;
        }

        _pluginDir = Path.GetDirectoryName(_pluginPath);
        _toggleRequestPath =
            Path.Combine(_dataDir, "cityflipper-toggle.request");
        _operationIdPath =
            Path.Combine(_dataDir, "cityflipper-operation.id");
        _cancelRequestPath =
            Path.Combine(_dataDir, "cityflipper-cancel.request");

        DeleteIfExists(_toggleRequestPath);
        DeleteIfExists(_operationIdPath);

        FlipperCacheStore.Initialize(
            _dataDir,
            _config.CacheFreshSeconds > 0 ? _config.CacheFreshSeconds : 60);

        return true;
    }

    private static bool IsDefaultAccount(AccountInfo account)
    {
        return string.Equals(account.Username, "user1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(account.Password, "pass1", StringComparison.Ordinal) ||
               string.Equals(account.Character, "char1", StringComparison.OrdinalIgnoreCase);
    }

    private static void StopForConfiguration(string message)
    {
        Console.WriteLine(message);
        if (_interactive)
        {
            Console.WriteLine();
            Console.WriteLine("Press ENTER to exit.");
            Console.ReadLine();
        }
    }

    private static int RunService(WaitHandle stopSignal)
    {
        Console.WriteLine("======================================");
        Console.WriteLine(" City Dwellers - Flipper Service");
        Console.WriteLine("======================================");
        Console.WriteLine();
        Console.WriteLine($"Character: {_account.Character}");
        Console.WriteLine($"Pipe:      {PipeName}");
        Console.WriteLine($"Cache:     {(_config.CacheFreshSeconds > 0 ? _config.CacheFreshSeconds : 60)}s fresh window");
        Console.WriteLine();
        Console.WriteLine("Flipper service idle. Apcflipper is NOT logged in.");
        Console.WriteLine("Recent confirmed city state is served from cache before a new login.");
        Console.WriteLine("Enable-only requests may raise cloak, but can never lower it.");
        Console.WriteLine("Waiting for Manager requests.");
        if (_interactive)
            Console.WriteLine("Press ENTER to stop Flipper.");
        Console.WriteLine();

        Thread pipeThread = new Thread(RunPipeServer)
        {
            IsBackground = true,
            Name = "CityDwellers.Flipper.Pipe"
        };

        pipeThread.Start();

        if (stopSignal != null)
            stopSignal.WaitOne();
        else
            Console.ReadLine();

        _stopping = true;
        DeleteIfExists(_toggleRequestPath);
        DeleteIfExists(_operationIdPath);
        if (!ProbeIdle.WaitOne(TimeSpan.FromSeconds(15)))
        {
            Console.WriteLine(
                "Flipper probe did not finish unloading within 15 seconds; " +
                "the unified host will continue shutdown.");
        }
        Console.WriteLine("Flipper service stopped.");
        return 0;
    }

    private static void RunPipeServer()
    {
        while (!_stopping)
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
                            Message = $"Invalid Flipper request: {ex.Message}"
                        };
                    }

                    writer.WriteLine(JsonConvert.SerializeObject(response));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Flipper pipe server error: {ex}");
                Thread.Sleep(500);
            }
        }
    }

    private static WorkerResponse HandleRequest(WorkerRequest request)
    {
        if (_stopping)
            return Fail(request, "Flipper service is stopping; no AO client was started.");

        if (request == null || string.IsNullOrWhiteSpace(request.Command))
            return Fail(request, "Missing command.");

        string command = request.Command.Trim().ToLowerInvariant();

        Console.WriteLine(
            $"IPC request {request.Id ?? "<no-id>"}: {command}");

        switch (command)
        {
            case "observe":
                return Observe(request, true);

            case "probe":
                return Observe(request, false);

            case "ensure-enabled":
                return EnsureEnabled(request);

            case "ensure-disabled-ready":
                return EnsureDisabledReady(request);

            case "ensure-disabled-watch":
                return EnsureDisabledWatch(request);

            case "ping":
                return Ok(request, "Flipper service is running.");

            default:
                return Fail(
                    request,
                    $"Unknown Flipper command '{request.Command}'.");
        }
    }

    private static WorkerResponse Observe(WorkerRequest request, bool allowFreshCache)
    {
        FlipperCacheSnapshot cached;

        if (allowFreshCache && FlipperCacheStore.TryGetFresh(out cached))
        {
            Console.WriteLine(
                $"Serving recent Flipper cache from {cached.ObservedUtc:O} ({cached.Source}).");

            return FromCache(
                request,
                cached,
                "Recent confirmed Flipper state served from cache.");
        }

        ProbeRun run = RunProbe(false);

        if (run.Success && run.Result != null)
            return FromFreshResult(request, run.Result);

        if (FlipperCacheStore.TryGetAny(out cached))
        {
            Console.WriteLine(
                $"Fresh probe failed; falling back to Flipper cache from {cached.ObservedUtc:O} ({cached.Source}).");

            return FromCache(
                request,
                cached,
                "Fresh probe failed; using last confirmed Flipper state.");
        }

        return Fail(
            request,
            "Flipper probe failed and no confirmed cache exists. See Flipper console for details.");
    }

    private static WorkerResponse EnsureEnabled(WorkerRequest request)
    {
        FlipperCacheSnapshot cached;

        if (FlipperCacheStore.TryGetFresh(out cached) &&
            IsEnabled(cached.CloakState) &&
            CacheMeetsTrigger(cached, request))
        {
            Console.WriteLine(
                $"Cloak already confirmed enabled at {cached.ObservedUtc:O}; no login needed.");

            return FromCache(
                request,
                cached,
                "Cloak already enabled; no action needed.");
        }

        ProbeRun run = RunProbe("enable");

        if (!run.Success || run.Result == null)
        {
            if (FlipperCacheStore.TryGetAny(out cached) &&
                IsEnabled(cached.CloakState) &&
                CacheMeetsTrigger(cached, request))
            {
                return FromCache(
                    request,
                    cached,
                    "Enable probe failed, but a confirmed post-trigger cache already shows cloak enabled.");
            }

            return Fail(
                request,
                "Unable to ensure cloak is enabled. See Flipper console for details.");
        }

        if (run.Result.ToggleSent)
        {
            return BuildEnsureEnabledResponse(request, run.Result);
        }

        WorkerResponse observed = FromFreshResult(request, run.Result);

        if (IsEnabled(observed.CloakState))
        {
            observed.Ok = true;
            observed.Message = "Cloak already enabled; no action needed.";
            return observed;
        }

        observed.Ok = false;
        observed.Message = !string.IsNullOrWhiteSpace(run.Result.ToggleBlockedReason)
            ? run.Result.ToggleBlockedReason
            : "Cloak is not enabled and Flipper did not send an enable action.";

        return observed;
    }

    private static WorkerResponse BuildEnsureEnabledResponse(
        WorkerRequest request,
        FlipperResult result)
    {
        string finalState = result.PostToggleCloakState;
        bool enabled =
            result.ToggleSucceeded &&
            string.Equals(
                finalState,
                "Enabled",
                StringComparison.OrdinalIgnoreCase);

        return new WorkerResponse
        {
            Id = request.Id,
            Ok = enabled,
            Message = enabled
                ? "Flipper raised and verified the city cloak."
                : !string.IsNullOrWhiteSpace(result.ToggleBlockedReason)
                    ? result.ToggleBlockedReason
                    : $"Enable action was sent, but the confirmed post-toggle state was " +
                      $"'{finalState ?? "Unknown"}'.",
            CloakState = finalState ?? result.InitialCloakState,
            ShieldTimerInSeconds = enabled
                ? result.PostToggleShieldTimerInSeconds
                : (int?)result.InitialShieldTimerInSeconds,
            ControllerCharge = result.ControllerCharge,
            Character = _account.Character,
            Cached = false,
            ObservedUtc = DateTime.UtcNow,
            ActionSent = result.ToggleSent
        };
    }

    private static WorkerResponse EnsureDisabledReady(WorkerRequest request)
    {
        // Starting a raid must never trust cache. The keeper logs in, reads the
        // live controller, and the plugin lowers only when the cloak is enabled,
        // toggleable, and the controller is at least 75% charged.
        ProbeRun run = RunProbe("disable-ready", _timeoutMs, request.Id);

        if (run.Canceled)
        {
            return Fail(
                request,
                "Raid-start operation canceled before the cloak was lowered.");
        }

        if (!run.Success || run.Result == null)
        {
            return Fail(
                request,
                "Unable to verify CT charge and lower the cloak. See Flipper console for details.");
        }

        return BuildEnsureDisabledResponse(request, run.Result);
    }

    private static WorkerResponse EnsureDisabledWatch(WorkerRequest request)
    {
        int watchSeconds = Math.Max(
            1,
            Math.Min(60, request.TimeoutSeconds ?? 60));

        // Keep one client online for the remaining fill window. The plugin
        // refreshes CT charge and lowers immediately when it reaches 75%.
        ProbeRun run = RunProbe(
            $"disable-watch:{watchSeconds}",
            Math.Max(_timeoutMs, (watchSeconds + 15) * 1000),
            request.Id);

        if (run.Canceled)
        {
            return Fail(
                request,
                "Raid-start operation canceled before the cloak was lowered.");
        }

        if (!run.Success || run.Result == null)
        {
            return Fail(
                request,
                "Unable to watch CT charge and lower the cloak. See Flipper console for details.");
        }

        return BuildEnsureDisabledResponse(request, run.Result);
    }

    private static WorkerResponse BuildEnsureDisabledResponse(
        WorkerRequest request,
        FlipperResult result)
    {

        if (!result.ToggleSent)
        {
            WorkerResponse blocked = FromFreshResult(request, result);
            blocked.Ok = false;
            blocked.Message = !string.IsNullOrWhiteSpace(result.ToggleBlockedReason)
                ? result.ToggleBlockedReason
                : "The live raid-start probe did not send a lower-cloak action.";
            return blocked;
        }

        string finalState = result.PostToggleCloakState;

        bool disabled =
            result.ToggleSucceeded &&
            string.Equals(
                finalState,
                "Disabled",
                StringComparison.OrdinalIgnoreCase);

        return new WorkerResponse
        {
            Id = request.Id,
            Ok = disabled,
            Message = disabled
                ? "Flipper verified CT readiness and lowered the city cloak."
                : $"Lower action was sent, but the confirmed post-toggle state was " +
                  $"'{finalState ?? "Unknown"}'.",
            CloakState = finalState,
            ShieldTimerInSeconds = disabled
                ? result.PostToggleShieldTimerInSeconds
                : (int?)result.InitialShieldTimerInSeconds,
            ControllerCharge = result.ControllerCharge,
            Character = _account.Character,
            Cached = false,
            ObservedUtc = DateTime.UtcNow,
            ActionSent = result.ToggleSent
        };
    }

    private static bool CacheMeetsTrigger(
        FlipperCacheSnapshot cache,
        WorkerRequest request)
    {
        return !request.NotBeforeUtc.HasValue ||
               UtcTimestamp.Normalize(cache.ObservedUtc) >=
               UtcTimestamp.Normalize(request.NotBeforeUtc.Value);
    }

    private static bool IsEnabled(string state)
    {
        return string.Equals(state, "Enabled", StringComparison.OrdinalIgnoreCase);
    }

    private static WorkerResponse FromFreshResult(
        WorkerRequest request,
        FlipperResult result)
    {
        string cloakState = GetDictionaryValue(
            result.CloakInfo,
            "CloakState");

        int shieldTimer;
        int? parsedTimer = null;

        if (int.TryParse(
            GetDictionaryValue(result.CloakInfo, "ShieldTimerInSeconds"),
            out shieldTimer))
        {
            parsedTimer = shieldTimer;
        }

        return new WorkerResponse
        {
            Id = request.Id,
            Ok = true,
            Message = "Fresh City Controller observation complete.",
            CloakState = cloakState,
            ShieldTimerInSeconds = parsedTimer,
            ControllerCharge = result.ControllerCharge,
            Character = _account.Character,
            Cached = false,
            ObservedUtc = DateTime.UtcNow
        };
    }

    private static WorkerResponse FromCache(
        WorkerRequest request,
        FlipperCacheSnapshot cache,
        string message)
    {
        return new WorkerResponse
        {
            Id = request.Id,
            Ok = true,
            Message = message,
            CloakState = cache.CloakState,
            ShieldTimerInSeconds = cache.ShieldTimerInSeconds,
            ControllerCharge = cache.ControllerCharge,
            Character = _account.Character,
            Cached = true,
            ObservedUtc = cache.ObservedUtc
        };
    }

    private static void RunManualProbe(bool toggle)
    {
        Console.WriteLine("======================================");
        Console.WriteLine(" City Dwellers - Flipper Probe");
        Console.WriteLine("======================================");
        Console.WriteLine();
        Console.WriteLine($"Character: {_account.Character}");
        Console.WriteLine($"Mode:      {(toggle ? "TOGGLE" : "OBSERVE")}");
        Console.WriteLine();

        ProbeRun run = RunProbe(toggle);

        Console.WriteLine();

        if (!run.Success)
            Console.WriteLine("PROBE FAILED.");
        else
            PrintResult(run);

        Console.WriteLine();
        if (_interactive)
        {
            Console.WriteLine("Press ENTER to exit.");
            Console.ReadLine();
        }
    }

    private static void RunManualLoginTest()
    {
        Console.WriteLine("======================================");
        Console.WriteLine(" City Dwellers - Flipper Login Test");
        Console.WriteLine("======================================");
        Console.WriteLine();
        Console.WriteLine($"Character: {_account.Character}");
        Console.WriteLine("Mode:      LOGIN ONLY (no city or cloak actions)");
        Console.WriteLine();

        ProbeRun run = RunProbe("login-test");

        Console.WriteLine();
        Console.WriteLine(
            run.Success
                ? "LOGIN TEST PASSED: Apcflipper reached InPlay."
                : "LOGIN TEST FAILED: Apcflipper did not reach InPlay.");

        Console.WriteLine();
        if (_interactive)
        {
            Console.WriteLine("Press ENTER to exit.");
            Console.ReadLine();
        }
    }

    private static ProbeRun RunProbe(bool toggle)
    {
        return RunProbe(toggle ? "toggle" : null);
    }

    private static ProbeRun RunProbe(string requestedAction)
    {
        return RunProbe(requestedAction, _timeoutMs);
    }

    private static ProbeRun RunProbe(string requestedAction, int timeoutMs)
    {
        return RunProbe(requestedAction, timeoutMs, null);
    }

    private static ProbeRun RunProbe(
        string requestedAction,
        int timeoutMs,
        string operationId)
    {
        var run = new ProbeRun();

        if (_stopping)
        {
            Console.WriteLine("Flipper probe canceled because the service is stopping.");
            run.Canceled = true;
            return run;
        }

        int cooldownRemainingMilliseconds;
        if (TryGetFailedProbeCooldownRemaining(out cooldownRemainingMilliseconds))
        {
            Console.WriteLine(
                "Flipper login cooling down for another " +
                Math.Max(1, (int)Math.Ceiling(cooldownRemainingMilliseconds / 1000.0)) +
                "s after an unsuccessful logged-in probe; no AO client was started.");
            return run;
        }

        bool actionRequested = !string.IsNullOrWhiteSpace(requestedAction);
        bool ensureEnabled = string.Equals(
            requestedAction,
            "enable",
            StringComparison.OrdinalIgnoreCase);
        bool loginTest = string.Equals(
            requestedAction,
            "login-test",
            StringComparison.OrdinalIgnoreCase);
        bool watchController =
            requestedAction != null &&
            requestedAction.StartsWith(
                "disable-watch:",
                StringComparison.OrdinalIgnoreCase);

        Console.WriteLine();
        Console.WriteLine("--------------------------------------");
        Console.WriteLine(
            ensureEnabled
                ? "ENSURE ENABLED PROBE"
                : loginTest
                    ? "LOGIN-ONLY TEST"
                : watchController
                    ? "RAID CT WATCH"
                    : actionRequested
                        ? "TOGGLE PROBE"
                        : "OBSERVE PROBE");
        Console.WriteLine("--------------------------------------");

        string resultPath =
            Path.Combine(_dataDir, "cityflipper-result.json");
        string tempPath = resultPath + ".tmp";

        DeleteIfExists(resultPath);
        DeleteIfExists(tempPath);
        DeleteIfExists(_toggleRequestPath);
        DeleteIfExists(_operationIdPath);

        if (actionRequested)
            File.WriteAllText(_toggleRequestPath, requestedAction);
        if (!string.IsNullOrWhiteSpace(operationId))
            File.WriteAllText(_operationIdPath, operationId);

        Logger logger = new LoggerConfiguration()
            .WriteTo.Console(
                outputTemplate: LoggingDefaults.ConsoleOutputTemplate)
            .MinimumLevel.Debug()
            .CreateLogger();

        ClientDomain domain = null;
        bool domainStarted = false;
        Stopwatch totalTimer = Stopwatch.StartNew();

        ProbeIdle.Reset();
        try
        {
            if (_stopping)
            {
                run.Canceled = true;
                return run;
            }

            if (IsFileValue(_cancelRequestPath, operationId))
            {
                Console.WriteLine(
                    $"[{totalTimer.Elapsed.TotalSeconds:F3}s] " +
                    "Raid-start operation canceled before client login.");
                run.Canceled = true;
                run.Success = false;
                return run;
            }

            Console.WriteLine(
                $"[{totalTimer.Elapsed.TotalSeconds:F3}s] Creating client domain.");

            domain = Client.CreateInstance(
                _account.Username,
                _account.Password,
                _account.Character,
                Dimension.RubiKa,
                logger);

            domain.LoadPlugin(_pluginPath);

            if (_stopping)
            {
                Console.WriteLine(
                    $"[{totalTimer.Elapsed.TotalSeconds:F3}s] " +
                    "Service shutdown began before AO login; unloading without starting client.");
                run.Canceled = true;
                return run;
            }

            Console.WriteLine(
                $"[{totalTimer.Elapsed.TotalSeconds:F3}s] Starting AO client.");

            domain.Start();
            domainStarted = true;

            Stopwatch resultWaitTimer = Stopwatch.StartNew();

            while (!File.Exists(resultPath))
            {
                if (_stopping)
                {
                    Console.WriteLine(
                        $"[{totalTimer.Elapsed.TotalSeconds:F3}s] " +
                        "Service shutdown began; unloading active Flipper client.");
                    run.Canceled = true;
                    run.Success = false;
                    return run;
                }

                if (IsFileValue(_cancelRequestPath, operationId))
                {
                    Console.WriteLine(
                        $"[{totalTimer.Elapsed.TotalSeconds:F3}s] " +
                        "Raid-start operation canceled; unloading client.");
                    run.Canceled = true;
                    run.Success = false;
                    return run;
                }

                if (resultWaitTimer.ElapsedMilliseconds >= timeoutMs)
                {
                    Console.WriteLine(
                        $"[{totalTimer.Elapsed.TotalSeconds:F3}s] " +
                        "TIMEOUT waiting for CityFlipper result.");

                    run.Success = false;
                    return run;
                }

                Thread.Sleep(50);
            }

            totalTimer.Stop();

            Console.WriteLine(
                $"[{totalTimer.Elapsed.TotalSeconds:F3}s] Observation received.");

            string json = File.ReadAllText(resultPath);
            run.Result = JsonConvert.DeserializeObject<FlipperResult>(json);
            run.TotalMilliseconds = totalTimer.Elapsed.TotalMilliseconds;
            run.Success = run.Result != null && !run.Result.ProbeFailed;
            run.Canceled = run.Result != null && run.Result.Canceled;

            if (run.Result != null && run.Result.ProbeFailed)
            {
                Console.WriteLine(
                    "CityFlipper reported a failed AO session: " +
                    (string.IsNullOrWhiteSpace(run.Result.ToggleBlockedReason)
                        ? "no additional reason was supplied."
                        : run.Result.ToggleBlockedReason));
            }

            if (run.Success && !loginTest)
                FlipperCacheStore.SaveFromResult(run.Result);

            return run;
        }
        catch (Exception ex)
        {
            totalTimer.Stop();

            Console.WriteLine();
            Console.WriteLine("FLIPPER PROBE EXCEPTION:");
            Console.WriteLine(ex);

            run.Success = false;
            run.TotalMilliseconds = totalTimer.Elapsed.TotalMilliseconds;

            return run;
        }
        finally
        {
            if (domain != null)
            {
                Console.WriteLine();
                Console.WriteLine("Unloading flipper client...");

                try
                {
                    domain.Unload();
                    Console.WriteLine("Flipper client unloaded.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Client unload failed: {ex}");
                }
            }

            if (domainStarted && !run.Success && !run.Canceled && !_stopping)
            {
                Interlocked.Exchange(
                    ref _failedProbeCooldownStartedTimestamp,
                    Stopwatch.GetTimestamp());
                Console.WriteLine(
                    "Unsuccessful Flipper login/probe entered a 90-second monotonic " +
                    "cooldown so AO can release the character session.");
            }

            DeleteIfExists(resultPath);
            DeleteIfExists(tempPath);
            DeleteIfExists(_toggleRequestPath);
            DeleteIfExists(_operationIdPath);
            DeleteIfContains(_cancelRequestPath, operationId);
            ProbeIdle.Set();
        }
    }

    private static bool TryGetFailedProbeCooldownRemaining(
        out int remainingMilliseconds)
    {
        remainingMilliseconds = 0;
        long started = Interlocked.Read(ref _failedProbeCooldownStartedTimestamp);
        if (started <= 0)
            return false;

        long elapsedTicks = Stopwatch.GetTimestamp() - started;
        double elapsedMilliseconds =
            elapsedTicks * 1000.0 / Stopwatch.Frequency;
        double remaining = FailedProbeCooldownMilliseconds - elapsedMilliseconds;
        if (remaining <= 0)
        {
            Interlocked.Exchange(ref _failedProbeCooldownStartedTimestamp, 0L);
            return false;
        }

        remainingMilliseconds = (int)Math.Ceiling(remaining);
        return true;
    }

    private static void PrintResult(ProbeRun run)
    {
        FlipperResult result = run.Result;

        Console.WriteLine("PROBE RESULT");
        Console.WriteLine();
        Console.WriteLine(
            $"Total host time:             {run.TotalMilliseconds:F0} ms");
        Console.WriteLine(
            $"Plugin Init -> CharInPlay:   {result.InitToInPlayMs:F0} ms");
        Console.WriteLine(
            $"Plugin Init -> Controller:   {result.InitToControllerMs:F0} ms");
        Console.WriteLine(
            $"Plugin Init -> CityInfo:     {result.InitToCityInfoMs:F0} ms");
        Console.WriteLine(
            $"Plugin Init -> CloakInfo:    {result.InitToCloakInfoMs:F0} ms");
        Console.WriteLine(
            $"Plugin Init -> ChargeInfo:   {result.InitToChargeInfoMs:F0} ms");

        Console.WriteLine();
        Console.WriteLine("Controller charge:");
        Console.WriteLine($"  Raw = {result.ControllerCharge}");
        Console.WriteLine(
            $"  Candidate percent = {result.ControllerCharge * 100:F1}%");

        Console.WriteLine();
        Console.WriteLine("CloakInfo:");
        PrintDictionary(result.CloakInfo);

        if (result.ToggleRequested)
        {
            Console.WriteLine();
            Console.WriteLine("Toggle test:");
            Console.WriteLine($"  Requested = {result.ToggleRequested}");
            Console.WriteLine($"  Sent = {result.ToggleSent}");
            Console.WriteLine($"  Initial state = {result.InitialCloakState}");
            Console.WriteLine(
                $"  Initial shield timer = {result.InitialShieldTimerInSeconds}");

            if (!string.IsNullOrWhiteSpace(result.ToggleBlockedReason))
                Console.WriteLine($"  Blocked/reason = {result.ToggleBlockedReason}");

            if (result.ToggleSent)
            {
                Console.WriteLine(
                    $"  Plugin Init -> ToggleSent: {result.InitToToggleSentMs:F0} ms");
                Console.WriteLine(
                    $"  Plugin Init -> PostToggleCloakInfo: " +
                    $"{result.InitToPostToggleCloakInfoMs:F0} ms");
                Console.WriteLine($"  Post state = {result.PostToggleCloakState}");
                Console.WriteLine(
                    $"  Post shield timer = {result.PostToggleShieldTimerInSeconds}");
                Console.WriteLine($"  State changed = {result.ToggleSucceeded}");
            }
        }
    }

    private static string GetDictionaryValue(
        Dictionary<string, string> values,
        string key)
    {
        if (values == null)
            return null;

        string value;
        return values.TryGetValue(key, out value) ? value : null;
    }

    private static void PrintDictionary(Dictionary<string, string> values)
    {
        if (values == null || values.Count == 0)
        {
            Console.WriteLine("  <none>");
            return;
        }

        foreach (var item in values)
            Console.WriteLine($"  {item.Key} = {item.Value}");
    }

    private static WorkerResponse Ok(WorkerRequest request, string message)
    {
        return new WorkerResponse
        {
            Id = request?.Id,
            Ok = true,
            Message = message
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

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void DeleteIfContains(string path, string expectedValue)
    {
        if (string.IsNullOrWhiteSpace(expectedValue))
            return;

        try
        {
            if (IsFileValue(path, expectedValue))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static bool IsFileValue(string path, string expectedValue)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(expectedValue))
        {
            return false;
        }

        try
        {
            return File.Exists(path) &&
                   string.Equals(
                       File.ReadAllText(path).Trim(),
                       expectedValue,
                       StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public class Config
    {
        public List<AccountInfo> Accounts;
        public int ProbeTimeoutMs = 20000;
        public int DelayBetweenPassesMs = 5000;
        public int CacheFreshSeconds = 60;
    }

    public class AccountInfo
    {
        public string Username;
        public string Password;
        public string Character;
    }

    public class ProbeRun
    {
        public bool Success;
        public bool Canceled;
        public double TotalMilliseconds;
        public FlipperResult Result;
    }

    public class FlipperResult
    {
        public string Character;

        public double InitToInPlayMs;
        public double InitToControllerMs;
        public double InitToCityInfoMs;
        public double InitToCloakInfoMs;
        public double InitToChargeInfoMs;

        public float ControllerCharge;

        public Dictionary<string, string> CityInfo;
        public Dictionary<string, string> CloakInfo;

        public bool ToggleRequested;
        public bool ProbeFailed;
        public bool Canceled;
        public bool ToggleSent;
        public bool ToggleSucceeded;
        public string ToggleBlockedReason;
        public double InitToToggleSentMs;
        public double InitToPostToggleCloakInfoMs;
        public string InitialCloakState;
        public int InitialShieldTimerInSeconds;
        public string PostToggleCloakState;
        public int PostToggleShieldTimerInSeconds;
        public Dictionary<string, string> PostToggleCloakInfo;
    }

    private class WorkerRequest
    {
        public string Id;
        public string Command;
        public DateTime? NotBeforeUtc;
        public int? TimeoutSeconds;
    }

    private class WorkerResponse
    {
        public string Id;
        public bool Ok;
        public string Message;
        public string Character;
        public string CloakState;
        public int? ShieldTimerInSeconds;
        public float? ControllerCharge;
        public bool Cached;
        public DateTime? ObservedUtc;
        public bool ActionSent;
    }
}
