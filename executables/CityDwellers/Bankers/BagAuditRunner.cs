using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using AOSharp.Clientless;
using AOSharp.Clientless.Common;
using CityBankers.Shared;
using Newtonsoft.Json;
using Serilog;
using Serilog.Core;

internal static class BagAuditRunner
{
    private const string PluginFileName = "CityBankers.dll";
    private const int DefaultDiagnosticTimeoutMs = 30000;
    private const int BagOpenTimeoutMs = 3000;
    private const int BagMoveTimeoutMs = 3000;
    private const int MinimumAuditTimeoutMs = 120000;

    private static readonly string[] RequiredRoles =
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

    public static void Run()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string settingsDir;
        string settingsError;
        if (!SettingsPaths.TryEnsureDirectory(out settingsDir, out settingsError))
        {
            StopForError(settingsError);
            return;
        }

        AuditConfig config;
        try
        {
            config = SettingsPaths.ReadBankersSettings(settingsDir)
                .ToObject<AuditConfig>();
        }
        catch (Exception ex)
        {
            StopForError(
                $"Unable to read the Bankers section from citydwellers.json.\n{ex}");
            return;
        }

        string validationError;
        List<AuditRole> roles;
        if (!TryBuildRoles(config, out roles, out validationError))
        {
            StopForError(validationError);
            return;
        }

        string pluginPath = Path.GetFullPath(Path.Combine(baseDir, PluginFileName));
        if (!File.Exists(pluginPath))
        {
            StopForError(
                $"Required plugin '{pluginPath}' was not found. Build the City Dwellers " +
                "solution so CityDwellers.exe and CityBankers.dll share the same runtime directory.");
            return;
        }

        // All mutable banker state and coordination artifacts share the unified
        // City Dwellers data directory.
        baseDir = RuntimeStateStore.GetDataDirectory(settingsDir);

        Logger logger = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: LoggingDefaults.ConsoleOutputTemplate)
            .MinimumLevel.Debug()
            .CreateLogger();

        // The audit is observational. Capture the expected state before any client is
        // started so the report can explain every live difference without replacing the
        // baseline that it is comparing against.
        StorageState expectedStorage = RuntimeStateStore.LoadStorageState(settingsDir);

        string runId =
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        var runtimes = new List<AuditRuntime>();
        try
        {
            Console.WriteLine("======================================");
            Console.WriteLine(" CityBankers - Parallel Bag Audit");
            Console.WriteLine("======================================");
            Console.WriteLine();
            Console.WriteLine($"Run:       {runId}");
            Console.WriteLine("Mode:      non-destructive staged bag identity/content discovery");
            Console.WriteLine($"Workers:   {roles.Count - 1} storage bankers concurrently after Central");
            Console.WriteLine("Bank bags: stage one at a time through normal inventory, open/read, return to bank");
            Console.WriteLine($"Plugin:    {pluginPath}");
            Console.WriteLine($"State:     {baseDir}");
            Console.WriteLine($"Output:    {Path.Combine(baseDir, "diagnostic-dumps")}");
            Console.WriteLine();

            foreach (AuditRole role in roles)
            {
                AuditRuntime runtime = CreateRuntime(
                    role,
                    ResolvePassword(config, role.Account),
                    pluginPath,
                    baseDir,
                    logger);
                runtimes.Add(runtime);
            }

            int diagnosticTimeoutMs = config.DiagnosticTimeoutMs > 0
                ? config.DiagnosticTimeoutMs
                : DefaultDiagnosticTimeoutMs;

            AuditRuntime central = runtimes.First(r =>
                string.Equals(r.Role, "central", StringComparison.OrdinalIgnoreCase));

            Console.WriteLine(
                $"Starting Central first: {central.Character}. Storage workers remain stopped.");
            central.Domain.Start();
            central.Started = true;

            if (!WaitForFile(central.DiagnosticPath, diagnosticTimeoutMs))
            {
                Console.WriteLine(
                    $"Central did not publish its bank diagnostic within {diagnosticTimeoutMs} ms. " +
                    "Bag audit aborted before worker startup.");
                Environment.ExitCode = 1;
                return;
            }

            List<AuditRuntime> workers = runtimes
                .Where(r => !string.Equals(
                    r.Role,
                    "central",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            Console.WriteLine();
            Console.WriteLine(
                $"Central is ready. Starting all {workers.Count} storage workers now; their AO " +
                "connections and audits overlap concurrently.");

            foreach (AuditRuntime worker in workers)
            {
                worker.Domain.Start();
                worker.Started = true;
                Console.WriteLine($"  START {worker.Role,-13} {worker.Character}");
            }

            DateTime diagnosticsDeadline = DateTime.UtcNow.AddMilliseconds(
                diagnosticTimeoutMs);
            while (DateTime.UtcNow < diagnosticsDeadline &&
                   workers.Any(w => !File.Exists(w.DiagnosticPath)))
            {
                Thread.Sleep(50);
            }

            Console.WriteLine();
            Console.WriteLine("Worker bank-diagnostic readiness:");
            foreach (AuditRuntime worker in workers)
            {
                bool ready = File.Exists(worker.DiagnosticPath);
                Console.WriteLine(
                    $"  {(ready ? "READY" : "NO RESULT"),-9} {worker.Role,-13} {worker.Character}");
            }

            if (workers.Any(w => !File.Exists(w.DiagnosticPath)))
            {
                Console.WriteLine(
                    "At least one worker never completed the prerequisite bank diagnostic; " +
                    "no bag-audit commands will be issued.");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine();
            // Central also has a bank and may own bags; it is not just a login anchor.
            workers.Insert(0, central);
            Console.WriteLine(
                "Issuing one census command to every banker, including Central. " +
                "Each banker reads its bags serially and reports loose inventory/bank items.");

            foreach (AuditRuntime worker in workers)
            {
                WriteAuditCommand(worker, runId);
                Console.WriteLine($"  AUDIT {worker.Role,-13} {worker.Character}");
            }

            // Worst-case ceiling allows a move-out timeout, open timeout, and move-back
            // timeout per possible bag. Normal successful runs should be much faster.
            int expectedMaxBags = 132;
            int perBagWorstCaseMs =
                BagOpenTimeoutMs + (2 * BagMoveTimeoutMs);
            int auditTimeoutMs = Math.Max(
                MinimumAuditTimeoutMs,
                expectedMaxBags * perBagWorstCaseMs + 30000);
            DateTime auditDeadline = DateTime.UtcNow.AddMilliseconds(auditTimeoutMs);

            while (DateTime.UtcNow < auditDeadline &&
                   workers.Any(w => !File.Exists(w.AuditResultPath)))
            {
                Thread.Sleep(100);
            }

            foreach (AuditRuntime worker in workers)
            {
                if (!File.Exists(worker.AuditResultPath))
                    continue;

                try
                {
                    worker.Audit = JsonConvert.DeserializeObject<BagAuditResult>(
                        File.ReadAllText(worker.AuditResultPath));
                }
                catch (Exception ex)
                {
                    worker.ReadError = ex.ToString();
                }
            }

            AuditDifferenceSummary differences;
            string dumpPath = WriteCombinedDump(
                baseDir,
                runId,
                workers,
                expectedStorage,
                out differences);

            Console.WriteLine();
            Console.WriteLine("======================================");
            Console.WriteLine(" Bag audit summary");
            Console.WriteLine("======================================");

            foreach (AuditRuntime worker in workers)
            {
                if (worker.Audit == null)
                {
                    Console.WriteLine(
                        $"  {worker.Role,-13} NO RESULT  {worker.Character}" +
                        (string.IsNullOrWhiteSpace(worker.ReadError)
                            ? string.Empty
                            : " (result parse failed)"));
                    continue;
                }

                BagAuditResult result = worker.Audit;
                Console.WriteLine(
                    $"  {worker.Role,-13} bags={result.TotalBagCount,3} " +
                    $"opened={result.OpenedCount,3} failed={result.FailedCount,3} " +
                    $"empty={result.EmptyCount,3} nonempty={result.NonEmptyCount,3} " +
                    $"returned={result.BankReturnedCount,3}/{result.BankBagCount,3} " +
                    $"uniqueIds={DistinctUniqueIdentities(result),3}");
            }

            int complete = workers.Count(w => w.Audit != null);
            int failures = workers
                .Where(w => w.Audit != null)
                .Sum(w => w.Audit.FailedCount);
            int returnFailures = workers
                .Where(w => w.Audit != null)
                .Sum(w => w.Audit.BankReturnFailureCount);
            int nonempty = workers
                .Where(w => w.Audit != null)
                .Sum(w => w.Audit.NonEmptyCount);
            int fatalResults = workers.Count(w =>
                w.Audit != null && !string.IsNullOrWhiteSpace(w.Audit.FatalError));
            int incompleteLooseCensuses = workers.Count(w =>
                w.Audit == null || w.Audit.LooseInventoryItems == null ||
                w.Audit.LooseBankItems == null);

            Console.WriteLine();
            Console.WriteLine(
                $"Result: {complete}/{workers.Count} worker results; audit failures={failures}; " +
                $"bank-return failures={returnFailures}; fatalResults={fatalResults}; " +
                $"incompleteLooseCensuses={incompleteLooseCensuses}; " +
                $"non-empty bags={nonempty}.");
            Console.WriteLine($"Diagnostic dump created: {dumpPath}");
            Console.WriteLine(
                $"Expected-state differences: incompleteWorkers={differences.IncompleteWorkers}; " +
                $"missingBags={differences.MissingBags}; " +
                $"unexpectedBags={differences.UnexpectedBags}; " +
                $"unverifiedBags={differences.UnverifiedBags}; " +
                $"unverifiedItems={differences.UnverifiedItems}; " +
                $"missingItems={differences.MissingItems}; " +
                $"unexpectedItems={differences.UnexpectedItems}; " +
                $"metadataChanges={differences.MetadataChanges}; " +
                $"templateCountChanges={differences.TemplateCountChanges}.");
            foreach (string detail in differences.Details)
                Console.WriteLine("  DIFF " + detail);
            Console.WriteLine(
                "Run CityDwellers.exe bankers-bagaudit again after a clean relog, without manually " +
                "rearranging bags, to create the identity-stability comparison dump.");

            if (complete != workers.Count ||
                failures != 0 ||
                returnFailures != 0 ||
                fatalResults != 0 ||
                incompleteLooseCensuses != 0)
            {
                Environment.ExitCode = 1;
            }

            Console.WriteLine();
            Console.WriteLine(
                $"All {runtimes.Count} started bankers remain online. Press ENTER to unload them cleanly.");
            Console.ReadLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("BAG AUDIT EXCEPTION:");
            Console.WriteLine(ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            for (int i = runtimes.Count - 1; i >= 0; i--)
            {
                AuditRuntime runtime = runtimes[i];
                if (runtime.Started)
                {
                    Console.WriteLine($"Unloading {runtime.Role} ({runtime.Character})...");
                    try
                    {
                        runtime.Domain?.Unload();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"Client unload failed for {runtime.Character}: {ex}");
                        Environment.ExitCode = 1;
                    }
                }

                DeleteIfExists(runtime.DiagnosticPath);
                DeleteIfExists(runtime.DiagnosticPath + ".tmp");
                DeleteIfExists(runtime.AuditCommandPath);
                DeleteIfExists(runtime.AuditCommandPath + ".tmp");
                DeleteIfExists(runtime.AuditResultPath);
                DeleteIfExists(runtime.AuditResultPath + ".tmp");
            }

            logger.Dispose();
        }
    }

    private static bool TryBuildRoles(
        AuditConfig config,
        out List<AuditRole> roles,
        out string error)
    {
        roles = new List<AuditRole>();
        error = null;

        if (config == null)
        {
            error = "The Bankers section in citydwellers.json is empty or invalid.";
            return false;
        }

        if (config.Roles == null)
        {
            error = "The Bankers section requires a Roles map.";
            return false;
        }

        foreach (string roleName in RequiredRoles)
        {
            KeyValuePair<string, AuditAccount> match = config.Roles.FirstOrDefault(kvp =>
                string.Equals(kvp.Key, roleName, StringComparison.OrdinalIgnoreCase));

            AuditAccount account = match.Value;
            if (!IsFullyConfigured(account))
            {
                error =
                    $"CityDwellers.exe bankers-bagaudit requires all nine roles. Role '{roleName}' " +
                    "is missing or still uses a placeholder mapping.";
                return false;
            }

            string password = ResolvePassword(config, account);
            if (string.IsNullOrWhiteSpace(password) ||
                string.Equals(password, "pass1", StringComparison.Ordinal))
            {
                error =
                    $"Role '{roleName}' requires a real Password either on its role mapping " +
                    "or as the shared Bankers.Password fallback.";
                return false;
            }

            roles.Add(new AuditRole
            {
                Role = roleName,
                Account = account
            });
        }

        int maxParallel = config.MaxParallelLogins > 0
            ? config.MaxParallelLogins
            : 32;
        if (roles.Count > maxParallel)
        {
            error =
                $"Bag audit requires {roles.Count} configured clients but " +
                $"MaxParallelLogins is {maxParallel}.";
            return false;
        }

        return true;
    }

    private static string ResolvePassword(AuditConfig config, AuditAccount account)
    {
        return !string.IsNullOrWhiteSpace(account?.Password)
            ? account.Password
            : config.Password;
    }

    private static bool IsFullyConfigured(AuditAccount account)
    {
        return account != null &&
            !string.IsNullOrWhiteSpace(account.Username) &&
            !string.IsNullOrWhiteSpace(account.Character) &&
            !account.Username.StartsWith("account-", StringComparison.OrdinalIgnoreCase) &&
            !account.Character.StartsWith("character-", StringComparison.OrdinalIgnoreCase);
    }

    private static AuditRuntime CreateRuntime(
        AuditRole role,
        string password,
        string pluginPath,
        string baseDir,
        Logger logger)
    {
        string token = SafeFileToken(role.Account.Character);
        string diagnosticPath = Path.Combine(
            baseDir,
            $"citybankers-diagnostic-{token}.json");
        string commandPath = Path.Combine(
            baseDir,
            $"citybankers-bagaudit-command-{token}.json");
        string resultPath = Path.Combine(
            baseDir,
            $"citybankers-bagaudit-result-{token}.json");

        DeleteIfExists(diagnosticPath);
        DeleteIfExists(diagnosticPath + ".tmp");
        DeleteIfExists(commandPath);
        DeleteIfExists(commandPath + ".tmp");
        DeleteIfExists(resultPath);
        DeleteIfExists(resultPath + ".tmp");

        ClientDomain domain = Client.CreateInstance(
            role.Account.Username,
            password,
            role.Account.Character,
            Dimension.RubiKa,
            logger);
        domain.LoadPlugin(pluginPath);

        return new AuditRuntime
        {
            Role = role.Role,
            Character = role.Account.Character,
            Domain = domain,
            DiagnosticPath = diagnosticPath,
            AuditCommandPath = commandPath,
            AuditResultPath = resultPath
        };
    }

    private static void WriteAuditCommand(AuditRuntime runtime, string runId)
    {
        var command = new BagAuditCommand
        {
            RunId = runId,
            Role = runtime.Role,
            BagOpenTimeoutMs = BagOpenTimeoutMs,
            BagMoveTimeoutMs = BagMoveTimeoutMs
        };

        string temp = runtime.AuditCommandPath + ".tmp";
        File.WriteAllText(
            temp,
            JsonConvert.SerializeObject(command, Formatting.Indented));
        if (File.Exists(runtime.AuditCommandPath))
            File.Delete(runtime.AuditCommandPath);
        File.Move(temp, runtime.AuditCommandPath);
    }

    private static bool WaitForFile(string path, int timeoutMs)
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

    private static string WriteCombinedDump(
        string baseDir,
        string runId,
        List<AuditRuntime> workers,
        StorageState expectedStorage,
        out AuditDifferenceSummary differences)
    {
        string directory = Path.Combine(baseDir, "diagnostic-dumps");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            $"citybankers-bagaudit-{runId}.log");

        var text = new StringBuilder();
        text.AppendLine("CityBankers parallel bag identity/content audit");
        text.AppendLine("RunId: " + runId);
        text.AppendLine(
            "CreatedUtc: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        text.AppendLine("Mode: NON-DESTRUCTIVE STAGED READ");
        text.AppendLine(
            "MutationPolicy: inventory bags are opened in place; each bank bag may be " +
            "temporarily moved to one free normal-inventory slot, opened/read, and returned " +
            "to bank before the next bank bag. Bag contents are never moved, added, deleted, " +
            "or renamed.");
        text.AppendLine("WorkerCount: " + workers.Count);
        text.AppendLine(
            "ExpectedBaseline: " +
            (expectedStorage?.BaselineRunId ?? "<missing storage-state>"));
        text.AppendLine(
            "ComparisonHint: compare UNIQUE identities across runs. For bank bags also compare " +
            "OUTER_ORIGINAL and RETURNED_BANK to see whether physical position survives staging/relog.");
        text.AppendLine();

        differences = new AuditDifferenceSummary();

        foreach (AuditRuntime worker in workers)
        {
            text.AppendLine(new string('=', 78));
            text.AppendLine(
                $"ROLE {worker.Role} CHARACTER {worker.Character}");
            text.AppendLine(new string('=', 78));

            if (worker.Audit == null)
            {
                text.AppendLine("RESULT: MISSING");
                if (!string.IsNullOrWhiteSpace(worker.ReadError))
                    text.AppendLine("READ_ERROR: " + SingleLine(worker.ReadError));
                differences.IncompleteWorkers++;
                AddDifference(
                    text,
                    differences,
                    "AUDIT_RESULT_MISSING role=" + worker.Role + " character=" +
                    worker.Character + " parseError=" + Quote(worker.ReadError));
                text.AppendLine();
                continue;
            }

            BagAuditResult result = worker.Audit;
            int distinctUnique = DistinctUniqueIdentities(result);
            int duplicateUnique = Math.Max(0, result.TotalBagCount - distinctUnique);
            int distinctOuter = result.Bags != null
                ? result.Bags.Select(b => b.Source + "|" + b.OuterSlot).Distinct().Count()
                : 0;
            int distinctHandles = result.Bags != null
                ? result.Bags.Where(b => b.Opened).Select(b => b.Handle).Distinct().Count()
                : 0;
            int returnedOriginal = result.Bags != null
                ? result.Bags.Count(b =>
                    string.Equals(b.Source, "bank", StringComparison.Ordinal) &&
                    b.ReturnedToBank &&
                    b.ReturnedToOriginalOuterSlot)
                : 0;

            text.AppendLine("ObservedUtc: " + result.ObservedUtc.ToString("O"));
            text.AppendLine("PlayfieldModelId: " + result.PlayfieldModelId);
            text.AppendLine("BankOpened: " + result.BankOpened);
            AppendLooseCensus(text, "LOOSE_INVENTORY", result.LooseInventoryItems);
            AppendLooseCensus(text, "LOOSE_BANK", result.LooseBankItems);
            text.AppendLine(
                $"SUMMARY total={result.TotalBagCount} bank={result.BankBagCount} " +
                $"inventory={result.InventoryBagCount} opened={result.OpenedCount} " +
                $"failed={result.FailedCount} empty={result.EmptyCount} " +
                $"nonempty={result.NonEmptyCount} bankStaged={result.BankStagedCount} " +
                $"bankReturned={result.BankReturnedCount} " +
                $"bankReturnFailures={result.BankReturnFailureCount}");
            text.AppendLine(
                $"IDENTITY_SUMMARY distinctUnique={distinctUnique} " +
                $"duplicateUnique={duplicateUnique} distinctOuter={distinctOuter} " +
                $"distinctOpenHandles={distinctHandles} " +
                $"returnedToOriginalBankSlot={returnedOriginal}/{result.BankReturnedCount}");
            if (!string.IsNullOrWhiteSpace(result.FatalError))
                text.AppendLine("FATAL_ERROR: " + SingleLine(result.FatalError));
            text.AppendLine();

            foreach (BagAuditEntry bag in (result.Bags ?? new List<BagAuditEntry>())
                .OrderBy(b => b.Source == "bank" ? 0 : 1)
                .ThenBy(b => b.OuterSlotInstance)
                .ThenBy(b => b.Ordinal))
            {
                text.AppendLine(
                    $"BAG ordinal={bag.Ordinal:D3} source={bag.Source} " +
                    $"name={Quote(bag.Name)} lowId={bag.LowId} highId={bag.HighId} ql={bag.Ql}");
                text.AppendLine(
                    $"  OUTER_ORIGINAL type={bag.OuterSlotType} instance={bag.OuterSlotInstance} " +
                    $"identity={bag.OuterSlot}");
                text.AppendLine(
                    $"  UNIQUE type={bag.UniqueIdentityType} instance={bag.UniqueIdentityInstance} " +
                    $"identity={bag.UniqueIdentity}");
                text.AppendLine(
                    $"  STAGE attempted={bag.MoveToInventoryAttempted} " +
                    $"completed={bag.MoveToInventoryCompleted} " +
                    $"slotType={bag.StagedInventorySlotType} " +
                    $"slotInstance={bag.StagedInventorySlotInstance} " +
                    $"slot={bag.StagedInventorySlot} elapsedMs={bag.MoveToInventoryElapsedMs}");
                text.AppendLine(
                    $"  OPEN opened={bag.Opened} preHandle={bag.PreOpenHandle} " +
                    $"handle={bag.Handle} containerIdentity={bag.ContainerIdentity} " +
                    $"elapsedMs={bag.OpenElapsedMs} itemCount={bag.ItemCount} " +
                    $"freeSlots={bag.FreeSlots} error={Quote(bag.Error)}");
                text.AppendLine(
                    $"  RETURNED_BANK attempted={bag.ReturnToBankAttempted} " +
                    $"returned={bag.ReturnedToBank} sameOriginalSlot={bag.ReturnedToOriginalOuterSlot} " +
                    $"slotType={bag.ReturnedOuterSlotType} " +
                    $"slotInstance={bag.ReturnedOuterSlotInstance} " +
                    $"slot={bag.ReturnedOuterSlot} elapsedMs={bag.ReturnToBankElapsedMs}");

                if (bag.Items == null || bag.Items.Count == 0)
                {
                    text.AppendLine("  CONTENT <empty>");
                }
                else
                {
                    foreach (BagInnerItem item in bag.Items.OrderBy(i => i.SlotInstance))
                    {
                        text.AppendLine(
                            $"  CONTENT slotType={item.SlotType} slotInstance={item.SlotInstance} " +
                            $"slot={item.Slot} unique={item.UniqueIdentity} " +
                            $"name={Quote(item.Name)} lowId={item.LowId} " +
                            $"highId={item.HighId} ql={item.Ql}");
                    }
                }
            }

            AppendExpectedStateComparison(
                text,
                worker,
                expectedStorage,
                differences);

            text.AppendLine();
        }

        text.AppendLine(new string('=', 78));
        text.AppendLine("EXPECTED-STATE DIFFERENCE TOTALS");
        text.AppendLine(new string('=', 78));
        text.AppendLine(
            $"missingBags={differences.MissingBags} " +
            $"unexpectedBags={differences.UnexpectedBags} " +
            $"unverifiedBags={differences.UnverifiedBags} " +
            $"unverifiedItems={differences.UnverifiedItems} " +
            $"incompleteWorkers={differences.IncompleteWorkers} " +
            $"missingItems={differences.MissingItems} " +
            $"unexpectedItems={differences.UnexpectedItems} " +
            $"metadataChanges={differences.MetadataChanges} " +
            $"templateCountChanges={differences.TemplateCountChanges}");
        text.AppendLine(
            "BaselineMutation: NONE. This report compared the pre-run storage-state " +
            "snapshot with live audit results and did not replace it.");

        File.WriteAllText(path, text.ToString());
        return Path.GetFullPath(path);
    }

    private static void AppendExpectedStateComparison(
        StringBuilder text,
        AuditRuntime runtime,
        StorageState expectedStorage,
        AuditDifferenceSummary summary)
    {
        int detailCountBeforeWorker = summary.Details.Count;
        text.AppendLine();
        text.AppendLine("  EXPECTED_STATE_COMPARISON");

        if (string.Equals(runtime.Role, "central", StringComparison.OrdinalIgnoreCase))
        {
            text.AppendLine("    CENTRAL_OBSERVATION_ONLY: storage-state has no Central " +
                "baseline. Bag and loose-item observations are evidence, not a custody verdict.");
            return;
        }

        StorageWorkerState expectedWorker = (expectedStorage?.Workers ??
            new List<StorageWorkerState>()).FirstOrDefault(worker =>
                worker != null &&
                string.Equals(worker.Role, runtime.Role, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    worker.Character,
                    runtime.Character,
                    StringComparison.OrdinalIgnoreCase));
        if (expectedWorker == null)
        {
            AddDifference(
                text,
                summary,
                "EXPECTED_WORKER_MISSING role=" + runtime.Role +
                " character=" + runtime.Character);
            return;
        }

        List<StorageBagState> unmatchedExpected =
            new List<StorageBagState>(expectedWorker.Bags ?? new List<StorageBagState>());
        var observedItems = new List<ObservedAuditItem>();
        var expectedItems = new List<ExpectedAuditItem>();
        bool completeContentComparison = true;

        foreach (StorageBagState expectedBag in unmatchedExpected)
        {
            foreach (StoredItemState item in expectedBag?.Items ?? new List<StoredItemState>())
            {
                if (item != null)
                    expectedItems.Add(new ExpectedAuditItem(expectedBag, item));
            }
        }

        foreach (BagAuditEntry observedBag in runtime.Audit?.Bags ??
            new List<BagAuditEntry>())
        {
            int expectedBagIndex = FindExpectedBagIndex(unmatchedExpected, observedBag);
            if (expectedBagIndex < 0)
            {
                summary.UnexpectedBags++;
                AddDifference(
                    text,
                    summary,
                    "UNEXPECTED_BAG role=" + runtime.Role + " character=" +
                    runtime.Character + " live=" + AuditBagLabel(observedBag));
                if (!observedBag.Opened)
                {
                    summary.UnverifiedBags++;
                    completeContentComparison = false;
                    continue;
                }
                foreach (BagInnerItem item in observedBag.Items ?? new List<BagInnerItem>())
                {
                    observedItems.Add(new ObservedAuditItem(observedBag, item));
                    summary.UnexpectedItems++;
                    AddDifference(
                        text,
                        summary,
                        "UNEXPECTED_ITEM role=" + runtime.Role + " character=" +
                        runtime.Character + " live=" + AuditItemLabel(observedBag, item));
                }
                continue;
            }

            StorageBagState expectedBag = unmatchedExpected[expectedBagIndex];
            unmatchedExpected.RemoveAt(expectedBagIndex);

            if (!string.Equals(
                    expectedBag.Source,
                    observedBag.Source,
                    StringComparison.OrdinalIgnoreCase) ||
                expectedBag.OuterSlotInstance != observedBag.OuterSlotInstance)
            {
                summary.MetadataChanges++;
                AddDifference(
                    text,
                    summary,
                    "BAG_LOCATION_CHANGED role=" + runtime.Role + " character=" +
                    runtime.Character + " expected=" + StorageBagLabel(expectedBag) +
                    " live=" + AuditBagLabel(observedBag));
            }
            if (!string.Equals(
                    expectedBag.LastUniqueIdentity,
                    observedBag.UniqueIdentity,
                    StringComparison.Ordinal))
            {
                summary.MetadataChanges++;
                AddDifference(
                    text,
                    summary,
                    "BAG_IDENTITY_CHANGED role=" + runtime.Role + " character=" +
                    runtime.Character + " expected=" + StorageBagLabel(expectedBag) +
                    " live=" + AuditBagLabel(observedBag));
            }
            if (!observedBag.Opened)
            {
                summary.UnverifiedBags++;
                completeContentComparison = false;
                AddDifference(
                    text,
                    summary,
                    "UNVERIFIED_BAG role=" + runtime.Role + " character=" +
                    runtime.Character + " expected=" + StorageBagLabel(expectedBag) +
                    " live=" + AuditBagLabel(observedBag) + " error=" +
                    Quote(observedBag.Error));
                foreach (StoredItemState item in
                    expectedBag.Items ?? new List<StoredItemState>())
                {
                    summary.UnverifiedItems++;
                    AddDifference(
                        text,
                        summary,
                        "EXPECTED_ITEM_UNVERIFIED role=" + runtime.Role +
                        " character=" + runtime.Character +
                        " reason=bag-open-failed expected=" +
                        StorageItemLabel(expectedBag, item));
                }
                continue;
            }

            int observedCapacity = observedBag.ItemCount + observedBag.FreeSlots;
            if (expectedBag.Capacity != observedCapacity)
            {
                summary.MetadataChanges++;
                AddDifference(
                    text,
                    summary,
                    "BAG_CAPACITY_CHANGED role=" + runtime.Role + " character=" +
                    runtime.Character + " expected=" + expectedBag.Capacity +
                    " observed=" + observedCapacity + " bag=" +
                    AuditBagLabel(observedBag));
            }

            List<BagInnerItem> unmatchedObserved =
                new List<BagInnerItem>(observedBag.Items ?? new List<BagInnerItem>());
            foreach (BagInnerItem item in unmatchedObserved)
                observedItems.Add(new ObservedAuditItem(observedBag, item));

            foreach (StoredItemState expectedItem in
                expectedBag.Items ?? new List<StoredItemState>())
            {
                int observedIndex = unmatchedObserved.FindIndex(item =>
                    item != null && (item.SlotInstance & 0xFFFF) ==
                        (expectedItem.InnerSlot & 0xFFFF) &&
                    SameTemplate(expectedItem, item));
                if (observedIndex >= 0)
                {
                    BagInnerItem observedItem = unmatchedObserved[observedIndex];
                    if (!string.Equals(
                            IsUsableIdentity(expectedItem.UniqueIdentity) ? expectedItem.UniqueIdentity : null,
                            IsUsableIdentity(observedItem.UniqueIdentity) ? observedItem.UniqueIdentity : null,
                            StringComparison.Ordinal))
                    {
                        summary.MetadataChanges++;
                        AddDifference(
                            text,
                            summary,
                            "ITEM_IDENTITY_CHANGED role=" + runtime.Role +
                            " character=" + runtime.Character + " expected=" +
                            StorageItemLabel(expectedBag, expectedItem) + " live=" +
                            AuditItemLabel(observedBag, observedItem));
                    }
                    if (!string.Equals(
                            expectedItem.Name,
                            observedItem.Name,
                            StringComparison.Ordinal))
                    {
                        summary.MetadataChanges++;
                        AddDifference(
                            text,
                            summary,
                            "ITEM_NAME_CHANGED role=" + runtime.Role +
                            " character=" + runtime.Character + " expected=" +
                            StorageItemLabel(expectedBag, expectedItem) + " live=" +
                            AuditItemLabel(observedBag, observedItem));
                    }
                    unmatchedObserved.RemoveAt(observedIndex);
                    continue;
                }

                summary.MissingItems++;
                AddDifference(
                    text,
                    summary,
                    "EXPECTED_ITEM_MISSING role=" + runtime.Role + " character=" +
                    runtime.Character + " expected=" +
                    StorageItemLabel(expectedBag, expectedItem));
            }

            foreach (BagInnerItem unexpected in unmatchedObserved)
            {
                summary.UnexpectedItems++;
                AddDifference(
                    text,
                    summary,
                    "UNEXPECTED_ITEM role=" + runtime.Role + " character=" +
                    runtime.Character + " live=" +
                    AuditItemLabel(observedBag, unexpected));
            }
        }

        foreach (StorageBagState missingBag in unmatchedExpected)
        {
            summary.MissingBags++;
            completeContentComparison = false;
            AddDifference(
                text,
                summary,
                "EXPECTED_BAG_MISSING role=" + runtime.Role + " character=" +
                runtime.Character + " expected=" + StorageBagLabel(missingBag) +
                " expectedItems=" + (missingBag?.Items?.Count ?? 0));
            foreach (StoredItemState item in missingBag?.Items ?? new List<StoredItemState>())
            {
                summary.UnverifiedItems++;
                AddDifference(
                    text,
                    summary,
                    "EXPECTED_ITEM_UNVERIFIED role=" + runtime.Role + " character=" +
                    runtime.Character + " reason=expected-bag-missing expected=" +
                    StorageItemLabel(missingBag, item));
            }
        }

        Dictionary<string, int> expectedCounts = expectedItems
            .GroupBy(item => TemplateKey(item.Item.AoId, item.Item.HighId, item.Item.Ql))
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Dictionary<string, int> observedCounts = observedItems
            .GroupBy(item => TemplateKey(item.Item.LowId, item.Item.HighId, item.Item.Ql))
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (string key in expectedCounts.Keys.Union(observedCounts.Keys)
            .OrderBy(value => value, StringComparer.Ordinal))
        {
            int expectedCount;
            int observedCount;
            expectedCounts.TryGetValue(key, out expectedCount);
            observedCounts.TryGetValue(key, out observedCount);
            if (expectedCount == observedCount)
                continue;

            summary.TemplateCountChanges++;
            string name = expectedItems
                .Where(item => TemplateKey(
                    item.Item.AoId,
                    item.Item.HighId,
                    item.Item.Ql) == key)
                .Select(item => item.Item.Name)
                .Concat(observedItems
                    .Where(item => TemplateKey(
                        item.Item.LowId,
                        item.Item.HighId,
                        item.Item.Ql) == key)
                    .Select(item => item.Item.Name))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            AddDifference(
                text,
                summary,
                "TEMPLATE_COUNT role=" + runtime.Role + " character=" +
                runtime.Character + " template=" + key + " name=" + Quote(name) +
                " expected=" + expectedCount + " observed=" + observedCount +
                " delta=" + (observedCount - expectedCount) +
                " comparisonComplete=" + completeContentComparison);
        }

        if (summary.Details.Count == detailCountBeforeWorker)
            text.AppendLine("    NO_DIFFERENCES");
    }

    private static int FindExpectedBagIndex(
        List<StorageBagState> expectedBags,
        BagAuditEntry observed)
    {
        if (expectedBags == null || observed == null)
            return -1;

        if (IsUsableIdentity(observed.UniqueIdentity))
        {
            int identityIndex = expectedBags.FindIndex(bag =>
                bag != null && IsUsableIdentity(bag.LastUniqueIdentity) &&
                string.Equals(
                    bag.LastUniqueIdentity,
                    observed.UniqueIdentity,
                    StringComparison.Ordinal));
            if (identityIndex >= 0)
                return identityIndex;
        }

        return expectedBags.FindIndex(bag =>
            bag != null && string.Equals(
                bag.Source,
                observed.Source,
                StringComparison.OrdinalIgnoreCase) &&
            bag.OuterSlotInstance == observed.OuterSlotInstance);
    }

    private static bool SameTemplate(StoredItemState expected, BagInnerItem observed)
    {
        return expected != null && observed != null &&
            expected.AoId == observed.LowId && expected.HighId == observed.HighId &&
            expected.Ql == observed.Ql;
    }

    private static string TemplateKey(int lowId, int highId, int ql)
    {
        return lowId + "/" + highId + "/" + ql;
    }

    private static bool IsUsableIdentity(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            !string.Equals(value, "(None:0000)", StringComparison.Ordinal) &&
            !string.Equals(value, "None:0000", StringComparison.Ordinal);
    }

    private static string StorageBagLabel(StorageBagState bag)
    {
        return (bag?.Source ?? "?") + ":" + (bag?.OuterSlotInstance ?? -1) +
            " uid=" + (bag?.LastUniqueIdentity ?? "<none>");
    }

    private static string AuditBagLabel(BagAuditEntry bag)
    {
        return (bag?.Source ?? "?") + ":" + (bag?.OuterSlotInstance ?? -1) +
            " uid=" + (bag?.UniqueIdentity ?? "<none>") +
            " opened=" + (bag?.Opened ?? false);
    }

    private static string StorageItemLabel(StorageBagState bag, StoredItemState item)
    {
        return StorageBagLabel(bag) + "/inner:" + (item?.InnerSlot ?? -1) +
            " uid=" + (item?.UniqueIdentity ?? "<none>") + " name=" +
            Quote(item?.Name) + " aoid=" + (item?.AoId ?? 0) + " highId=" +
            (item?.HighId ?? 0) + " ql=" + (item?.Ql ?? 0) + " transaction=" +
            (item?.TransactionId ?? "<none>");
    }

    private static string AuditItemLabel(BagAuditEntry bag, BagInnerItem item)
    {
        return AuditBagLabel(bag) + "/inner:" + (item?.SlotInstance ?? -1) +
            " uid=" + (item?.UniqueIdentity ?? "<none>") + " name=" +
            Quote(item?.Name) + " aoid=" + (item?.LowId ?? 0) + " highId=" +
            (item?.HighId ?? 0) + " ql=" + (item?.Ql ?? 0);
    }

    private static void AddDifference(
        StringBuilder text,
        AuditDifferenceSummary summary,
        string detail)
    {
        string value = detail ?? string.Empty;
        text.AppendLine("    " + value);
        summary.Details.Add(value);
    }

    private static void AppendLooseCensus(
        StringBuilder text, string section, List<BagInnerItem> items)
    {
        if (items == null)
        {
            text.AppendLine(section + " UNVERIFIED: no item census supplied");
            return;
        }
        text.AppendLine(section + " count=" + items.Count);
        foreach (BagInnerItem item in items.OrderBy(value => value.SlotInstance))
        {
            text.AppendLine("  ITEM slot=" + item.Slot + " unique=" + item.UniqueIdentity +
                " name=" + Quote(item.Name) + " lowId=" + item.LowId +
                " highId=" + item.HighId + " ql=" + item.Ql);
        }
    }

    private static int DistinctUniqueIdentities(BagAuditResult result)
    {
        return result != null && result.Bags != null
            ? result.Bags
                .Where(b => !string.IsNullOrWhiteSpace(b.UniqueIdentity))
                .Select(b => b.UniqueIdentity)
                .Distinct(StringComparer.Ordinal)
                .Count()
            : 0;
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";
        return "\"" +
            value.Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\"", "\\\"") +
            "\"";
    }

    private static string SingleLine(string value)
    {
        return (value ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ");
    }

    private static string SafeFileToken(string value)
    {
        string token = string.IsNullOrWhiteSpace(value) ? "unknown" : value;
        foreach (char invalid in Path.GetInvalidFileNameChars())
            token = token.Replace(invalid, '_');
        return token;
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

    private static void StopForError(string message)
    {
        Console.WriteLine(message);
        Console.WriteLine();
        Console.WriteLine("Press ENTER to exit.");
        Console.ReadLine();
        Environment.ExitCode = 1;
    }

    private class AuditConfig
    {
        public string Password;
        public int MaxParallelLogins = 32;
        public int DiagnosticTimeoutMs = DefaultDiagnosticTimeoutMs;
        public Dictionary<string, AuditAccount> Roles;
    }

    private class AuditAccount
    {
        public string Username;
        public string Password;
        public string Character;
    }

    private class AuditRole
    {
        public string Role;
        public AuditAccount Account;
    }

    private class AuditRuntime
    {
        public string Role;
        public string Character;
        public ClientDomain Domain;
        public string DiagnosticPath;
        public string AuditCommandPath;
        public string AuditResultPath;
        public bool Started;
        public BagAuditResult Audit;
        public string ReadError;
    }

    private sealed class AuditDifferenceSummary
    {
        public int IncompleteWorkers;
        public int MissingBags;
        public int UnexpectedBags;
        public int UnverifiedBags;
        public int UnverifiedItems;
        public int MissingItems;
        public int UnexpectedItems;
        public int MetadataChanges;
        public int TemplateCountChanges;
        public readonly List<string> Details = new List<string>();
    }

    private sealed class ExpectedAuditItem
    {
        public ExpectedAuditItem(StorageBagState bag, StoredItemState item)
        {
            Bag = bag;
            Item = item;
        }

        public StorageBagState Bag;
        public StoredItemState Item;
    }

    private sealed class ObservedAuditItem
    {
        public ObservedAuditItem(BagAuditEntry bag, BagInnerItem item)
        {
            Bag = bag;
            Item = item;
        }

        public BagAuditEntry Bag;
        public BagInnerItem Item;
    }

    private class BagAuditCommand
    {
        public string RunId;
        public string Role;
        public int BagOpenTimeoutMs;
        public int BagMoveTimeoutMs;
    }

    private class BagAuditResult
    {
        public List<BagInnerItem> LooseInventoryItems;
        public List<BagInnerItem> LooseBankItems;
        public string RunId;
        public string Role;
        public string Character;
        public DateTime ObservedUtc;
        public int PlayfieldModelId;
        public bool BankOpened;
        public int BankOuterItemCount;
        public int BankBagCount;
        public int InventoryBagCount;
        public int TotalBagCount;
        public int OpenedCount;
        public int FailedCount;
        public int EmptyCount;
        public int NonEmptyCount;
        public int BankStagedCount;
        public int BankReturnedCount;
        public int BankReturnFailureCount;
        public string FatalError;
        public List<BagAuditEntry> Bags;
    }

    private class BagAuditEntry
    {
        public int Ordinal;
        public string Source;
        public string OuterSlot;
        public string OuterSlotType;
        public int OuterSlotInstance;
        public string UniqueIdentity;
        public string UniqueIdentityType;
        public int UniqueIdentityInstance;
        public string Name;
        public int LowId;
        public int HighId;
        public int Ql;
        public bool MoveToInventoryAttempted;
        public bool MoveToInventoryCompleted;
        public string StagedInventorySlot;
        public string StagedInventorySlotType;
        public int StagedInventorySlotInstance;
        public int MoveToInventoryElapsedMs;
        public int PreOpenHandle;
        public bool Opened;
        public int Handle;
        public string ContainerIdentity;
        public int ItemCount;
        public int FreeSlots;
        public int OpenElapsedMs;
        public bool ReturnToBankAttempted;
        public bool ReturnedToBank;
        public string ReturnedOuterSlot;
        public string ReturnedOuterSlotType;
        public int ReturnedOuterSlotInstance;
        public bool ReturnedToOriginalOuterSlot;
        public int ReturnToBankElapsedMs;
        public string Error;
        public List<BagInnerItem> Items;
    }

    private class BagInnerItem
    {
        public string Slot;
        public string SlotType;
        public int SlotInstance;
        public string UniqueIdentity;
        public string Name;
        public int LowId;
        public int HighId;
        public int Ql;
    }
}
