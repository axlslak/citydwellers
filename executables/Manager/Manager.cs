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
using CityDwellers.Shared;

public class ManagerHost
{
    // Manager configuration is the Manager section of citydwellers.json beside
    // the executable. Bot-owned mutable files live under data.

    // Example Manager section:
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

        Config config;
        string configError;
        if (!SettingsPaths.TryReadSettingsSection(
                settingsDirectory,
                "Manager",
                out config,
                out configError))
        {
            StopForConfiguration(configError);
            return 1;
        }

        string validationError;
        if (!TryValidateConfig(config, out validationError))
        {
            StopForConfiguration(
                $"The Manager section in citydwellers.json is invalid.\n{validationError}");
            return 1;
        }

        string pluginPath = Path.Combine(settingsDirectory, "CityManager.dll");
        if (!File.Exists(pluginPath))
        {
            StopForConfiguration(
                $"Required Manager plugin was not found at '{pluginPath}'.");
            return 1;
        }

        try
        {
            foreach (AccountInfo acc in config.Accounts)
                CreateBot(acc, pluginPath);

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

        error = null;
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

    private static void WaitForStop(WaitHandle stopSignal)
    {
        if (stopSignal != null)
            stopSignal.WaitOne();
        else
            Console.ReadLine();
    }

    private static void CreateBot(AccountInfo accInfo, string pluginPath)
    {
        Logger logger =
            new LoggerConfiguration()
                .WriteTo.Console(
                    outputTemplate: LoggingDefaults.ConsoleOutputTemplate)
                .MinimumLevel.Debug()
                .CreateLogger();
        ClientDomain instance = Client.CreateInstance(accInfo.Username, accInfo.Password, accInfo.Character, Dimension.RubiKa, logger);

        instance.LoadPlugin(pluginPath);

        BotDomains.Add(instance);
        instance.Start();
    }

    public class Config
    {
        public List<AccountInfo> Accounts;
        public string Bot;
    }

    public class AccountInfo
    {
        public string Username;
        public string Password;
        public string Character;
    }
}
