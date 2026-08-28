using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Chat;
using AOSharp.Clientless.Logging;
using Newtonsoft.Json;

namespace CityManager
{
    public partial class CityManager
    {
        private const int AltStateVersion = 1;
        private const int AltRefreshHours = 24;
        private const int AltReplyTimeoutSeconds = 30;
        private const int AltSuccessfulRequestSpacingSeconds = 2;
        private const int AltLoginRefreshDelaySeconds = 30;
        private const int AltRosterOfficerSpacingSeconds = 10;

        private static readonly Regex AltHeadingRegex = new Regex(
            @"Alts\s+of\s+([A-Za-z0-9]+)\s*\((\d+)\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AltCharacterRegex = new Regex(
            @"<font\s+color=['""]#00BFFF['""]>([A-Za-z0-9]+)</font>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly object _altsSync = new object();
        private readonly Dictionary<string, AltGroup> _altGroups =
            new Dictionary<string, AltGroup>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _altToMain =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<AltLookupRequest> _altQueue =
            new List<AltLookupRequest>();

        private string _altsPath;
        private string _altsBotName;
        private AltLookupRequest _pendingAltLookup;
        private bool _altSendInFlight;
        private bool _altsShuttingDown;
        private DateTime _nextAltRequestUtc = DateTime.MinValue;
        private DateTime _nextAltTickUtc = DateTime.MinValue;
        private DateTime _nextStaleAltScanUtc = DateTime.MinValue;

        private void InitializeAlts()
        {
            lock (_altsSync)
            {
                _altsPath = Path.Combine(_settingsDir, "alts.json");
                _altsBotName = LoadAltBotName();
                _altGroups.Clear();
                _altToMain.Clear();
                _altQueue.Clear();
                _pendingAltLookup = null;
                _altSendInFlight = false;
                _altsShuttingDown = false;
                _nextAltRequestUtc = DateTime.MinValue;
                _nextAltTickUtc = DateTime.MinValue;
                _nextStaleAltScanUtc = DateTime.MinValue;

                if (File.Exists(_altsPath))
                {
                    try
                    {
                        LoadAltsLocked();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Unable to load alt cache: {ex.Message}");
                        PreserveInvalidAltsFileLocked();
                        _altGroups.Clear();
                        _altToMain.Clear();
                        TrySaveAltsLocked();
                    }
                }
                else
                {
                    TrySaveAltsLocked();
                }
            }

            CanonicalizeKnownLists();

            Logger.Information(
                $"Alt cache initialized: groups={GetAltGroupCount()}, " +
                $"bot={_altsBotName ?? "disabled"}.");
            DevTrace(
                $"ALTS initialized file=alts.json groups={GetAltGroupCount()} " +
                $"bot={_altsBotName ?? "disabled"}.");
        }

        private void BeginAltsAfterInPlay()
        {
            _nextStaleAltScanUtc = DateTime.MinValue;
            QueueStaleAdministratorAlts();
            QueueRosterOfficerAlts(
                GetCachedOfficerCharacters(),
                "cached-officer-roster");
        }

        private void ShutdownAlts()
        {
            lock (_altsSync)
            {
                _altsShuttingDown = true;
                _altQueue.Clear();
                _pendingAltLookup = null;
                TrySaveAltsLocked();
            }
        }

        private string LoadAltBotName()
        {
            string managerPath = Path.Combine(_settingsDir, "manager.json");
            if (!File.Exists(managerPath))
                return null;

            try
            {
                AltBotConfig config = JsonConvert.DeserializeObject<AltBotConfig>(
                    File.ReadAllText(managerPath));

                string normalized;
                string error;
                if (config == null || string.IsNullOrWhiteSpace(config.Bot))
                    return null;

                if (!TryNormalizeAltName(config.Bot, out normalized, out error))
                {
                    Logger.Warning($"Manager Bot setting is invalid: {error}");
                    return null;
                }

                return normalized;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Unable to read Manager Bot setting: {ex.Message}");
                return null;
            }
        }

        private bool HasTellAltsCommandShape(string[] parts)
        {
            if (parts == null || parts.Length == 0 || parts.Length > 4)
                return false;

            if (parts.Length <= 2)
                return true;

            if (parts.Length == 3)
            {
                return string.Equals(parts[1], "list", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(parts[1], "add", StringComparison.OrdinalIgnoreCase) ||
                       IsRemoveVerb(parts[1]);
            }

            return string.Equals(parts[1], "add", StringComparison.OrdinalIgnoreCase) ||
                   IsRemoveVerb(parts[1]);
        }

        private void ProcessAltsCommand(
            string senderName,
            string[] parts,
            ReplyTarget target,
            bool isAdmin)
        {
            if (parts.Length == 1)
            {
                ShowAndRefreshAlts(senderName, target);
                return;
            }

            if (string.Equals(parts[1], "list", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 2)
                {
                    Reply(target, BuildCachedAltSummary(senderName));
                    return;
                }

                if (parts.Length == 3 && isAdmin)
                {
                    Reply(target, BuildCachedAltSummary(parts[2]));
                    return;
                }

                Reply(
                    target,
                    isAdmin
                        ? Usage(target, "alts list [character]")
                        : "You may only view your own alts.");
                return;
            }

            bool add = string.Equals(
                parts[1],
                "add",
                StringComparison.OrdinalIgnoreCase);
            bool remove = IsRemoveVerb(parts[1]);

            if (add || remove)
            {
                if (!isAdmin)
                {
                    Reply(target, "Only administrators may edit the alt cache.");
                    return;
                }

                string main;
                string alternate;
                if (parts.Length == 3)
                {
                    main = senderName;
                    alternate = parts[2];
                }
                else if (parts.Length == 4)
                {
                    main = parts[2];
                    alternate = parts[3];
                }
                else
                {
                    Reply(
                        target,
                        Usage(
                            target,
                            "alts [add|del|rem|remove|delete] [main] [alternate]"));
                    return;
                }

                string mutationMessage;
                bool changed = TryMutateAltGroup(
                    main,
                    alternate,
                    add,
                    out mutationMessage);

                DevTrace(
                    $"ALTS MANUAL {(add ? "ADD" : "DEL")} actor={senderName} " +
                    $"main={main} alternate={alternate} changed={changed}; {mutationMessage}");
                Reply(target, mutationMessage);

                if (changed)
                    CanonicalizeKnownLists();

                return;
            }

            if (parts.Length == 2)
            {
                if (!isAdmin)
                {
                    Reply(target, "You may only view your own alts.");
                    return;
                }

                ShowAndRefreshAlts(parts[1], target);
                return;
            }

            Reply(
                target,
                isAdmin
                    ? Usage(target, "alts [character|list|add|del]")
                    : Usage(target, "alts"));
        }

        private void ShowAndRefreshAlts(string characterName, ReplyTarget target)
        {
            string normalized;
            string error;
            if (!TryNormalizeAltName(characterName, out normalized, out error))
            {
                Reply(target, error);
                return;
            }

            string cached = BuildCachedAltSummary(normalized);
            string fingerprint = GetAltFingerprint(normalized);

            if (string.IsNullOrWhiteSpace(_altsBotName))
            {
                Reply(
                    target,
                    cached + " External alt lookup is disabled; set Bot in settings/manager.json to enable it.");
                return;
            }

            Reply(target, cached + $" Asking {_altsBotName} for a fresh reading.");
            QueueAltLookup(
                normalized,
                new AltLookupWaiter
                {
                    Target = target,
                    BeforeFingerprint = fingerprint
                },
                true,
                false,
                "command");
        }

        private void TickAlts()
        {
            if (string.IsNullOrWhiteSpace(_altsBotName))
                return;

            DateTime now = DateTime.UtcNow;
            if (now < _nextAltTickUtc)
                return;

            _nextAltTickUtc = now.AddSeconds(1);

            if (now >= _nextStaleAltScanUtc)
            {
                _nextStaleAltScanUtc = now.AddMinutes(1);
                QueueStaleAdministratorAlts();
            }

            AltLookupRequest timedOut = null;
            AltLookupRequest toSend = null;

            lock (_altsSync)
            {
                if (_altsShuttingDown)
                    return;

                if (_pendingAltLookup != null &&
                    now >= _pendingAltLookup.DeadlineUtc)
                {
                    timedOut = _pendingAltLookup;
                    _pendingAltLookup = null;
                    _nextAltRequestUtc = now.AddSeconds(AltReplyTimeoutSeconds);

                    if (timedOut.KeepRetrying)
                        RequeueAltLookupLocked(timedOut.Target, true, "retry");
                }

                if (_pendingAltLookup == null &&
                    !_altSendInFlight &&
                    now >= _nextAltRequestUtc &&
                    _altQueue.Count > 0)
                {
                    toSend = _altQueue.FirstOrDefault(request =>
                        request.NotBeforeUtc <= now);

                    if (toSend != null)
                    {
                        _altQueue.Remove(toSend);
                        _altSendInFlight = true;
                    }
                }
            }

            if (timedOut != null)
            {
                DevTrace(
                    $"ALTS {_altsBotName} timeout target={timedOut.Target}; " +
                    "cached answer retained, retry spacing=30s.");
                ReplyToAltWaiters(
                    timedOut,
                    $"{_altsBotName} did not answer within 30 seconds. " +
                    BuildCachedAltSummary(timedOut.Target));
            }

            if (toSend != null)
                BeginSendAltLookup(toSend);
        }

        private void QueueStaleAdministratorAlts()
        {
            if (string.IsNullOrWhiteSpace(_altsBotName))
                return;

            foreach (string administrator in AdminListStore.Snapshot())
                QueueAltLookup(administrator, null, false, true, "daily-admin-refresh");
        }

        private void QueueRosterOfficerAlts(
            IEnumerable<string> officerCharacters,
            string reason)
        {
            if (string.IsNullOrWhiteSpace(_altsBotName))
                return;

            int queued = 0;
            foreach (string officer in officerCharacters ?? Enumerable.Empty<string>())
            {
                if (QueueAltLookup(
                        officer,
                        null,
                        false,
                        false,
                        reason,
                        queued * AltRosterOfficerSpacingSeconds))
                {
                    queued++;
                }
            }
        }

        private void ObserveAltLoginAnnouncement(
            string senderName,
            string message)
        {
            if (string.IsNullOrWhiteSpace(_altsBotName) ||
                !string.Equals(senderName, _altsBotName, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(message) ||
                message.IndexOf("has logged on", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            Match character = AltCharacterRegex.Match(message);
            if (!character.Success)
                return;

            string loggedOnCharacter = character.Groups[1].Value;
            QueueAltLookup(
                loggedOnCharacter,
                null,
                false,
                false,
                "org-login",
                AltLoginRefreshDelaySeconds);
        }

        private bool QueueAltLookup(
            string characterName,
            AltLookupWaiter waiter,
            bool force,
            bool keepRetrying,
            string reason,
            int delaySeconds = 0)
        {
            string normalized;
            string error;
            if (!TryNormalizeAltName(characterName, out normalized, out error))
                return false;

            lock (_altsSync)
            {
                string target = ResolveCanonicalAltMainLocked(normalized);
                DateTime notBeforeUtc = DateTime.UtcNow.AddSeconds(
                    Math.Max(0, delaySeconds));

                if (!force && IsAltGroupFreshLocked(target, DateTime.UtcNow))
                    return false;

                AltLookupRequest existing = FindAltRequestLocked(target);
                if (existing != null)
                {
                    if (waiter != null)
                        existing.Waiters.Add(waiter);
                    existing.Force = existing.Force || force;
                    existing.KeepRetrying = existing.KeepRetrying || keepRetrying;

                    if (force || existing.NotBeforeUtc > notBeforeUtc)
                        existing.NotBeforeUtc = notBeforeUtc;

                    if (force && !ReferenceEquals(existing, _pendingAltLookup))
                    {
                        _altQueue.Remove(existing);
                        _altQueue.Insert(0, existing);
                    }

                    return true;
                }

                var request = new AltLookupRequest
                {
                    Target = target,
                    Force = force,
                    KeepRetrying = keepRetrying,
                    Reason = reason,
                    NotBeforeUtc = notBeforeUtc,
                    Waiters = new List<AltLookupWaiter>()
                };
                if (waiter != null)
                    request.Waiters.Add(waiter);

                if (force)
                    _altQueue.Insert(0, request);
                else
                    _altQueue.Add(request);
                DevTrace(
                    $"ALTS queued target={target} reason={reason} " +
                    $"force={force} retry={keepRetrying} " +
                    $"not-before={notBeforeUtc:O}.");
                return true;
            }
        }

        private AltLookupRequest FindAltRequestLocked(string target)
        {
            if (_pendingAltLookup != null &&
                string.Equals(
                    ResolveCanonicalAltMainLocked(_pendingAltLookup.Target),
                    target,
                    StringComparison.OrdinalIgnoreCase))
            {
                return _pendingAltLookup;
            }

            return _altQueue.FirstOrDefault(request =>
                string.Equals(
                    ResolveCanonicalAltMainLocked(request.Target),
                    target,
                    StringComparison.OrdinalIgnoreCase));
        }

        private void RequeueAltLookupLocked(
            string target,
            bool keepRetrying,
            string reason)
        {
            if (FindAltRequestLocked(target) != null)
                return;

            _altQueue.Add(new AltLookupRequest
            {
                Target = ResolveCanonicalAltMainLocked(target),
                Force = false,
                KeepRetrying = keepRetrying,
                Reason = reason,
                NotBeforeUtc = DateTime.UtcNow,
                Waiters = new List<AltLookupWaiter>()
            });
        }

        private void BeginSendAltLookup(AltLookupRequest request)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    uint botId;
                    if (!TryResolveCharacterId(_altsBotName, out botId))
                        throw new InvalidOperationException(
                            $"unable to resolve {_altsBotName}'s character ID");

                    lock (_altsSync)
                    {
                        if (_altsShuttingDown)
                        {
                            _altSendInFlight = false;
                            return;
                        }

                        _pendingAltLookup = request;
                        request.SentUtc = DateTime.UtcNow;
                        request.DeadlineUtc = request.SentUtc.AddSeconds(
                            AltReplyTimeoutSeconds);
                        _altSendInFlight = false;
                    }

                    Client.SendPrivateMessage(botId, $"alts {request.Target}");
                    Logger.Information(
                        $"ALTS -> {_altsBotName}: alts {request.Target}");
                    DevTrace(
                        $"ALTS -> {_altsBotName} target={request.Target} " +
                        $"reason={request.Reason}; timeout=30s.");
                }
                catch (Exception ex)
                {
                    HandleAltSendFailure(request, ex.Message);
                }
            });
        }

        private void HandleAltSendFailure(AltLookupRequest request, string error)
        {
            lock (_altsSync)
            {
                _altSendInFlight = false;
                if (ReferenceEquals(_pendingAltLookup, request))
                    _pendingAltLookup = null;

                _nextAltRequestUtc = DateTime.UtcNow.AddSeconds(
                    AltReplyTimeoutSeconds);

                if (request.KeepRetrying)
                    RequeueAltLookupLocked(request.Target, true, "retry");
            }

            Logger.Warning($"Alt lookup send failed: {error}");
            DevTrace(
                $"ALTS {_altsBotName} unavailable target={request.Target}: {error}; " +
                "cached answer retained.");
            ReplyToAltWaiters(
                request,
                $"Unable to ask {_altsBotName} right now. " +
                BuildCachedAltSummary(request.Target));
        }

        private bool TryHandleAltsBotTell(PrivateMessage message)
        {
            string botName;
            lock (_altsSync)
                botName = _altsBotName;

            if (string.IsNullOrWhiteSpace(botName) ||
                !string.Equals(
                    message.SenderName,
                    botName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string main;
            List<string> characters;
            int declaredCount;
            if (!TryParseAltReply(
                    message.Message,
                    out main,
                    out characters,
                    out declaredCount))
            {
                Logger.Information(
                    $"Ignoring non-alt reply from configured bot {botName}.");
                DevTrace($"ALTS <- {botName}: reply was not an alt list.");
                return true;
            }

            AltLookupRequest completed;
            string afterFingerprint;
            lock (_altsSync)
            {
                AltLookupRequest pending = _pendingAltLookup;
                bool matchesPending = pending != null &&
                    (string.Equals(
                         pending.Target,
                         main,
                         StringComparison.OrdinalIgnoreCase) ||
                     characters.Contains(
                         pending.Target,
                         StringComparer.OrdinalIgnoreCase));

                UpdateAltGroupFromBotLocked(main, characters, DateTime.UtcNow);
                TrySaveAltsLocked();
                PruneAndCanonicalizeAltQueueLocked();
                completed = matchesPending ? pending : null;
                if (matchesPending)
                {
                    _pendingAltLookup = null;
                    _nextAltRequestUtc = DateTime.UtcNow.AddSeconds(
                        AltSuccessfulRequestSpacingSeconds);
                }
                afterFingerprint = GetAltFingerprintLocked(main);
            }

            CanonicalizeKnownLists();

            Logger.Information(
                $"ALTS <- {botName}: main={main}, parsed={characters.Count}, " +
                $"declared={declaredCount}.");
            DevTrace(
                $"ALTS <- {botName} main={main} parsed={characters.Count} " +
                $"declared={declaredCount}; cache saved.");

            if (completed != null)
            {
                foreach (AltLookupWaiter waiter in completed.Waiters)
                {
                    bool changed = !string.Equals(
                        waiter.BeforeFingerprint,
                        afterFingerprint,
                        StringComparison.Ordinal);
                    Reply(
                        waiter.Target,
                        $"{botName}: {BuildCachedAltSummary(main)} " +
                        (changed ? "The cache was updated." : "This matches the cache."));
                }
            }

            return true;
        }

        private bool TryParseAltReply(
            string message,
            out string main,
            out List<string> characters,
            out int declaredCount)
        {
            main = null;
            characters = new List<string>();
            declaredCount = 0;

            Match heading = AltHeadingRegex.Match(message ?? string.Empty);
            if (!heading.Success ||
                !int.TryParse(heading.Groups[2].Value, out declaredCount))
            {
                return false;
            }

            string normalizedMain;
            string error;
            if (!TryNormalizeAltName(heading.Groups[1].Value, out normalizedMain, out error))
                return false;

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                normalizedMain
            };

            foreach (Match match in AltCharacterRegex.Matches(message ?? string.Empty))
            {
                string normalized;
                if (TryNormalizeAltName(match.Groups[1].Value, out normalized, out error))
                    unique.Add(normalized);
            }

            main = normalizedMain;
            characters = unique
                .OrderBy(name =>
                    string.Equals(
                        name,
                        normalizedMain,
                        StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return true;
        }

        private void UpdateAltGroupFromBotLocked(
            string main,
            List<string> characters,
            DateTime observedUtc)
        {
            var returned = new HashSet<string>(
                characters ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase)
            {
                main
            };

            var carriedAdds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var carriedRemovals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (AltGroup group in _altGroups.Values.ToList())
            {
                if (returned.Contains(group.Main))
                {
                    carriedAdds.UnionWith(group.AddedCharacters ?? new List<string>());
                    carriedRemovals.UnionWith(group.RemovedCharacters ?? new List<string>());
                    _altGroups.Remove(group.Main);
                    continue;
                }

                RemoveNames(group.ObservedCharacters, returned);
                RemoveNames(group.AddedCharacters, returned);
                RemoveNames(group.RemovedCharacters, returned);
            }

            AltGroup target;
            if (!_altGroups.TryGetValue(main, out target))
            {
                target = new AltGroup
                {
                    Main = main,
                    ObservedCharacters = new List<string>(),
                    AddedCharacters = new List<string>(),
                    RemovedCharacters = new List<string>()
                };
            }

            carriedAdds.UnionWith(target.AddedCharacters ?? new List<string>());
            carriedRemovals.UnionWith(target.RemovedCharacters ?? new List<string>());
            target.Main = main;
            target.ObservedCharacters = SortedAltNames(returned);
            target.AddedCharacters = SortedAltNames(carriedAdds);
            target.RemovedCharacters = SortedAltNames(carriedRemovals);
            target.LastUpdatedUtc = observedUtc;
            _altGroups[main] = target;
            RebuildAltLookupLocked();
        }

        private bool TryMutateAltGroup(
            string mainCandidate,
            string alternateCandidate,
            bool add,
            out string message)
        {
            string main;
            string alternate;
            string error;
            if (!TryNormalizeAltName(mainCandidate, out main, out error) ||
                !TryNormalizeAltName(alternateCandidate, out alternate, out error))
            {
                message = error;
                return false;
            }

            lock (_altsSync)
            {
                main = ResolveCanonicalAltMainLocked(main);

                AltGroup group;
                if (!_altGroups.TryGetValue(main, out group))
                {
                    if (!add)
                    {
                        message = $"No cached alt group exists for {main}.";
                        return false;
                    }

                    group = new AltGroup
                    {
                        Main = main,
                        ObservedCharacters = new List<string> { main },
                        AddedCharacters = new List<string>(),
                        RemovedCharacters = new List<string>()
                    };
                    _altGroups[main] = group;
                }

                if (string.Equals(main, alternate, StringComparison.OrdinalIgnoreCase))
                {
                    message = "A main character cannot be added to or removed from its own group.";
                    return false;
                }

                List<string> before = GetEffectiveAltCharactersLocked(group);

                if (add)
                {
                    string previousMain;
                    if (_altToMain.TryGetValue(alternate, out previousMain) &&
                        !string.Equals(previousMain, main, StringComparison.OrdinalIgnoreCase))
                    {
                        MergeAltGroupIntoLocked(previousMain, group);
                    }

                    RemoveName(group.RemovedCharacters, alternate);
                    AddUnique(group.AddedCharacters, alternate);
                }
                else
                {
                    if (!before.Contains(alternate, StringComparer.OrdinalIgnoreCase))
                    {
                        message = $"{alternate} is not cached as an alt of {main}.";
                        return false;
                    }

                    RemoveName(group.AddedCharacters, alternate);
                    AddUnique(group.RemovedCharacters, alternate);
                }

                NormalizeAltGroupLocked(group);
                RebuildAltLookupLocked();

                try
                {
                    SaveAltsLocked();
                }
                catch (Exception ex)
                {
                    try
                    {
                        LoadAltsLocked();
                    }
                    catch
                    {
                    }

                    message = $"Alt cache was not changed: {ex.Message}";
                    return false;
                }

                message = add
                    ? $"Added {alternate} as an alt of {main}."
                    : $"Removed {alternate} from the alt group of {main}.";
                return true;
            }
        }

        private void MergeAltGroupIntoLocked(string sourceMain, AltGroup destination)
        {
            AltGroup source;
            if (!_altGroups.TryGetValue(sourceMain, out source) ||
                ReferenceEquals(source, destination))
            {
                return;
            }

            foreach (string character in GetEffectiveAltCharactersLocked(source))
            {
                if (!string.Equals(character, destination.Main, StringComparison.OrdinalIgnoreCase))
                    AddUnique(destination.AddedCharacters, character);
            }

            _altGroups.Remove(sourceMain);
        }

        private bool IsAdministrator(string characterName)
        {
            return GetAltIdentityCandidates(characterName)
                .Any(AdminListStore.Contains);
        }

        private bool IsBanned(string characterName)
        {
            return GetAltIdentityCandidates(characterName)
                .Any(BanListStore.Contains);
        }

        private string ResolveCanonicalAltMain(string characterName)
        {
            string normalized;
            string error;
            if (!TryNormalizeAltName(characterName, out normalized, out error))
                return (characterName ?? string.Empty).Trim();

            lock (_altsSync)
                return ResolveCanonicalAltMainLocked(normalized);
        }

        private string ResolveCanonicalAltMainLocked(string normalizedName)
        {
            string main;
            return _altToMain.TryGetValue(normalizedName, out main)
                ? main
                : normalizedName;
        }

        private List<string> GetAltIdentityCandidates(string characterName)
        {
            string normalized;
            string error;
            if (!TryNormalizeAltName(characterName, out normalized, out error))
                return new List<string>();

            lock (_altsSync)
            {
                string main = ResolveCanonicalAltMainLocked(normalized);
                AltGroup group;
                if (!_altGroups.TryGetValue(main, out group))
                    return new List<string> { normalized };

                return GetEffectiveAltCharactersLocked(group);
            }
        }

        private void CanonicalizeKnownLists()
        {
            List<string> canonicalAdmins = AdminListStore.Snapshot()
                .Select(ResolveCanonicalAltMain)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool changed;
            string message;
            if (!AdminListStore.TryReplace(
                    canonicalAdmins,
                    out changed,
                    out message))
            {
                Logger.Warning($"Unable to canonicalize administrator list: {message}");
            }
            else if (changed)
            {
                DevTrace(
                    $"ADMIN LIST canonicalized to mains: " +
                    $"{string.Join(", ", AdminListStore.Snapshot())}.");
            }

            List<string> canonicalBans = BanListStore.Snapshot()
                .Select(ResolveCanonicalAltMain)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!BanListStore.TryReplace(
                    canonicalBans,
                    out changed,
                    out message))
            {
                Logger.Warning($"Unable to canonicalize ban list: {message}");
            }
            else if (changed)
            {
                DevTrace(
                    $"BAN LIST canonicalized to mains: " +
                    $"{string.Join(", ", BanListStore.Snapshot())}.");
            }

            CanonicalizePermanentMembersFromAlts();
        }

        private string BuildCachedAltSummary(string characterName)
        {
            string normalized;
            string error;
            if (!TryNormalizeAltName(characterName, out normalized, out error))
                return error;

            lock (_altsSync)
            {
                string main = ResolveCanonicalAltMainLocked(normalized);
                AltGroup group;
                if (!_altGroups.TryGetValue(main, out group))
                    return $"No cached alt group for {normalized}.";

                List<string> characters = GetEffectiveAltCharactersLocked(group);
                string age = group.LastUpdatedUtc.HasValue
                    ? $", read {FormatDuration(DateTime.UtcNow - group.LastUpdatedUtc.Value)} ago"
                    : ", manual cache";
                return
                    $"Cached alts of {group.Main} ({characters.Count}{age}): " +
                    $"{string.Join(", ", characters)}.";
            }
        }

        private string BuildAltStatusSummary()
        {
            lock (_altsSync)
            {
                string service = string.IsNullOrWhiteSpace(_altsBotName)
                    ? "disabled"
                    : _pendingAltLookup != null
                        ? $"waiting for {_altsBotName}"
                        : $"configured as {_altsBotName}";
                return $"Alts = {service}, {_altGroups.Count} cached groups";
            }
        }

        private string GetAltFingerprint(string characterName)
        {
            lock (_altsSync)
                return GetAltFingerprintLocked(characterName);
        }

        private string GetAltFingerprintLocked(string characterName)
        {
            string main = ResolveCanonicalAltMainLocked(characterName);
            AltGroup group;
            if (!_altGroups.TryGetValue(main, out group))
                return string.Empty;

            return group.Main.ToLowerInvariant() + ":" + string.Join(",", GetEffectiveAltCharactersLocked(group)
                .Select(name => name.ToLowerInvariant()));
        }

        private void ReplyToAltWaiters(AltLookupRequest request, string message)
        {
            if (request?.Waiters == null)
                return;

            foreach (AltLookupWaiter waiter in request.Waiters)
                Reply(waiter.Target, message);
        }

        private bool IsAltGroupFreshLocked(string target, DateTime now)
        {
            string main = ResolveCanonicalAltMainLocked(target);
            AltGroup group;
            return _altGroups.TryGetValue(main, out group) &&
                   group.LastUpdatedUtc.HasValue &&
                   now < group.LastUpdatedUtc.Value.AddHours(AltRefreshHours);
        }

        private int GetAltGroupCount()
        {
            lock (_altsSync)
                return _altGroups.Count;
        }

        private void LoadAltsLocked()
        {
            PersistedAltState state = JsonConvert.DeserializeObject<PersistedAltState>(
                File.ReadAllText(_altsPath));

            if (state == null || state.Version != AltStateVersion || state.Groups == null)
                throw new InvalidDataException("Unsupported alt-cache file.");

            _altGroups.Clear();
            foreach (AltGroup group in state.Groups)
            {
                if (group == null)
                    continue;

                string normalized;
                string error;
                if (!TryNormalizeAltName(group.Main, out normalized, out error))
                    throw new InvalidDataException(error);

                group.Main = normalized;
                NormalizeAltGroupLocked(group);
                _altGroups[group.Main] = group;
            }

            RebuildAltLookupLocked();
        }

        private void SaveAltsLocked()
        {
            if (string.IsNullOrWhiteSpace(_altsPath))
                throw new InvalidOperationException("Alt storage is not initialized.");

            var state = new PersistedAltState
            {
                Version = AltStateVersion,
                Groups = _altGroups.Values
                    .OrderBy(group => group.Main, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            string tempPath = _altsPath + ".tmp";
            File.WriteAllText(
                tempPath,
                JsonConvert.SerializeObject(state, Formatting.Indented));

            if (File.Exists(_altsPath))
                File.Delete(_altsPath);

            File.Move(tempPath, _altsPath);
        }

        private void TrySaveAltsLocked()
        {
            try
            {
                SaveAltsLocked();
            }
            catch (Exception ex)
            {
                Logger.Error($"Unable to save alt cache {_altsPath}: {ex.Message}");
            }
        }

        private void PreserveInvalidAltsFileLocked()
        {
            if (!File.Exists(_altsPath))
                return;

            try
            {
                string backup =
                    _altsPath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(_altsPath, backup);
                Logger.Warning($"Preserved invalid alt cache as {backup}.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Unable to preserve invalid alt cache: {ex.Message}");
            }
        }

        private void NormalizeAltGroupLocked(AltGroup group)
        {
            group.ObservedCharacters = NormalizeAltNames(group.ObservedCharacters);
            group.AddedCharacters = NormalizeAltNames(group.AddedCharacters);
            group.RemovedCharacters = NormalizeAltNames(group.RemovedCharacters);
            AddUnique(group.ObservedCharacters, group.Main);
            RemoveName(group.RemovedCharacters, group.Main);
        }

        private void RebuildAltLookupLocked()
        {
            _altToMain.Clear();
            foreach (AltGroup group in _altGroups.Values
                .OrderBy(value => value.Main, StringComparer.OrdinalIgnoreCase))
            {
                foreach (string character in GetEffectiveAltCharactersLocked(group))
                    _altToMain[character] = group.Main;
            }
        }

        private void PruneAndCanonicalizeAltQueueLocked()
        {
            var kept = new List<AltLookupRequest>();
            foreach (AltLookupRequest request in _altQueue)
            {
                request.Target = ResolveCanonicalAltMainLocked(request.Target);
                if (!request.Force &&
                    (request.Waiters == null || request.Waiters.Count == 0) &&
                    IsAltGroupFreshLocked(request.Target, DateTime.UtcNow))
                {
                    continue;
                }

                AltLookupRequest existing = kept.FirstOrDefault(item =>
                    string.Equals(
                        item.Target,
                        request.Target,
                        StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    kept.Add(request);
                    continue;
                }

                if (request.Waiters != null)
                    existing.Waiters.AddRange(request.Waiters);
                existing.Force = existing.Force || request.Force;
                existing.KeepRetrying =
                    existing.KeepRetrying || request.KeepRetrying;
            }

            _altQueue.Clear();
            _altQueue.AddRange(kept.OrderByDescending(request => request.Force));
        }

        private List<string> GetEffectiveAltCharactersLocked(AltGroup group)
        {
            var names = new HashSet<string>(
                group.ObservedCharacters ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            names.UnionWith(group.AddedCharacters ?? new List<string>());
            names.ExceptWith(group.RemovedCharacters ?? new List<string>());
            names.Add(group.Main);
            return SortedAltNames(names, group.Main);
        }

        private static List<string> NormalizeAltNames(IEnumerable<string> names)
        {
            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in names ?? Enumerable.Empty<string>())
            {
                string name;
                string error;
                if (!TryNormalizeAltName(candidate, out name, out error))
                    throw new InvalidDataException(error);
                normalized.Add(name);
            }
            return SortedAltNames(normalized);
        }

        private static List<string> SortedAltNames(
            IEnumerable<string> names,
            string main = null)
        {
            return (names ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name =>
                    main != null &&
                    string.Equals(name, main, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void RemoveNames(
            List<string> destination,
            HashSet<string> names)
        {
            if (destination == null)
                return;
            destination.RemoveAll(name => names.Contains(name));
        }

        private static void AddUnique(List<string> names, string value)
        {
            if (names == null || names.Contains(value, StringComparer.OrdinalIgnoreCase))
                return;
            names.Add(value);
        }

        private static void RemoveName(List<string> names, string value)
        {
            if (names == null)
                return;
            names.RemoveAll(name =>
                string.Equals(name, value, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryNormalizeAltName(
            string characterName,
            out string normalized,
            out string error)
        {
            string value = (characterName ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 30)
            {
                normalized = value;
                error = "Character name must be between 1 and 30 characters.";
                return false;
            }

            foreach (char character in value)
            {
                if (!char.IsLetterOrDigit(character))
                {
                    normalized = value;
                    error = "Character names may contain only letters and digits.";
                    return false;
                }
            }

            normalized = value.Length == 1
                ? value.ToUpperInvariant()
                : char.ToUpperInvariant(value[0]) + value.Substring(1).ToLowerInvariant();
            error = null;
            return true;
        }

        private static bool IsRemoveVerb(string verb)
        {
            return string.Equals(verb, "del", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(verb, "rem", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(verb, "remove", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(verb, "delete", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class AltBotConfig
        {
            public string Bot;
        }

        private sealed class PersistedAltState
        {
            public int Version;
            public List<AltGroup> Groups;
        }

        private sealed class AltGroup
        {
            public string Main;
            public List<string> ObservedCharacters;
            public List<string> AddedCharacters;
            public List<string> RemovedCharacters;
            public DateTime? LastUpdatedUtc;
        }

        private sealed class AltLookupRequest
        {
            public string Target;
            public string Reason;
            public bool Force;
            public bool KeepRetrying;
            public DateTime NotBeforeUtc;
            public DateTime SentUtc;
            public DateTime DeadlineUtc;
            public List<AltLookupWaiter> Waiters;
        }

        private sealed class AltLookupWaiter
        {
            public ReplyTarget Target;
            public string BeforeFingerprint;
        }
    }
}
