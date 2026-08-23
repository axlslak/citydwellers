using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;

using AOSharp.Clientless;
using AOSharp.Clientless.Common;

using Newtonsoft.Json;

using Serilog;
using Serilog.Core;

public class PluginLoader
{
    private const string PipeName = "citydwellers-buddies";

    private static readonly object ActiveLock = new object();
    private static readonly Dictionary<int, ActiveBuddy> ActiveBuddies =
        new Dictionary<int, ActiveBuddy>();

    private static Config _config;
    private static string _baseDir;

    static void Main(string[] args)
    {
        _baseDir = AppDomain.CurrentDomain.BaseDirectory;

        string configPath = Path.Combine(_baseDir, "buddies.json");

        try
        {
            string configText = File.ReadAllText(configPath);
            _config = JsonConvert.DeserializeObject<Config>(configText);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load '{configPath}'.");
            Console.WriteLine(ex);
            Environment.Exit(1);
            return;
        }

        if (!ValidateConfig(_config))
        {
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
        Console.WriteLine("Character scheme: Apcr{level}{index:00}");
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

        Console.ReadLine();

        Console.WriteLine();
        Console.WriteLine("Stopping Buddies service...");
        ShutdownAll();
        Console.WriteLine("Buddies service stopped.");
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

        Console.WriteLine(
            $"IPC request {request.Id ?? "<no-id>"}: " +
            $"{command} level={request.Level} index={request.Index}");

        switch (command)
        {
            case "wakeup":
                return Wakeup(request);

            case "sleep":
                return Sleep(request);

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
            ActiveBuddy existing;

            if (ActiveBuddies.TryGetValue(index, out existing))
            {
                if (existing.Level == level)
                {
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

                ActiveBuddies[index] = new ActiveBuddy
                {
                    Index = index,
                    Level = level,
                    Character = character,
                    Domain = domain
                };

                domain = null;

                Console.WriteLine(
                    $"Buddy domain started: {character} (index {index}).");

                return Ok(
                    request,
                    $"Started {character} on account index {index}.",
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

                Console.WriteLine(
                    $"Failed starting buddy {character}: {ex}");

                return Fail(
                    request,
                    $"Failed starting {character}: {ex.Message}");
            }
        }
    }

    private static WorkerResponse Sleep(WorkerRequest request)
    {
        if (!request.Index.HasValue)
            return Fail(request, "sleep requires an account index.");

        int index = request.Index.Value;

        lock (ActiveLock)
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
        return $"Apcr{level}{index:D2}";
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
    }

    private class WorkerRequest
    {
        public string Id;
        public string Command;
        public int? Level;
        public int? Index;
    }

    private class WorkerResponse
    {
        public string Id;
        public bool Ok;
        public string Message;
        public string Character;
        public int? Level;
        public int? Index;
    }
}
