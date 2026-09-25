using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Common;
using CityDwellers.Shared;
using Serilog;

internal static class BuffersHost
{
    private const int BufferControlWaitMilliseconds = 250;

    private sealed class BufferRuntime
    {
        internal BufferAccount Account;
        internal ClientDomain Domain;
    }

    public static bool IsEnabled()
    {
        try { return BufferSettings.Read().Enabled; }
        catch { return true; } // Report invalid optional config on its own component thread.
    }

    public static int Run(WaitHandle stop)
    {
        var runtimes = new List<BufferRuntime>();
        var byCharacter = new Dictionary<string, BufferRuntime>(StringComparer.OrdinalIgnoreCase);
        int exitCode = 0;
        using (var logger = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: CityDwellers.Shared.LoggingDefaults.ConsoleOutputTemplate)
            .MinimumLevel.Debug().CreateLogger())
        {
            try
            {
                var config = BufferSettings.Read();
                string plugin = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CityBuffers.dll");
                if (!File.Exists(plugin)) throw new FileNotFoundException("Build CityBuffers with the solution.", plugin);

                foreach (var account in config.Active)
                {
                    var runtime = new BufferRuntime { Account = account };
                    runtimes.Add(runtime);
                    byCharacter.Add(account.Character, runtime);
                    ManagerMemory.Current.SetBufferSleeping(account.Character, false);
                }

                foreach (BufferRuntime runtime in runtimes)
                {
                    if (stop.WaitOne(0)) break;
                    StartBuffer(runtime, plugin, logger);
                }

                while (!stop.WaitOne(0))
                {
                    BufferControlOperation operation =
                        ManagerMemory.Current.WaitForBufferControl(BufferControlWaitMilliseconds);
                    if (operation == null) continue;
                    ProcessBufferControl(operation, byCharacter, plugin, logger);
                }
            }
            catch (Exception ex)
            {
                // Never dump credentials or deserialized account configuration.
                logger.Error("Buffers host stopped: {ErrorType}: {Message}", ex.GetType().Name, ex.Message);
                exitCode = 1;
            }
            finally
            {
                for (int i = runtimes.Count - 1; i >= 0; i--)
                {
                    BufferRuntime runtime = runtimes[i];
                    if (runtime.Domain == null) continue;
                    try
                    {
                        ClientDomainLifetime.Unload(runtime.Domain);
                        runtime.Domain = null;
                    }
                    catch (Exception ex)
                    {
                        logger.Warning("Buffer unload failed for {Character}: {Message}",
                            runtime.Account.Character, ex.Message);
                        exitCode = 1;
                    }
                }
            }
        }
        return exitCode;
    }

    private static void StartBuffer(BufferRuntime runtime, string plugin, ILogger logger)
    {
        if (runtime.Domain != null) return;

        ClientDomain domain = null;
        try
        {
            logger.Information("Starting froob buffer {Character}.", runtime.Account.Character);
            domain = Client.CreateInstance(
                runtime.Account.Username,
                runtime.Account.Password,
                runtime.Account.Character,
                Dimension.RubiKa,
                logger);
            ClientDomainLifetime.Track(domain, runtime.Account.Character);
            domain.LoadPlugin(plugin);
            domain.Start(); // Clientless owns this domain's update loop.
            runtime.Domain = domain;
            ManagerMemory.Current.SetBufferSleeping(runtime.Account.Character, false);
        }
        catch
        {
            if (domain != null)
            {
                string cleanupError;
                if (!ClientDomainLifetime.TryUnload(domain, out cleanupError))
                    logger.Warning("Buffer startup cleanup failed for {Character}: {Message}",
                        runtime.Account.Character, cleanupError);
            }
            runtime.Domain = null;
            throw;
        }
    }

    private static void ProcessBufferControl(
        BufferControlOperation operation,
        Dictionary<string, BufferRuntime> byCharacter,
        string plugin,
        ILogger logger)
    {
        bool success = false;
        string result;

        try
        {
            BufferRuntime runtime;
            if (!byCharacter.TryGetValue(operation.Character ?? string.Empty, out runtime))
            {
                result = "No enabled buffer named " + (operation.Character ?? "<missing>") + ".";
            }
            else if (string.Equals(operation.Action, "sleep", StringComparison.OrdinalIgnoreCase))
            {
                if (runtime.Domain == null)
                {
                    ManagerMemory.Current.SetBufferSleeping(runtime.Account.Character, true);
                    success = true;
                    result = runtime.Account.Character + " is already sleeping.";
                }
                else
                {
                    string error;
                    if (!ClientDomainLifetime.TryUnload(runtime.Domain, out error))
                    {
                        result = "Unable to sleep " + runtime.Account.Character + ": " + error;
                    }
                    else
                    {
                        runtime.Domain = null;
                        ManagerMemory.Current.SetBufferSleeping(runtime.Account.Character, true);
                        logger.Information("Buffer {Character} is sleeping for manual owner use.",
                            runtime.Account.Character);
                        success = true;
                        result = "Slept " + runtime.Account.Character + ".";
                    }
                }
            }
            else if (string.Equals(operation.Action, "wakeup", StringComparison.OrdinalIgnoreCase))
            {
                if (runtime.Domain != null)
                {
                    ManagerMemory.Current.SetBufferSleeping(runtime.Account.Character, false);
                    success = true;
                    result = runtime.Account.Character + " is already awake.";
                }
                else
                {
                    try
                    {
                        StartBuffer(runtime, plugin, logger);
                        success = true;
                        result = "Woke " + runtime.Account.Character + ".";
                    }
                    catch (Exception ex)
                    {
                        ManagerMemory.Current.SetBufferSleeping(runtime.Account.Character, true);
                        result = "Unable to wake " + runtime.Account.Character + ": " +
                                 ex.GetType().Name + ": " + ex.Message;
                    }
                }
            }
            else
            {
                result = "Unknown buffer control action '" + (operation.Action ?? "<missing>") + "'.";
            }
        }
        catch (Exception ex)
        {
            result = "Buffer control failed: " + ex.GetType().Name + ": " + ex.Message;
        }

        ManagerMemory.Current.PublishBufferControlResult(operation.Id, success, result);
    }
}
