using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Serilog.Core;
using AOSharp.Clientless;
using System.IO;
using AOSharp.Clientless.Common;
using Newtonsoft.Json;

public class PluginLoader
{
    //This plugin loader assumes you have a file 'config.json' in your debug folder, change account info and plugin paths to your liking

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
    //    "CityManager.dll",
    //  ]
    //}

    private static List<ClientDomain> BotDomains = new List<ClientDomain>();

    static void Main(string[] args)
    {
        string configFile;
        string configPath = AppDomain.CurrentDomain.BaseDirectory + "manager.json";

        try
        {
            configFile = File.ReadAllText(configPath);
        }
        catch
        {
            Console.WriteLine($"Config file not found at '{configPath}', read the instructions.");
            Console.ReadLine();
            return;
        }

        Config config = JsonConvert.DeserializeObject<Config>(configFile);

        foreach (AccountInfo acc in config.Accounts)
            CreateBot(acc, config.Plugins);

        Console.ReadLine();

        foreach (var domain in BotDomains)
            domain.Unload();
    }

    private static void CreateBot(AccountInfo accInfo, List<string> pluginPaths)
    {
        Logger logger = new LoggerConfiguration().WriteTo.Console().MinimumLevel.Debug().CreateLogger();
        ClientDomain instance = Client.CreateInstance(accInfo.Username, accInfo.Password, accInfo.Character, Dimension.RubiKa, logger);

        foreach (var path in pluginPaths)
            instance.LoadPlugin(path);

        instance.Start();
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
        public string Character;
    }
}
