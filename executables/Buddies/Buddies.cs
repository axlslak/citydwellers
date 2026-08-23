using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Serilog;
using Serilog.Core;
using AOSharp.Clientless;
using AOSharp.Clientless.Common;
using Newtonsoft.Json;

public class PluginLoader
{
    static void Main(string[] args)
    {
        string configPath =
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "buddies.json");

        Config config;

        try
        {
            string configFile =
                File.ReadAllText(configPath);

            config =
                JsonConvert.DeserializeObject<Config>(configFile);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Failed to load '{configPath}'");

            Console.WriteLine(ex);

            Environment.Exit(1);
            return;
        }

        Console.WriteLine(
            $"Found {config.Accounts.Count} buddy accounts.");

        Console.WriteLine();

        foreach (AccountInfo account in config.Accounts)
        {
            bool success =
                ScanAccount(account, config.Plugins);

            if (!success)
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"FATAL: Failed scanning account '{account.Username}'");

                Environment.Exit(1);
                return;
            }
        }

        Console.WriteLine();
        Console.WriteLine("==============================");
        Console.WriteLine("All buddy accounts scanned.");
        Console.WriteLine("No AO sessions should remain.");
        Console.WriteLine("==============================");
        Console.WriteLine();
        Console.WriteLine(
            "Buddies supervisor idle. Press ENTER to exit.");

        Console.ReadLine();
    }

    private static bool ScanAccount(
        AccountInfo account,
        List<string> pluginPaths)
    {
        if (pluginPaths == null ||
            pluginPaths.Count == 0)
        {
            Console.WriteLine("No plugins configured.");
            return false;
        }

        string pluginPath =
            Path.GetFullPath(pluginPaths[0]);

        string pluginDir =
            Path.GetDirectoryName(pluginPath);

        string resultPath =
            Path.Combine(
                pluginDir,
                "buddy-scan.result");

        string tempPath =
            resultPath + ".tmp";

        // Remove result from a previous scan.
        if (File.Exists(resultPath))
            File.Delete(resultPath);

        if (File.Exists(tempPath))
            File.Delete(tempPath);

        Console.WriteLine();
        Console.WriteLine(
            $"Scanning account: {account.Username}");

        Logger logger =
            new LoggerConfiguration()
                .WriteTo.Console()
                .MinimumLevel.Debug()
                .CreateLogger();

        ClientDomain domain = null;

        try
        {
            domain =
                Client.CreateInstance(
                    account.Username,
                    account.Password,
                    "",
                    Dimension.RubiKa,
                    logger);

            foreach (string path in pluginPaths)
            {
                domain.LoadPlugin(path);
            }

            domain.Start();

            //
            // Wait for CityBuddies.dll to write its result.
            //
            DateTime timeout =
                DateTime.UtcNow.AddSeconds(15);

            while (!File.Exists(resultPath))
            {
                if (DateTime.UtcNow >= timeout)
                {
                    Console.WriteLine(
                        "Timed out waiting for CharacterList.");

                    return false;
                }

                Thread.Sleep(100);
            }

            Console.WriteLine();
            Console.WriteLine(
                $"Character list for {account.Username}:");

            Console.WriteLine("------------------------------");

            string[] lines =
                File.ReadAllLines(resultPath);

            foreach (string line in lines)
            {
                Console.WriteLine(line);
            }

            Console.WriteLine("------------------------------");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Exception scanning {account.Username}:");

            Console.WriteLine(ex);

            return false;
        }
        finally
        {
            if (domain != null)
            {
                Console.WriteLine(
                    $"Unloading client for {account.Username}...");

                try
                {
                    domain.Unload();

                    Console.WriteLine(
                        $"Client unloaded for {account.Username}.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"Failed unloading client: {ex}");

                    // For our eventual production version,
                    // this is probably fatal.
                }
            }

            if (File.Exists(resultPath))
                File.Delete(resultPath);

            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public class Config
    {
        public List<AccountInfo> Accounts;
        public List<string> Plugins;
    }

    public class AccountInfo
    {
        public string Username;
        public string Password;
    }
}