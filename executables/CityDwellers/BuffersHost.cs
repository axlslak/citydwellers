using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Common;
using CityDwellers.Shared;
using Serilog;
using Serilog.Core;

internal static class BuffersHost
{
    private const int BufferControlWaitMilliseconds = 250;

    private sealed class BufferRuntime
    {
        internal BufferAccount Account;
        internal ClientDomain Domain;
        internal bool Paid, Blocked;
        internal string AccountOwner;
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

                var paid = config.Enabled ? config.Paid.FirstOrDefault(a => a != null && a.Enabled) : null;
                ManagerMemory.Current.RegisterPaidBuffer(paid?.Character);
                if (paid != null)
                {
                    runtimes.Add(new BufferRuntime { Account = paid, Paid = true });
                    logger.Information("Paid Fixer {Character} is available on demand: lu / fsc; staying offline until requested.", paid.Character);
                }

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
                    if (runtime.Paid) continue;
                    StartBuffer(runtime, plugin, logger);
                }

                while (!stop.WaitOne(0))
                {
                    BufferControlOperation operation =
                        ManagerMemory.Current.WaitForBufferControl(BufferControlWaitMilliseconds);
                    if (operation != null) ProcessBufferControl(operation, byCharacter, plugin, logger);
                    foreach (var runtime in runtimes.Where(r => r.Paid))
                        ProcessPaidBuffer(runtime, plugin, logger);
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
                        if (runtime.Paid)
                        {
                            ManagerMemory.Current.ReleaseAoAccount(runtime.Account.Username, runtime.AccountOwner);
                            ManagerMemory.Current.StopPaidBufferSession(true, "Paid buffer service stopped.");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warning("Buffer unload failed for {Character}: {Message}",
                            runtime.Account.Character, ex.Message);
                        exitCode = 1;
                    }
                }
                ManagerMemory.Current.FailPaidBufferRequests("Paid buffer service stopped; check the host log.");
                var paidState = ManagerMemory.Current.ReadPaidBuffer();
                if (paidState != null && !paidState.Running) ManagerMemory.Current.RegisterPaidBuffer(null);
            }
        }
        return exitCode;
    }

    private static void StartBuffer(BufferRuntime runtime, string plugin, Logger logger)
    {
        if (runtime.Domain != null) return;

        ClientDomain domain = null;
        try
        {
            logger.Information("Starting {Kind} buffer {Character}.", runtime.Paid ? "on-demand paid" : "froob", runtime.Account.Character);
            domain = Client.CreateInstance(
                runtime.Account.Username,
                runtime.Account.Password,
                runtime.Account.Character,
                Dimension.RubiKa,
                logger);
            ClientDomainLifetime.Track(domain, runtime.Account.Character);
            domain.LoadPlugin(plugin);
            AoLoginAdmission admission =
                ManagerMemory.Current.WaitForAoLoginAdmission(
                    "Buffers", runtime.Account.Character);
            logger.Information(
                "Governor AO login admit {Character}: wave={Wave} slot={Slot}/{WaveSize} waited={Waited}ms.",
                runtime.Account.Character,
                admission.Wave,
                admission.PositionInWave,
                admission.WaveSize,
                admission.WaitedMilliseconds);
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
                {
                    logger.Warning("Buffer startup cleanup failed for {Character}: {Message}",
                        runtime.Account.Character, cleanupError);
                    runtime.Domain = domain;
                    runtime.Blocked = true;
                    throw;
                }
            }
            runtime.Domain = null;
            throw;
        }
    }

    private static void ProcessPaidBuffer(BufferRuntime runtime, string plugin, Logger logger)
    {
        var memory = ManagerMemory.Current;
        if (runtime.Blocked) return; // Failed unload: never retry login or release its account.
        var state = memory.ReadPaidBuffer();
        if (runtime.Domain == null)
        {
            if (!memory.HasPaidBufferRequests() || DateTime.UtcNow < state.RetryAfterUtc) return;
            string owner = "Buffers:" + Guid.NewGuid().ToString("N");
            if (!memory.TryAcquireAoAccount(runtime.Account.Username, owner, false)) return;
            runtime.AccountOwner = owner;
            memory.StartPaidBufferSession();
            try { StartBuffer(runtime, plugin, logger); }
            catch (Exception ex)
            {
                logger.Warning("Paid buffer {Character} startup failed: {Message}; no automatic retry for this request.",
                    runtime.Account.Character, ex.Message);
                if (runtime.Domain == null)
                {
                    memory.ReleaseAoAccount(runtime.Account.Username, owner);
                    memory.StopPaidBufferSession(true, "Paid buffer login failed; please wait a minute before retrying.");
                }
                else memory.BlockPaidBuffer("Paid buffer cleanup failed; administrator intervention required.");
            }
            return;
        }

        if (memory.AoAccountHasWaiter(runtime.Account.Username)) memory.DrainPaidBuffer();
        bool failed = !state.Parked &&
            ((!state.Ready && DateTime.UtcNow - state.StartedUtc > TimeSpan.FromSeconds(90)) ||
             DateTime.UtcNow - state.ObservedUtc > TimeSpan.FromSeconds(30));
        if (!state.Parked && !failed) return;
        string error;
        if (!ClientDomainLifetime.TryUnload(runtime.Domain, out error))
        {
            runtime.Blocked = true;
            memory.DrainPaidBuffer();
            memory.BlockPaidBuffer("Paid buffer could not unload; administrator intervention required.");
            logger.Warning("Paid buffer {Character} unload failed; account remains reserved: {Message}",
                runtime.Account.Character, error);
            return;
        }
        runtime.Domain = null;
        memory.MarkBufferBotOffline(runtime.Account.Character);
        memory.ReleaseAoAccount(runtime.Account.Username, runtime.AccountOwner);
        memory.StopPaidBufferSession(failed, "Paid buffer did not become ready or stopped responding; please retry later.");
        logger.Information("Paid buffer {Character} logged out; shared account released. Failed={Failed}.",
            runtime.Account.Character, failed);
    }

    private static void ProcessBufferControl(
        BufferControlOperation operation,
        Dictionary<string, BufferRuntime> byCharacter,
        string plugin,
        Logger logger)
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
