using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

using AOSharp.Clientless;
using AOSharp.Clientless.Common;

using CityBankers.Shared;
using Newtonsoft.Json;
using Serilog;
using Serilog.Core;

public class BankerLoader
{
    private const string PluginFileName = "CityBankers.dll";
    private const int DefaultDiagnosticTimeoutMs = 30000;
    private const int ReportAckTimeoutMs = 10000;
    private const int BagCapacity = 21;

    private static readonly string[] StorageRoles =
    {
        "artillery",
        "infantry",
        "control",
        "support",
        "extermination",
        "spirit",
        "dyna",
        "phatz"
    };

    private static BankerConfig _config;
    private static string _baseDir;
    private static string _settingsDir;
    private static bool _interactive;

    public static int RunAll(WaitHandle stopSignal, bool interactive)
    {
        _interactive = interactive;
        _baseDir = AppDomain.CurrentDomain.BaseDirectory;

        string settingsError;
        if (!SettingsPaths.TryEnsureDirectory(out _settingsDir, out settingsError))
        {
            Console.WriteLine(settingsError);
            return 1;
        }

        if (!LoadConfig())
            return 1;

        List<RoleMapping> configured = GetConfiguredRoles();
        if (configured.Count == 0)
        {
            Console.WriteLine(
                "No banker roles are fully configured in the Bankers section of citydwellers.json.");
            return 1;
        }

        string roleError;
        if (!ValidateCapacityReportRoles(configured, out roleError))
        {
            StopForConfiguration(roleError);
            return 1;
        }

        if (configured.Count > _config.MaxParallelLogins)
        {
            Console.WriteLine(
                $"Configured banker role count {configured.Count} exceeds " +
                $"MaxParallelLogins ({_config.MaxParallelLogins}).");
            return 1;
        }

        return RunRoles(configured, null, stopSignal, interactive);
    }

    static void Main(string[] args)
    {
        Environment.ExitCode = RunAll(null, true);
    }

    private static bool LoadConfig()
    {
        try
        {
            _config = SettingsPaths.ReadBankersSettings(_settingsDir)
                .ToObject<BankerConfig>();
        }
        catch (Exception ex)
        {
            StopForConfiguration(
                $"Unable to read the Bankers section from citydwellers.json.\n{ex}");
            return false;
        }

        if (_config == null)
        {
            StopForConfiguration(
                "The Bankers section in citydwellers.json is empty or invalid.");
            return false;
        }

        if (_config.Roles == null || _config.Roles.Count == 0)
        {
            StopForConfiguration(
                "The Bankers section in citydwellers.json requires a Roles map.");
            return false;
        }

        if (_config.MaxParallelLogins <= 0)
            _config.MaxParallelLogins = 32;

        foreach (KeyValuePair<string, AccountMapping> role in _config.Roles)
        {
            if (!ValidateSelectedAccount(role.Key, role.Value))
                return false;
        }

        try
        {
            foreach (SymbiantCatalog.AcceptanceRule rule in SymbiantCatalog.GetRules(_settingsDir))
            {
                AccountMapping destination;
                if (!TryGetRole(rule.Role, out destination) || !IsFullyConfigured(destination))
                {
                    StopForConfiguration(
                        "AcceptancePolicy AOID " + rule.AoId + " routes to missing or " +
                        "placeholder role '" + rule.Role + "'.");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            StopForConfiguration("Invalid Bankers.AcceptancePolicy: " + ex.Message);
            return false;
        }

        return true;
    }

    private static string BuildDefaultConfig()
    {
        var config = new BankerConfig
        {
            Password = "pass1",
            MaxParallelLogins = 32,
            DiagnosticTimeoutMs = DefaultDiagnosticTimeoutMs,
            AcceptancePolicy = new AcceptancePolicyConfig
            {
                SymbiantMaxCopies = 10,
                SpiritMaxCopies = 5,
                Items = new Dictionary<string, AcceptanceItemConfig>()
            },
            Roles = new Dictionary<string, AccountMapping>
            {
                { "central", NewPlaceholder("central") },
                { "artillery", NewPlaceholder("artillery") },
                { "infantry", NewPlaceholder("infantry") },
                { "control", NewPlaceholder("control") },
                { "support", NewPlaceholder("support") },
                { "extermination", NewPlaceholder("extermination") },
                { "spirit", NewAccount("kbspirit", "Kbspirit") },
                { "dyna", NewAccount("kbdyna", "Kbdyna") },
                { "phatz", NewAccount("kbphatz", "Kbphatz") }
            }
        };

        return JsonConvert.SerializeObject(config, Formatting.Indented);
    }

    private static AccountMapping NewPlaceholder(string role)
    {
        return new AccountMapping
        {
            Username = $"account-{role}",
            Character = $"character-{role}"
        };
    }

    private static AccountMapping NewAccount(string username, string character)
    {
        return new AccountMapping { Username = username, Character = character };
    }

    private static bool TryGetRole(string requestedRole, out AccountMapping account)
    {
        account = null;

        foreach (KeyValuePair<string, AccountMapping> item in _config.Roles)
        {
            if (!string.Equals(
                    item.Key,
                    requestedRole,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            account = item.Value;
            return account != null;
        }

        return false;
    }

    private static List<RoleMapping> GetConfiguredRoles()
    {
        return _config.Roles
            .Where(kvp => IsFullyConfigured(kvp.Value))
            .Select(kvp => new RoleMapping
            {
                Role = kvp.Key,
                Account = kvp.Value
            })
            .OrderBy(r => RoleOrder(r.Role))
            .ThenBy(r => r.Role, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int RoleOrder(string role)
    {
        if (string.Equals(role, "central", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (string.Equals(role, "artillery", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (string.Equals(role, "infantry", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (string.Equals(role, "control", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (string.Equals(role, "support", StringComparison.OrdinalIgnoreCase))
            return 4;
        if (string.Equals(role, "extermination", StringComparison.OrdinalIgnoreCase))
            return 5;
        if (string.Equals(role, "spirit", StringComparison.OrdinalIgnoreCase))
            return 6;
        if (string.Equals(role, "dyna", StringComparison.OrdinalIgnoreCase))
            return 7;
        if (string.Equals(role, "phatz", StringComparison.OrdinalIgnoreCase))
            return 8;

        return 100;
    }

    private static bool ValidateSelectedAccount(string role, AccountMapping account)
    {
        if (!IsFullyConfigured(account))
        {
            StopForConfiguration(
                $"Role '{role}' requires real local Username and Character values " +
                "in the Bankers section of citydwellers.json " +
                "(generic account-/character- placeholders are not usable).");
            return false;
        }

        string password = ResolvePassword(account);
        if (string.IsNullOrWhiteSpace(password) ||
            string.Equals(password, "pass1", StringComparison.Ordinal))
        {
            StopForConfiguration(
                $"Role '{role}' requires a real Password either on its role mapping " +
                "or as the shared Bankers.Password fallback.");
            return false;
        }

        return true;
    }

    private static string ResolvePassword(AccountMapping account)
    {
        return !string.IsNullOrWhiteSpace(account?.Password)
            ? account.Password
            : _config.Password;
    }

    private static bool ValidateCapacityReportRoles(
        List<RoleMapping> configured,
        out string error)
    {
        error = null;
        string[] requiredRoles =
        {
            "central",
            "artillery",
            "infantry",
            "control",
            "support",
            "extermination",
            "spirit",
            "dyna",
            "phatz"
        };

        List<string> missing = requiredRoles
            .Where(role => configured.All(
                r => !string.Equals(
                    r.Role,
                    role,
                    StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (missing.Count == 0)
            return true;

        error =
            "CityDwellers requires all nine banker roles to be fully " +
            "configured. Missing: " + string.Join(", ", missing) + ".";
        return false;
    }

    private static bool IsFullyConfigured(AccountMapping account)
    {
        return account != null &&
            !string.IsNullOrWhiteSpace(account.Username) &&
            !string.IsNullOrWhiteSpace(account.Character) &&
            !IsTemplatePlaceholder(account.Username, "account-") &&
            !IsTemplatePlaceholder(account.Character, "character-");
    }

    private static bool IsTemplatePlaceholder(string value, string prefix)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void ShowConfiguredRoles(string message)
    {
        Console.WriteLine(message);
        Console.WriteLine("Roles in the Bankers section of citydwellers.json:");

        foreach (KeyValuePair<string, AccountMapping> item in _config.Roles)
        {
            Console.WriteLine(
                $"  {item.Key} -> " +
                $"{(IsFullyConfigured(item.Value) ? item.Value.Character : "<not configured>")}");
        }
    }

    private static int RunRoles(
        List<RoleMapping> roles,
        string reportRecipient,
        WaitHandle stopSignal,
        bool interactive)
    {
        string pluginPath = Path.GetFullPath(
            Path.Combine(_baseDir, PluginFileName));

        if (!File.Exists(pluginPath))
        {
            Console.WriteLine(
                $"Required plugin '{pluginPath}' was not found. " +
                "Build the City Dwellers solution so CityDwellers.exe and " +
                "CityBankers.dll share the same runtime directory.");
            return 1;
        }

        string pluginDir = RuntimeStateStore.GetDataDirectory(_settingsDir);
        Logger logger = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: LoggingDefaults.ConsoleOutputTemplate)
            .MinimumLevel.Debug()
            .CreateLogger();

        var runtimes = new List<BankerRuntime>();
        Stopwatch timer = Stopwatch.StartNew();
        BankerRuntime central = null;
        int exitCode = 0;

        try
        {
            Console.WriteLine("======================================");
            Console.WriteLine(" CityBankers - Clientless Banker");
            Console.WriteLine("======================================");
            Console.WriteLine();
            Console.WriteLine(
                $"Mode:      {(roles.Count > 1 ? "all configured roles" : roles[0].Role)}");
            Console.WriteLine($"Clients:   {roles.Count}");
            Console.WriteLine($"Plugin:    {pluginPath}");
            Console.WriteLine($"State:     {pluginDir}");
            Console.WriteLine($"Parallel-login limit: {_config.MaxParallelLogins}");
            if (!string.IsNullOrWhiteSpace(reportRecipient))
                Console.WriteLine($"Capacity report recipient: {reportRecipient}");
            Console.WriteLine();

            foreach (RoleMapping role in roles)
            {
                string resultPath = Path.Combine(
                    pluginDir,
                    $"citybankers-diagnostic-{SafeFileToken(role.Account.Character)}.json");
                string tempPath = resultPath + ".tmp";

                DeleteIfExists(resultPath);
                DeleteIfExists(tempPath);

                Console.WriteLine(
                    $"[{timer.Elapsed.TotalSeconds:F3}s] Creating {role.Role} " +
                    $"({role.Account.Character}) client domain.");

                ClientDomain domain = Client.CreateInstance(
                    role.Account.Username,
                    ResolvePassword(role.Account),
                    role.Account.Character,
                    Dimension.RubiKa,
                    logger);

                domain.LoadPlugin(pluginPath);

                runtimes.Add(new BankerRuntime
                {
                    Role = role.Role,
                    Character = role.Account.Character,
                    Domain = domain,
                    ResultPath = resultPath,
                    TempPath = tempPath
                });
            }

            int timeoutMs = _config.DiagnosticTimeoutMs > 0
                ? _config.DiagnosticTimeoutMs
                : DefaultDiagnosticTimeoutMs;

            central = runtimes.FirstOrDefault(
                r => string.Equals(
                    r.Role,
                    "central",
                    StringComparison.OrdinalIgnoreCase));

            if (central != null && runtimes.Count > 1)
            {
                Console.WriteLine(
                    $"[{timer.Elapsed.TotalSeconds:F3}s] Starting central " +
                    $"({central.Character}) first.");
                central.Domain.Start();
                central.Started = true;

                if (!WaitForResultFile(central.ResultPath, timeoutMs))
                {
                    Console.WriteLine(
                        $"Central diagnostic did not arrive within {timeoutMs} ms; " +
                        "storage bankers will not be started.");
                    return 1;
                }

                Console.WriteLine(
                    $"[{timer.Elapsed.TotalSeconds:F3}s] Central is online and its " +
                    "bank diagnostic completed. Starting storage bankers.");

                foreach (BankerRuntime runtime in runtimes.Where(r => r != central))
                    StartRuntime(runtime, timer);
            }
            else
            {
                foreach (BankerRuntime runtime in runtimes)
                    StartRuntime(runtime, timer);
            }

            DateTime timeout = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < timeout &&
                   runtimes.Any(r => r.Started && !File.Exists(r.ResultPath)))
            {
                Thread.Sleep(50);
            }

            Console.WriteLine();
            Console.WriteLine("======================================");
            Console.WriteLine(" CityBankers diagnostic results");
            Console.WriteLine("======================================");

            foreach (BankerRuntime runtime in runtimes.Where(r => r.Started))
            {
                Console.WriteLine();
                Console.WriteLine($"[{runtime.Role}] {runtime.Character}");

                if (!File.Exists(runtime.ResultPath))
                {
                    Console.WriteLine(
                        $"  Diagnostic snapshot did not arrive within {timeoutMs} ms.");
                    continue;
                }

                runtime.Result = JsonConvert.DeserializeObject<DiagnosticResult>(
                    File.ReadAllText(runtime.ResultPath));
                PrintDiagnostic(runtime.Result, timer.Elapsed.TotalSeconds);
            }

            if (!string.IsNullOrWhiteSpace(reportRecipient))
            {
                Console.WriteLine();
                SendCapacityReport(
                    runtimes,
                    central,
                    pluginDir,
                    reportRecipient,
                    timeoutMs);
            }

            if (interactive)
            {
                Console.WriteLine();
                if (runtimes.Count(r => r.Started) == 1)
                {
                    BankerRuntime only = runtimes.First(r => r.Started);
                    Console.WriteLine(
                        $"{only.Character} remains online. " +
                        "Press ENTER to unload this banker.");
                }
                else
                {
                    Console.WriteLine(
                        $"All {runtimes.Count(r => r.Started)} started bankers remain online. " +
                        "Press ENTER to unload all of them.");
                }
            }

            if (stopSignal != null)
                stopSignal.WaitOne();
            else
                Console.ReadLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("BANKER CLIENT EXCEPTION:");
            Console.WriteLine(ex);
            exitCode = 1;
        }
        finally
        {
            for (int i = runtimes.Count - 1; i >= 0; i--)
            {
                BankerRuntime runtime = runtimes[i];

                if (runtime.Started)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Unloading {runtime.Role} ({runtime.Character})...");

                    try
                    {
                        runtime.Domain?.Unload();
                        Console.WriteLine($"{runtime.Character} unloaded.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"Client unload failed for {runtime.Character}: {ex}");
                        exitCode = 1;
                    }
                }

                DeleteIfExists(runtime.ResultPath);
                DeleteIfExists(runtime.TempPath);
            }

            if (central != null)
            {
                DeleteIfExists(GetReportCommandPath(pluginDir, central.Character));
                DeleteIfExists(GetReportAckPath(pluginDir, central.Character));
                DeleteIfExists(GetReportCommandPath(pluginDir, central.Character) + ".tmp");
                DeleteIfExists(GetReportAckPath(pluginDir, central.Character) + ".tmp");
            }

            logger.Dispose();
        }

        return exitCode;
    }

    private static void StartRuntime(BankerRuntime runtime, Stopwatch timer)
    {
        Console.WriteLine(
            $"[{timer.Elapsed.TotalSeconds:F3}s] Starting {runtime.Role} " +
            $"({runtime.Character}).");
        runtime.Domain.Start();
        runtime.Started = true;
    }

    private static bool WaitForResultFile(string path, int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
                return true;

            Thread.Sleep(50);
        }

        return File.Exists(path);
    }

    private static void SendCapacityReport(
        List<BankerRuntime> runtimes,
        BankerRuntime central,
        string pluginDir,
        string recipient,
        int timeoutMs)
    {
        if (central == null || !central.Started)
        {
            Console.WriteLine(
                "Capacity report could not be sent because Central is not running.");
            Environment.ExitCode = 1;
            return;
        }

        List<string> messages = BuildCapacityReportMessages(runtimes);
        string commandPath = GetReportCommandPath(pluginDir, central.Character);
        string commandTempPath = commandPath + ".tmp";
        string ackPath = GetReportAckPath(pluginDir, central.Character);

        DeleteIfExists(commandPath);
        DeleteIfExists(commandTempPath);
        DeleteIfExists(ackPath);
        DeleteIfExists(ackPath + ".tmp");

        var command = new ReportCommand
        {
            Recipient = recipient,
            Messages = messages
        };

        File.WriteAllText(
            commandTempPath,
            JsonConvert.SerializeObject(command, Formatting.Indented));
        File.Move(commandTempPath, commandPath);

        Console.WriteLine(
            $"Queued {messages.Count} capacity-report tell line(s) for Central -> {recipient}.");

        int ackTimeoutMs = Math.Min(
            Math.Max(timeoutMs, 1000),
            ReportAckTimeoutMs);

        if (!WaitForResultFile(ackPath, ackTimeoutMs))
        {
            Console.WriteLine(
                "Central did not acknowledge the report command before timeout.");
            Environment.ExitCode = 1;
            return;
        }

        ReportAck ack = JsonConvert.DeserializeObject<ReportAck>(
            File.ReadAllText(ackPath));

        if (ack == null)
        {
            Console.WriteLine("Central report acknowledgement could not be parsed.");
            Environment.ExitCode = 1;
            return;
        }

        if (!string.IsNullOrWhiteSpace(ack.Error))
        {
            Console.WriteLine($"Central report send failed: {ack.Error}");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine(
            $"Central queued {ack.MessageCount} tell line(s) to {ack.Recipient}.");
    }

    private static List<string> BuildCapacityReportMessages(
        List<BankerRuntime> runtimes)
    {
        var messages = new List<string>
        {
            "CityBankers capacity report: configured copies per accepted AOID; " +
            "bags counted only in bank + normal inventory (no worn/social bags)."
        };

        int passed = 0;

        foreach (string role in StorageRoles)
        {
            BankerRuntime runtime = runtimes.FirstOrDefault(
                r => string.Equals(
                    r.Role,
                    role,
                    StringComparison.OrdinalIgnoreCase));

            List<SymbiantCatalog.AcceptanceRule> rules = SymbiantCatalog
                .GetRules(_settingsDir)
                .Where(rule => string.Equals(rule.Role, role, StringComparison.OrdinalIgnoreCase))
                .ToList();
            bool unbounded = rules.Any(rule => rule.MaxCopies == SymbiantCatalog.KeepAllCopies);
            int requiredSlots = rules.Where(rule => rule.MaxCopies > 0)
                .Sum(rule => rule.MaxCopies);
            int requiredBags = (requiredSlots + BagCapacity - 1) / BagCapacity;

            if (runtime == null || !runtime.Started || runtime.Result == null)
            {
                messages.Add(
                    $"{role}: NO RESULT; policy needs {(unbounded ? "unbounded" : requiredSlots.ToString())} item slots.");
                continue;
            }

            DiagnosticResult result = runtime.Result;
            if (!result.BankOpened || result.TotalBagCount < 0)
            {
                messages.Add(
                    $"{role}: bank not verified; inventory bags={result.InventoryBagCount}; " +
                        $"policy={(unbounded ? "unbounded" : requiredSlots.ToString())} slots.");
                continue;
            }

            int actualBags = result.TotalBagCount;
            int capacity = actualBags * BagCapacity;
            int bagDelta = actualBags - requiredBags;
            bool enough = !unbounded && bagDelta >= 0;

            if (enough)
                passed++;

            messages.Add(
                $"{role}: bags={actualBags} (bank {result.BankBagCount} + inv " +
                $"{result.InventoryBagCount}), need={requiredBags}; capacity={capacity}/" +
                $"{(unbounded ? "unbounded" : requiredSlots.ToString())} -> {(unbounded ? "MONITOR" : enough ? "OK" : "SHORT")} " +
                $"({(bagDelta >= 0 ? "+" : string.Empty)}{bagDelta} bags).");
        }

        messages.Add(
            $"Overall: {(passed == StorageRoles.Length ? "PASS" : "CHECK")} " +
            $"{passed}/{StorageRoles.Length} storage bankers have enough bags.");

        return messages;
    }

    private static string GetReportCommandPath(string pluginDir, string character)
    {
        return Path.Combine(
            pluginDir,
            $"citybankers-report-command-{SafeFileToken(character)}.json");
    }

    private static string GetReportAckPath(string pluginDir, string character)
    {
        return Path.Combine(
            pluginDir,
            $"citybankers-report-ack-{SafeFileToken(character)}.json");
    }

    private static void PrintDiagnostic(DiagnosticResult result, double hostSeconds)
    {
        if (result == null)
        {
            Console.WriteLine("  Diagnostic file existed but could not be parsed.");
            return;
        }

        Console.WriteLine($"  Host elapsed:       {hostSeconds:F3}s");
        Console.WriteLine($"  Playfield model:    {result.PlayfieldModelId}");
        Console.WriteLine(
            $"  Position:           ({result.X:F3}, {result.Y:F3}, {result.Z:F3})");
        Console.WriteLine($"  Static dynels:      {result.StaticDynelCount}");
        Console.WriteLine($"  Live players seen:  {result.LivePlayerCount}");

        if (result.LivePlayers != null)
        {
            foreach (PlayerSnapshot player in result.LivePlayers)
            {
                Console.WriteLine(
                    $"    PLAYER {player.Name} | {player.Identity} | " +
                    $"distance={player.Distance:F2}m");
            }
        }

        Console.WriteLine(
            $"  Bank attempted:     {result.BankAttempted}");
        if (result.BankAttempted)
        {
            Console.WriteLine(
                $"  Bank target:        {result.BankTargetName} | " +
                $"{result.BankTargetIdentity} | " +
                $"template={FormatNullable(result.BankTargetTemplateId)}");
        }

        Console.WriteLine($"  Bank opened:        {result.BankOpened}");
        if (result.BankOpened)
        {
            Console.WriteLine($"  Bank items:         {result.BankItemCount}");
            Console.WriteLine($"  Bank free slots:    {result.BankFreeSlots}");
            Console.WriteLine($"  Bank bags:          {result.BankBagCount}");
        }

        Console.WriteLine($"  Inventory items:    {result.InventoryItemCount}");
        Console.WriteLine($"  Inventory free:     {result.InventoryFreeSlots}");
        Console.WriteLine($"  Inventory bags:     {result.InventoryBagCount}");
        Console.WriteLine($"  Total bank+inv bags:{result.TotalBagCount}");

        if (!string.IsNullOrWhiteSpace(result.BankError))
            Console.WriteLine($"  Bank note/error:    {result.BankError}");
    }

    private static string FormatNullable(int? value)
    {
        return value.HasValue ? value.Value.ToString() : "n/a";
    }

    private static string SafeFileToken(string value)
    {
        string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
        foreach (char invalid in Path.GetInvalidFileNameChars())
            token = token.Replace(invalid, '_');

        return token;
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

    public class BankerConfig
    {
        public string Password;
        public int MaxParallelLogins = 32;
        public int DiagnosticTimeoutMs = DefaultDiagnosticTimeoutMs;
        public AcceptancePolicyConfig AcceptancePolicy;
        public Dictionary<string, AccountMapping> Roles;
    }

    public class AcceptancePolicyConfig
    {
        public int SymbiantMaxCopies = 10;
        public int SpiritMaxCopies = 5;
        public Dictionary<string, AcceptanceItemConfig> Items;
    }

    public class AcceptanceItemConfig
    {
        public string Role;
        public int MaxCopies;
    }

    public class AccountMapping
    {
        public string Username;
        public string Password;
        public string Character;
    }

    private class RoleMapping
    {
        public string Role;
        public AccountMapping Account;
    }

    private class BankerRuntime
    {
        public string Role;
        public string Character;
        public ClientDomain Domain;
        public string ResultPath;
        public string TempPath;
        public bool Started;
        public DiagnosticResult Result;
    }

    public class DiagnosticResult
    {
        public DateTime ObservedUtc;
        public string Character;
        public int PlayfieldModelId;
        public float X;
        public float Y;
        public float Z;
        public int DynelCount;
        public int StaticDynelCount;
        public int LivePlayerCount;
        public List<PlayerSnapshot> LivePlayers;
        public List<DynelSnapshot> BankCandidates;
        public List<DynelSnapshot> Dynels;
        public bool BankAttempted;
        public bool BankOpened;
        public string BankTargetName;
        public string BankTargetIdentity;
        public int? BankTargetTemplateId;
        public int BankItemCount;
        public int BankFreeSlots;
        public int InventoryItemCount;
        public int InventoryFreeSlots;
        public int InventoryBagCount;
        public int BankBagCount;
        public int TotalBagCount;
        public string BankError;
    }

    public class PlayerSnapshot
    {
        public string Name;
        public string Identity;
        public float Distance;
    }

    public class DynelSnapshot
    {
        public string Name;
        public string Identity;
        public float Distance;
        public string Source;
        public int? TemplateId;
    }

    public class ReportCommand
    {
        public string Recipient;
        public List<string> Messages;
    }

    public class ReportAck
    {
        public DateTime SentUtc;
        public string Recipient;
        public int MessageCount;
        public string Error;
    }
}