using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

using AOSharp.Clientless;
using AOSharp.Clientless.Common;

using Newtonsoft.Json;

using Serilog;
using Serilog.Core;

public class FlipperLoader
{
    static void Main(string[] args)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string configPath = Path.Combine(baseDir, "flipper.json");

        Config config;

        try
        {
            string configText = File.ReadAllText(configPath);
            config = JsonConvert.DeserializeObject<Config>(configText);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unable to read '{configPath}'.");
            Console.WriteLine(ex);
            Environment.Exit(1);
            return;
        }

        if (config == null || config.Accounts == null || config.Accounts.Count != 1)
        {
            Console.WriteLine("flipper.json must contain exactly one account.");
            Environment.Exit(1);
            return;
        }

        if (config.Plugins == null || config.Plugins.Count != 1)
        {
            Console.WriteLine("flipper.json must contain exactly one plugin.");
            Environment.Exit(1);
            return;
        }

        bool toggleMode =
            args.Length == 1 &&
            string.Equals(args[0], "toggle", StringComparison.OrdinalIgnoreCase);

        if (args.Length > 0 && !toggleMode)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  Flipper.exe         # read-only two-pass observation");
            Console.WriteLine("  Flipper.exe toggle  # one guarded cloak toggle test");
            Environment.Exit(1);
            return;
        }

        AccountInfo account = config.Accounts[0];

        int timeoutMs = config.ProbeTimeoutMs > 0
            ? config.ProbeTimeoutMs
            : 20000;

        int delayMs = config.DelayBetweenPassesMs > 0
            ? config.DelayBetweenPassesMs
            : 5000;

        string pluginPath = Path.GetFullPath(config.Plugins[0]);
        string pluginDir = Path.GetDirectoryName(pluginPath);
        string toggleRequestPath = Path.Combine(
            pluginDir,
            "cityflipper-toggle.request");

        DeleteIfExists(toggleRequestPath);

        if (toggleMode)
            File.WriteAllText(toggleRequestPath, "toggle");

        try
        {
            Console.WriteLine("======================================");
            Console.WriteLine(" City Dwellers - Flipper Probe");
            Console.WriteLine("======================================");
            Console.WriteLine();
            Console.WriteLine($"Character: {account.Character}");
            Console.WriteLine($"Mode:      {(toggleMode ? "TOGGLE" : "OBSERVE")}");
            Console.WriteLine();

            ProbeRun pass1 = RunProbe(1, account, config.Plugins, timeoutMs);

            if (!pass1.Success)
            {
                Console.WriteLine();
                Console.WriteLine("PASS 1 FAILED.");
                Environment.Exit(1);
                return;
            }

            if (toggleMode)
            {
                Console.WriteLine();
                Console.WriteLine("Toggle mode performs one login/pass only.");
                Console.WriteLine("Press ENTER to exit.");
                Console.ReadLine();
                return;
            }

            Console.WriteLine();
            Console.WriteLine($"Waiting {delayMs} ms before second fresh login...");
            Console.WriteLine();

            Thread.Sleep(delayMs);

            ProbeRun pass2 = RunProbe(2, account, config.Plugins, timeoutMs);

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("======================================");
            Console.WriteLine(" COMPARISON");
            Console.WriteLine("======================================");

            PrintComparison(pass1, pass2);

            Console.WriteLine();
            Console.WriteLine("Press ENTER to exit.");
            Console.ReadLine();
        }
        finally
        {
            DeleteIfExists(toggleRequestPath);
        }
    }

    private static ProbeRun RunProbe(
        int pass,
        AccountInfo account,
        List<string> plugins,
        int timeoutMs)
    {
        Console.WriteLine();
        Console.WriteLine("--------------------------------------");
        Console.WriteLine($"PASS {pass}");
        Console.WriteLine("--------------------------------------");

        string pluginPath = Path.GetFullPath(plugins[0]);
        string pluginDir = Path.GetDirectoryName(pluginPath);
        string resultPath = Path.Combine(pluginDir, "cityflipper-result.json");
        string tempPath = resultPath + ".tmp";

        DeleteIfExists(resultPath);
        DeleteIfExists(tempPath);

        Logger logger = new LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.Debug()
            .CreateLogger();

        ClientDomain domain = null;
        Stopwatch totalTimer = Stopwatch.StartNew();

        ProbeRun run = new ProbeRun
        {
            Pass = pass
        };

        try
        {
            Console.WriteLine(
                $"[{totalTimer.Elapsed.TotalSeconds:F3}s] Creating client domain.");

            domain = Client.CreateInstance(
                account.Username,
                account.Password,
                account.Character,
                Dimension.RubiKa,
                logger);

            foreach (string plugin in plugins)
                domain.LoadPlugin(plugin);

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
            run.Success = true;

            Console.WriteLine();
            PrintResult(run);

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
        }
    }

    private static void PrintResult(ProbeRun run)
    {
        FlipperResult result = run.Result;

        Console.WriteLine($"PASS {run.Pass} RESULT");
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

                Console.WriteLine();
                Console.WriteLine("Post-toggle CloakInfo:");
                PrintDictionary(result.PostToggleCloakInfo);
            }
        }

        Console.WriteLine();
        Console.WriteLine("CityInfo:");
        PrintDictionary(result.CityInfo);
    }

    private static void PrintComparison(ProbeRun first, ProbeRun second)
    {
        if (!first.Success)
        {
            Console.WriteLine("Pass 1 failed.");
            return;
        }

        if (!second.Success)
        {
            Console.WriteLine("Pass 2 failed.");
            return;
        }

        Console.WriteLine(
            $"Total time:        {first.TotalMilliseconds:F0} ms  ->  " +
            $"{second.TotalMilliseconds:F0} ms");
        Console.WriteLine(
            $"To CharInPlay:     {first.Result.InitToInPlayMs:F0} ms  ->  " +
            $"{second.Result.InitToInPlayMs:F0} ms");
        Console.WriteLine(
            $"To Controller:     {first.Result.InitToControllerMs:F0} ms  ->  " +
            $"{second.Result.InitToControllerMs:F0} ms");
        Console.WriteLine(
            $"To CloakInfo:      {first.Result.InitToCloakInfoMs:F0} ms  ->  " +
            $"{second.Result.InitToCloakInfoMs:F0} ms");
        Console.WriteLine(
            $"To ChargeInfo:     {first.Result.InitToChargeInfoMs:F0} ms  ->  " +
            $"{second.Result.InitToChargeInfoMs:F0} ms");
        Console.WriteLine(
            $"Charge raw:        {first.Result.ControllerCharge}  ->  " +
            $"{second.Result.ControllerCharge}");

        Console.WriteLine();
        Console.WriteLine(
            "Compare the CityInfo/CloakInfo/charge values above.");
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
    }

    public class AccountInfo
    {
        public string Username;
        public string Password;
        public string Character;
    }

    public class ProbeRun
    {
        public int Pass;
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
}
