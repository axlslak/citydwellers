using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Serilog.Core;
using AOSharp.Clientless;
using System.IO;
using AOSharp.Clientless.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CityDwellers.Shared;

public class ManagerHost
{
    // Manager configuration lives beside the executable in the portable
    // runtime root. Bot-owned mutable files live under data.

    // Example config:
    //{
    //  "Accounts": [
    //    {
    //      "Username": "TestUsername1",
    //      "Password": "Testpass1",
    //      "Character": "Testchar1"
    //    },
    //    {
    //    "Username": "TestUsername2",
    //      "Password": "Testpass2",
    //      "Character": "Testchar2"
    //    }
    //  ],
    //  "Plugins": [
    //    "CityManager.dll"
    //  ],
    //  "Bot": "Bobsan"
    //}

    private static List<ClientDomain> BotDomains = new List<ClientDomain>();
    private static bool _interactive;

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
        BotDomains = new List<ClientDomain>();

        string settingsDirectory;
        string settingsError;

        if (!SettingsPaths.TryEnsureDirectory(out settingsDirectory, out settingsError))
        {
            StopForConfiguration(settingsError);
            return 1;
        }

        string configPath = SettingsPaths.GetFilePath(settingsDirectory, "manager.json");

        if (!File.Exists(configPath))
        {
            string templateError;
            if (!SettingsPaths.TryCreateFile(
                    configPath,
                    BuildDefaultConfig(),
                    out templateError))
            {
                StopForConfiguration(templateError);
                return 1;
            }

            StopForConfiguration(
                $"Created a Manager configuration template at '{configPath}'.\n" +
                "The user1, pass1, and char1 values are examples and cannot log in. " +
                "Replace them, then start CityDwellers again.");
            return 1;
        }

        Config config;

        try
        {
            string configJson = File.ReadAllText(configPath);
            config = JsonConvert.DeserializeObject<Config>(configJson);
            TryAddMissingBotSetting(configPath, configJson);
        }
        catch (Exception ex)
        {
            StopForConfiguration(
                $"Unable to read Manager configuration '{configPath}'.\n{ex}");
            return 1;
        }

        string validationError;
        if (!TryValidateConfig(config, out validationError))
        {
            StopForConfiguration(
                $"Manager configuration '{configPath}' is invalid.\n{validationError}");
            return 1;
        }

        var pluginPaths = new List<string>();
        foreach (string configuredPath in config.Plugins)
        {
            string pluginPath = Path.GetFullPath(
                Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath));

            if (!File.Exists(pluginPath))
            {
                StopForConfiguration(
                    $"Manager plugin was not found at '{pluginPath}'.\n" +
                    "Use a filename such as 'CityManager.dll' for a plugin beside CityDwellers.exe.");
                return 1;
            }

            pluginPaths.Add(pluginPath);
        }

        try
        {
            foreach (AccountInfo acc in config.Accounts)
                CreateBot(acc, pluginPaths);

            WaitForStop(stopSignal);
            return 0;
        }
        finally
        {
            foreach (var domain in BotDomains)
            {
                try
                {
                    domain.Unload();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Unable to unload a Manager client domain: " + ex);
                }
            }

            BotDomains.Clear();
        }
    }

    private static bool TryValidateConfig(Config config, out string error)
    {
        if (config == null || config.Accounts == null || config.Accounts.Count == 0)
        {
            error = "At least one account is required.";
            return false;
        }

        if (config.Plugins == null || config.Plugins.Count == 0)
        {
            error = "At least one plugin is required.";
            return false;
        }

        foreach (AccountInfo account in config.Accounts)
        {
            if (account == null ||
                string.IsNullOrWhiteSpace(account.Username) ||
                string.IsNullOrWhiteSpace(account.Password) ||
                string.IsNullOrWhiteSpace(account.Character))
            {
                error = "Every account requires Username, Password, and Character.";
                return false;
            }

            if (IsDefaultAccount(account))
            {
                error =
                    "The user1/pass1/char1 defaults cannot log in. " +
                    "Replace them with the Manager account.";
                return false;
            }
        }

        foreach (string plugin in config.Plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin))
            {
                error = "Plugin paths cannot be empty.";
                return false;
            }
        }

        error = null;
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
            Plugins = new List<string> { "CityManager.dll" },
            Bot = null
        };

        return JsonConvert.SerializeObject(config, Formatting.Indented);
    }

    private static void TryAddMissingBotSetting(string configPath, string configJson)
    {
        try
        {
            JObject configuration = JObject.Parse(configJson);
            bool hasBotSetting = configuration.Properties().Any(property =>
                string.Equals(
                    property.Name,
                    "Bot",
                    StringComparison.OrdinalIgnoreCase));
            if (hasBotSetting)
                return;

            configuration["Bot"] = JValue.CreateNull();
            string tempPath = configPath + ".tmp";
            File.WriteAllText(tempPath, configuration.ToString(Formatting.Indented));
            File.Delete(configPath);
            File.Move(tempPath, configPath);
            Console.WriteLine(
                $"Added optional Bot setting to '{configPath}'. " +
                "Leave it null or set it to an alt-service character such as Bobsan.");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Warning: unable to add the optional Bot setting to '{configPath}': {ex.Message}");
        }
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

    private static void WaitForStop(WaitHandle stopSignal)
    {
        if (stopSignal != null)
            stopSignal.WaitOne();
        else
            Console.ReadLine();
    }

    private static void CreateBot(AccountInfo accInfo, List<string> pluginPaths)
    {
        Logger logger =
            new LoggerConfiguration()
                .WriteTo.Console(
                    outputTemplate: LoggingDefaults.ConsoleOutputTemplate)
                .MinimumLevel.Debug()
                .CreateLogger();
        ClientDomain instance = Client.CreateInstance(accInfo.Username, accInfo.Password, accInfo.Character, Dimension.RubiKa, logger);

        foreach (var path in pluginPaths)
            instance.LoadPlugin(path);

        BotDomains.Add(instance);
        instance.Start();
    }

    public class Config
    {
        public List<AccountInfo> Accounts;
        public List<string> Plugins;
        public string Bot;
    }

    public class AccountInfo
    {
        public string Username;
        public string Password;
        public string Character;
    }
}
