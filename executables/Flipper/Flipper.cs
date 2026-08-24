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

    private static Config _config;
    private static AccountInfo _account;
    private static string _baseDir;
    private static string _settingsDir;
    private static string _pluginPath;
    private static string _pluginDir;
    private static string _toggleRequestPath;
    private static int _timeoutMs;

    static void Main(string[] args)
    {
        if (ClientlessGameDataBootstrap.IsRestoreCommand(args))
        {
            Environment.ExitCode = ClientlessGameDataBootstrap.Run(args);
            return;
        }

        _baseDir = AppDomain.CurrentDomain.BaseDirectory;

        string settingsError;
        if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out settingsError))
        {
            StopForConfiguration(settingsError);
            Environment.Exit(1);
            return;
        }

        if (!LoadConfig())
        {
            Environment.Exit(1);
            return;
        }

        if (args.Length == 0)
        {
            RunService();
            return;
        }

        if (args.Length == 1 &&
            string.Equals(args[0], "probe", StringComparison.OrdinalIgnoreCase))
        {
            RunManualProbe(false);
            return;
        }

        if (args.Length == 1 &&
            string.Equals(args[0], "toggle", StringComparison.OrdinalIgnoreCase))
        {
            RunManualProbe(true);
            return;
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  Flipper.exe         # persistent idle service");
        Console.WriteLine("  Flipper.exe probe   # one read-only controller probe");
        Console.WriteLine("  Flipper.exe toggle  # one guarded cloak toggle probe");
        Environment.Exit(1);
    }

    private static bool LoadConfig()
    {
        string configPath = SettingsPaths.GetFilePath(_settingsDir, "flipper.json");

        if (!File.Exists(configPath))
        {
            string templateError;
            if (!SettingsPaths.TryCreateFile(
                    configPath,
                    BuildDefaultConfig(),
                    out templateError))
            {
                StopForConfiguration(templateError);
                return false;
            }

            StopForConfiguration(
                $"Created a Flipper configuration template at '{configPath}'.\n" +
                "The user1, pass1, and char1 values are examples and cannot log in. " +
                "Replace them, then start Flipper again.");
            return false;
        }

        try
        {
            string configText = File.ReadAllText(configPath);
            _config = JsonConvert.DeserializeObject<Config>(configText);
        }
        catch (Exception ex)
        {
            StopForConfiguration($"Unable to read '{configPath}'.\n{ex}");
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

        if (_config.Plugins == null || _config.Plugins.Count != 1)
        {
            StopForConfiguration(
                $"'{configPath}' must contain exactly one plugin.");
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

        string configuredPlugin = _config.Plugins[0];

        _pluginPath = Path.GetFullPath(
            Path.IsPathRooted(configuredPlugin)
                ? configuredPlugin
                : Path.Combine(_baseDir, configuredPlugin));

        _pluginDir = Path.GetDirectoryName(_pluginPath);
        _toggleRequestPath =
            Path.Combine(_pluginDir, "cityflipper-toggle.request");

        DeleteIfExists(_toggleRequestPath);

        FlipperCacheStore.Initialize(
            _baseDir,
            _config.CacheFreshSeconds > 0 ? _config.CacheFreshSeconds : 60);

        return true;
    }

    private static string BuildDefaultConfig()
    {
        var config = new Config
        {
            Accounts = new List<AccountInfo>
            {
                new AccountInfo
                {
                    Username = "user1",
                    Password = "pass1",
                    Character = "char1"
                }
            },
            Plugins = new List<string> { "CityFlipper.dll" },
            ProbeTimeoutMs = 20000,
            DelayBetweenPassesMs = 5000,
            CacheFreshSeconds = 60
        };

        return JsonConvert.SerializeObject(config, Formatting.Indented);
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
        Console.WriteLine();
        Console.WriteLine("Press ENTER to exit.");
        Console.ReadLine();
    }

    private static void RunService()
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
        Console.WriteLine("Press ENTER to stop Flipper.");
        Console.WriteLine();

        Thread pipeThread = new Thread(RunPipeServer)
        {
            IsBackground = true,
            Name = "CityDwellers.Flipper.Pipe"
        };

        pipeThread.Start();

        Console.ReadLine();

        DeleteIfExists(_toggleRequestPath);
        Console.WriteLine("Flipper service stopped.");
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
            if (FlipperCacheStore.TryGetAny(out cached) &&
                IsEnabled(cached.CloakState))
            {
                return FromCache(
                    request,
                    cached,
                    "Flipper enabled the city cloak.");
            }

            return new WorkerResponse
            {
                Id = request.Id,
                Ok = true,
                Message = "Flipper sent the enable action; treating its own action as authoritative.",
                CloakState = "Enabled",
                ShieldTimerInSeconds = 0,
                ControllerCharge = run.Result.ControllerCharge,
                Character = _account.Character,
                Cached = false,
                ObservedUtc = DateTime.UtcNow,
                ActionSent = true
            };
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

    private static WorkerResponse EnsureDisabledReady(WorkerRequest request)
    {
        // Starting a raid must never trust cache. The keeper logs in, reads the
        // live controller, and the plugin lowers only when the cloak is enabled,
        // toggleable, and the controller is at least 75% charged.
        ProbeRun run = RunProbe("disable-ready");

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
            Math.Max(_timeoutMs, (watchSeconds + 15) * 1000));

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
               cache.ObservedUtc >= request.NotBeforeUtc.Value;
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
        Console.WriteLine("Press ENTER to exit.");
        Console.ReadLine();
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
        bool actionRequested = !string.IsNullOrWhiteSpace(requestedAction);
        bool ensureEnabled = string.Equals(
            requestedAction,
            "enable",
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
                : watchController
                    ? "RAID CT WATCH"
                    : actionRequested
                        ? "TOGGLE PROBE"
                        : "OBSERVE PROBE");
        Console.WriteLine("--------------------------------------");

        string resultPath =
            Path.Combine(_pluginDir, "cityflipper-result.json");
        string tempPath = resultPath + ".tmp";

        DeleteIfExists(resultPath);
        DeleteIfExists(tempPath);
        DeleteIfExists(_toggleRequestPath);

        if (actionRequested)
            File.WriteAllText(_toggleRequestPath, requestedAction);

        Logger logger = new LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.Debug()
            .CreateLogger();

        ClientDomain domain = null;
        Stopwatch totalTimer = Stopwatch.StartNew();

        ProbeRun run = new ProbeRun();

        try
        {
            Console.WriteLine(
                $"[{totalTimer.Elapsed.TotalSeconds:F3}s] Creating client domain.");

            domain = Client.CreateInstance(
                _account.Username,
                _account.Password,
                _account.Character,
                Dimension.RubiKa,
                logger);

            domain.LoadPlugin(_pluginPath);

            Console.WriteLine(
                $"[{totalTimer.Elapsed.TotalSeconds:F3}s] Starting AO client.");

            domain.Start();

            DateTime timeout = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (!File.Exists(resultPath))
            {
                if (DateTime.UtcNow >= timeout)
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
            run.Success = run.Result != null;

            if (run.Success)
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

            DeleteIfExists(resultPath);
            DeleteIfExists(tempPath);
            DeleteIfExists(_toggleRequestPath);
        }
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

    public class Config
    {
        public List<AccountInfo> Accounts;
        public List<string> Plugins;
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
