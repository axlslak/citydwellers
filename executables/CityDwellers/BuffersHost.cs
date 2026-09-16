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
    public static bool IsEnabled()
    {
        try { return BufferSettings.Read().Enabled; }
        catch { return true; } // Report invalid optional config on its own component thread.
    }

    public static int Run(WaitHandle stop)
    {
        var domains = new List<ClientDomain>();
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
                    if (stop.WaitOne(0)) break;
                    logger.Information("Starting froob buffer {Character}.", account.Character);
                    var domain = Client.CreateInstance(account.Username, account.Password,
                        account.Character, Dimension.RubiKa, logger);
                    domains.Add(domain); // Also unload domains whose plugin/start fails.
                    domain.LoadPlugin(plugin);
                    domain.Start(); // Clientless owns this domain's update loop.
                }
                stop.WaitOne();
                return 0;
            }
            catch (Exception ex)
            {
                // Never dump credentials or deserialized account configuration.
                logger.Error("Buffers host stopped: {ErrorType}: {Message}", ex.GetType().Name, ex.Message);
                return 1;
            }
            finally
            {
                for (int i = domains.Count - 1; i >= 0; i--)
                    try { domains[i].Unload(); }
                    catch (Exception ex) { logger.Warning("Buffer unload failed: {Message}", ex.Message); }
            }
        }
    }
}
