using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using AOSharp.Clientless;
using AOSharp.Clientless.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityManager
{
    public partial class CityManager
    {
        private const int MembershipStateVersion = 1;
        private const int MemberListVersion = 1;
        private const int MembershipRefreshHours = 24;
        private const int MembershipRetryMinutes = 60;
        private const int SuspiciousRosterShrinkPercent = 30;

        private readonly object _membershipSync = new object();
        private readonly HashSet<string> _permanentMembers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _officialMembers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _liveAddedMembers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _liveRemovedMembers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private string _memberListPath;
        private string _membershipStatePath;
        private int _membershipOrgId;
        private int _membershipDimension = 5;
        private string _membershipOrgName;
        private DateTime? _membershipLastSuccessfulFetchUtc;
        private DateTime? _membershipSourceUpdatedUtc;
        private DateTime _membershipNextAttemptUtc = DateTime.MinValue;
        private DateTime _nextMembershipTickUtc = DateTime.MinValue;
        private int _suspiciousRosterShrinkCount;
        private bool _membershipFetchInFlight;
        private bool _membershipShuttingDown;

        private void InitializeMembership()
        {
            lock (_membershipSync)
            {
                _memberListPath = Path.Combine(_settingsDir, "memberlist.json");
                _membershipStatePath =
                    Path.Combine(_settingsDir, "citymanager-membership-state.json");
                _membershipShuttingDown = false;
                _membershipFetchInFlight = false;
                _nextMembershipTickUtc = DateTime.MinValue;

                LoadPermanentMembersLocked();
                LoadMembershipStateLocked();

                _membershipNextAttemptUtc =
                    _membershipLastSuccessfulFetchUtc.HasValue
                        ? _membershipLastSuccessfulFetchUtc.Value.AddHours(MembershipRefreshHours)
                        : DateTime.MinValue;

                Logger.Information(
                    $"Membership initialized: permanent={_permanentMembers.Count}, " +
                    $"official={_officialMembers.Count}, orgId={_membershipOrgId}, " +
                    $"lastFetch={_membershipLastSuccessfulFetchUtc:O}.");
                DevTrace(
                    $"MEMBERSHIP initialized permanent={_permanentMembers.Count} " +
                    $"official={_officialMembers.Count} live-add={_liveAddedMembers.Count} " +
                    $"live-del={_liveRemovedMembers.Count} cached-org={_membershipOrgId}.");
            }
        }

        private void BeginMembershipAfterInPlay()
        {
            DetectOrganizationIdentity();
            TickMembership();
        }

        private void ShutdownMembership()
        {
            lock (_membershipSync)
            {
                _membershipShuttingDown = true;
                TrySaveMembershipStateLocked();
            }
        }

        private void TickMembership()
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextMembershipTickUtc)
                return;

            _nextMembershipTickUtc = now.AddSeconds(5);

            if (_membershipOrgId <= 0 && Client.OrgId > 0)
                DetectOrganizationIdentity();

            int orgId;
            int dimension;

            lock (_membershipSync)
            {
                if (_membershipShuttingDown ||
                    _membershipFetchInFlight ||
                    _membershipOrgId <= 0 ||
                    now < _membershipNextAttemptUtc)
                {
                    return;
                }

                _membershipFetchInFlight = true;
                orgId = _membershipOrgId;
                dimension = _membershipDimension;
            }

            DevTrace(
                $"MEMBERSHIP ROSTER -> fetch org={orgId} dimension={dimension}.");

            ThreadPool.QueueUserWorkItem(_ => FetchOfficialRoster(orgId, dimension));
        }

        private void DetectOrganizationIdentity()
        {
            int orgId = Client.OrgId;
            if (orgId <= 0)
            {
                DevTrace("MEMBERSHIP waiting for Manager's AO organization ID.");
                return;
            }

            int dimension = string.Equals(
                Client.Dimension.ToString(),
                "RubiKa2019",
                StringComparison.OrdinalIgnoreCase)
                    ? 6
                    : 5;

            SetOrganizationIdentity(orgId, dimension, Client.OrgName);
        }

        private void SetOrganizationIdentity(int orgId, int dimension, string orgName)
        {
            if (orgId <= 0)
                return;

            bool changed;
            lock (_membershipSync)
            {
                changed = _membershipOrgId != orgId ||
                          _membershipDimension != dimension;

                if (changed)
                {
                    _officialMembers.Clear();
                    _liveAddedMembers.Clear();
                    _liveRemovedMembers.Clear();
                    _membershipLastSuccessfulFetchUtc = null;
                    _membershipSourceUpdatedUtc = null;
                    _membershipNextAttemptUtc = DateTime.MinValue;
                    _suspiciousRosterShrinkCount = 0;
                }

                _membershipOrgId = orgId;
                _membershipDimension = dimension;
                if (!string.IsNullOrWhiteSpace(orgName))
                    _membershipOrgName = orgName.Trim();

                TrySaveMembershipStateLocked();
            }

            if (changed)
            {
                Logger.Information(
                    $"Detected Manager organization: id={orgId}, dimension={dimension}, name={orgName}.");
                DevTrace(
                    $"MEMBERSHIP ORG detected id={orgId} dimension={dimension} " +
                    $"name={orgName ?? "unknown"}; roster refresh due now.");
            }
        }

        private bool IsOrganizationChannel(int channelId, string channelName)
        {
            int orgId;
            lock (_membershipSync)
                orgId = _membershipOrgId;

            if (orgId > 0 && channelId == orgId)
                return true;

            bool nameMatches = string.Equals(
                channelName,
                !string.IsNullOrWhiteSpace(_membershipOrgName)
                    ? _membershipOrgName
                    : OrgChannelName,
                StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    channelName,
                    OrgChannelName,
                    StringComparison.OrdinalIgnoreCase);

            if (nameMatches && orgId <= 0 && channelId > 0)
            {
                int dimension = string.Equals(
                    Client.Dimension.ToString(),
                    "RubiKa2019",
                    StringComparison.OrdinalIgnoreCase)
                        ? 6
                        : 5;
                SetOrganizationIdentity(channelId, dimension, channelName);
            }

            return nameMatches;
        }

        private bool IsTellMember(string characterName)
        {
            string normalized;
            string error;
            if (!TryNormalizeMemberName(characterName, out normalized, out error))
                return false;

            List<string> identities = GetAltIdentityCandidates(normalized);

            lock (_membershipSync)
            {
                foreach (string identity in identities)
                {
                    if (_permanentMembers.Contains(identity))
                        return true;

                    if (_liveRemovedMembers.Contains(identity))
                        continue;

                    if (_liveAddedMembers.Contains(identity) ||
                        _officialMembers.Contains(identity))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private bool IsCommandSourceAuthorized(
            string senderName,
            string command,
            string[] parts,
            ReplyTarget target,
            bool isAdmin)
        {
            if (isAdmin || target.IsOrg || target.IsGuest)
                return true;

            if (IsTellMember(senderName))
                return true;

            return string.Equals(command, "raid", StringComparison.OrdinalIgnoreCase) &&
                   parts.Length > 1 &&
                   IsCurrentRaidOwnerCommand(senderName, target.SenderId, parts);
        }

        private void ObserveOrganizationMembershipMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            string memberName;
            string actor;

            if (TryExtractOrganizationActorTarget(
                    message,
                    " invited ",
                    " to your organization.",
                    out actor,
                    out memberName))
            {
                ApplyLiveMembershipChange(memberName, true, "invited", actor);
                return;
            }

            if (TryExtractOrganizationActorTarget(
                    message,
                    " kicked ",
                    " from your organization.",
                    out actor,
                    out memberName))
            {
                ApplyLiveMembershipChange(memberName, false, "kicked", actor);
                return;
            }

            if (TryExtractOrganizationActorTarget(
                    message,
                    " removed inactive character ",
                    " from your organization.",
                    out actor,
                    out memberName))
            {
                ApplyLiveMembershipChange(
                    memberName,
                    false,
                    "removed inactive",
                    actor);
                return;
            }

            const string leftSuffix = " just left your organization.";
            if (message.EndsWith(leftSuffix, StringComparison.OrdinalIgnoreCase))
            {
                memberName = message.Substring(0, message.Length - leftSuffix.Length).Trim();
                ApplyLiveMembershipChange(memberName, false, "left", null);
                return;
            }

            const string alignmentSuffix =
                " kicked from organization (alignment changed).";
            if (message.EndsWith(alignmentSuffix, StringComparison.OrdinalIgnoreCase))
            {
                memberName =
                    message.Substring(0, message.Length - alignmentSuffix.Length).Trim();
                ApplyLiveMembershipChange(
                    memberName,
                    false,
                    "alignment changed",
                    null);
            }
        }

        private bool TryExtractOrganizationActorTarget(
            string message,
            string separator,
            string suffix,
            out string actor,
            out string memberName)
        {
            actor = null;
            memberName = null;

            if (!message.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;

            string withoutSuffix =
                message.Substring(0, message.Length - suffix.Length);
            int separatorIndex = withoutSuffix.IndexOf(
                separator,
                StringComparison.OrdinalIgnoreCase);

            if (separatorIndex <= 0)
                return false;

            actor = withoutSuffix.Substring(0, separatorIndex).Trim();
            memberName = withoutSuffix.Substring(
                separatorIndex + separator.Length).Trim();
            return true;
        }

        private void ApplyLiveMembershipChange(
            string characterName,
            bool added,
            string reason,
            string actor)
        {
            string normalized;
            string error;
            if (!TryNormalizeMemberName(characterName, out normalized, out error))
            {
                DevTrace(
                    $"MEMBERSHIP LIVE ignored name={characterName}: {error}");
                return;
            }

            lock (_membershipSync)
            {
                if (added)
                {
                    _liveRemovedMembers.Remove(normalized);
                    if (!_officialMembers.Contains(normalized))
                        _liveAddedMembers.Add(normalized);
                }
                else
                {
                    _liveAddedMembers.Remove(normalized);
                    // Keep the removal even before the first website fetch.  The
                    // daily roster may still contain this character until AO's
                    // public data catches up.
                    _liveRemovedMembers.Add(normalized);
                }

                TrySaveMembershipStateLocked();
            }

            DevTrace(
                $"MEMBERSHIP LIVE {(added ? "ADD" : "DEL")} name={normalized} " +
                $"reason={reason} actor={actor ?? "self/system"}.");
        }

        private void FetchOfficialRoster(int orgId, int dimension)
        {
            try
            {
                string url =
                    $"https://people.anarchy-online.com/org/stats/d/{dimension}/" +
                    $"name/{orgId}/basicstats.xml?data_type=json";

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.UserAgent = "CityDwellers-Manager/1.0";
                request.Timeout = 30000;
                request.ReadWriteTimeout = 30000;
                request.AutomaticDecompression =
                    DecompressionMethods.GZip | DecompressionMethods.Deflate;

                string json;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    json = reader.ReadToEnd();

                RemoteOrganizationRoster roster = ParseOfficialRoster(json);
                ApplyOfficialRoster(orgId, dimension, roster);
            }
            catch (Exception ex)
            {
                DateTime retryAt = DateTime.UtcNow.AddMinutes(MembershipRetryMinutes);
                lock (_membershipSync)
                {
                    _membershipFetchInFlight = false;
                    if (!_membershipShuttingDown)
                        _membershipNextAttemptUtc = retryAt;
                    TrySaveMembershipStateLocked();
                }

                Logger.Warning($"Official organization roster refresh failed: {ex.Message}");
                DevTrace(
                    $"MEMBERSHIP ROSTER FAIL org={orgId}: {ex.Message}; " +
                    $"retry={retryAt:O}.");
            }
        }

        private RemoteOrganizationRoster ParseOfficialRoster(string json)
        {
            JArray root = JArray.Parse(json);
            if (root.Count < 2)
                throw new InvalidDataException("Official roster response is incomplete.");

            var organization = root[0] as JObject;
            var members = root[1] as JArray;
            if (organization == null || members == null)
                throw new InvalidDataException("Official roster response has an unexpected shape.");

            int orgId = organization.Value<int?>("ORG_INSTANCE") ?? 0;
            string orgName = organization.Value<string>("NAME");
            int declaredCount = organization.Value<int?>("NUMMEMBERS") ?? 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (JToken token in members)
            {
                string name = token.Value<string>("NAME");
                string normalized;
                string error;
                if (TryNormalizeMemberName(name, out normalized, out error))
                    names.Add(normalized);
            }

            if (orgId <= 0 || string.IsNullOrWhiteSpace(orgName) || names.Count == 0)
                throw new InvalidDataException("Official roster response contains no usable roster.");

            DateTime? sourceUpdatedUtc = null;
            if (root.Count > 2)
            {
                string sourceTimestamp = root[2].Value<string>();
                DateTime parsed;
                if (DateTime.TryParseExact(
                        sourceTimestamp,
                        new[]
                        {
                            "yyyy/MM/dd HH:mm:ss",
                            "yyyy/MM/dd HH:mm:ss 'Universal'"
                        },
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out parsed))
                {
                    sourceUpdatedUtc = parsed;
                }
            }

            return new RemoteOrganizationRoster
            {
                OrgId = orgId,
                OrgName = orgName.Trim(),
                DeclaredCount = declaredCount,
                SourceUpdatedUtc = sourceUpdatedUtc,
                Members = names
            };
        }

        private void ApplyOfficialRoster(
            int requestedOrgId,
            int requestedDimension,
            RemoteOrganizationRoster roster)
        {
            DateTime now = DateTime.UtcNow;
            string telemetry;

            lock (_membershipSync)
            {
                if (_membershipShuttingDown ||
                    _membershipOrgId != requestedOrgId ||
                    _membershipDimension != requestedDimension)
                {
                    _membershipFetchInFlight = false;
                    return;
                }

                if (roster.OrgId != requestedOrgId)
                    throw new InvalidDataException(
                        $"Official roster returned organization {roster.OrgId}, expected {requestedOrgId}.");

                bool suspiciousShrink =
                    _officialMembers.Count > 0 &&
                    roster.Members.Count * 100 <
                    _officialMembers.Count * (100 - SuspiciousRosterShrinkPercent);

                if (suspiciousShrink && _suspiciousRosterShrinkCount == 0)
                {
                    _suspiciousRosterShrinkCount = 1;
                    _membershipFetchInFlight = false;
                    _membershipNextAttemptUtc = now.AddHours(MembershipRefreshHours);
                    TrySaveMembershipStateLocked();

                    telemetry =
                        $"MEMBERSHIP ROSTER WARN rejected one-time shrink " +
                        $"old={_officialMembers.Count} new={roster.Members.Count}; " +
                        $"confirm-after={_membershipNextAttemptUtc:O}.";
                }
                else
                {
                    _officialMembers.Clear();
                    foreach (string name in roster.Members)
                        _officialMembers.Add(name);

                    _liveAddedMembers.RemoveWhere(
                        name => _officialMembers.Contains(name));
                    _liveRemovedMembers.RemoveWhere(
                        name => !_officialMembers.Contains(name));

                    _membershipOrgName = roster.OrgName;
                    _membershipLastSuccessfulFetchUtc = now;
                    _membershipSourceUpdatedUtc = roster.SourceUpdatedUtc;
                    _membershipNextAttemptUtc = now.AddHours(MembershipRefreshHours);
                    _suspiciousRosterShrinkCount = 0;
                    _membershipFetchInFlight = false;
                    TrySaveMembershipStateLocked();

                    telemetry =
                        $"MEMBERSHIP ROSTER OK org={requestedOrgId} " +
                        $"name={roster.OrgName} members={_officialMembers.Count} " +
                        $"declared={roster.DeclaredCount} " +
                        $"source-updated={roster.SourceUpdatedUtc:O} " +
                        $"next={_membershipNextAttemptUtc:O}.";
                }
            }

            Logger.Information(telemetry);
            DevTrace(telemetry);
        }

        private void ProcessMemberListCommand(
            string senderName,
            string[] parts,
            ReplyTarget target)
        {
            if (parts.Length != 1)
            {
                Reply(target, Usage(target, "memberlist"));
                return;
            }

            List<string> members;
            lock (_membershipSync)
                members = SortedNames(_permanentMembers);

            string message = members.Count == 0
                ? "Permanent members (0): none."
                : $"Permanent members ({members.Count}): {string.Join(", ", members)}.";

            DevTrace(
                $"MEMBER LIST viewed by={senderName} count={members.Count}.");
            Reply(target, message);
        }

        private void ProcessMemberCommand(
            string senderName,
            string[] parts,
            ReplyTarget target)
        {
            bool add =
                parts.Length == 3 &&
                string.Equals(parts[1], "add", StringComparison.OrdinalIgnoreCase);
            bool del =
                parts.Length == 3 &&
                IsRemoveVerb(parts[1]);

            if (!add && !del)
            {
                Reply(target, Usage(target, "member [add|del|rem|remove|delete] [character]"));
                return;
            }

            string normalized;
            string error;
            if (!TryNormalizeMemberName(
                    ResolveCanonicalAltMain(parts[2]),
                    out normalized,
                    out error))
            {
                Reply(target, error);
                return;
            }

            bool changed;
            string message;

            lock (_membershipSync)
            {
                if (add)
                {
                    if (_permanentMembers.Contains(normalized))
                    {
                        changed = false;
                        message = $"{normalized} is already a permanent member.";
                    }
                    else
                    {
                        _permanentMembers.Add(normalized);
                        try
                        {
                            SavePermanentMembersLocked();
                            changed = true;
                            message = $"Added {normalized} to the permanent member list.";
                        }
                        catch (Exception ex)
                        {
                            _permanentMembers.Remove(normalized);
                            changed = false;
                            message = $"Permanent member list was not changed: {ex.Message}";
                        }
                    }
                }
                else
                {
                    string existing = _permanentMembers.FirstOrDefault(
                        name => string.Equals(
                            name,
                            normalized,
                            StringComparison.OrdinalIgnoreCase));

                    if (existing == null)
                    {
                        changed = false;
                        message = $"{normalized} is not a permanent member.";
                    }
                    else
                    {
                        _permanentMembers.Remove(existing);
                        try
                        {
                            SavePermanentMembersLocked();
                            changed = true;
                            message = $"Removed {existing} from the permanent member list.";
                        }
                        catch (Exception ex)
                        {
                            _permanentMembers.Add(existing);
                            changed = false;
                            message = $"Permanent member list was not changed: {ex.Message}";
                        }
                    }
                }
            }

            DevTrace(
                $"MEMBER LIST {parts[1].ToUpperInvariant()} actor={senderName} " +
                $"target={normalized} changed={changed}; {message}");
            Reply(target, message);
        }

        private void CanonicalizePermanentMembersFromAlts()
        {
            List<string> current;
            lock (_membershipSync)
                current = SortedNames(_permanentMembers);

            var canonical = new HashSet<string>(
                current.Select(ResolveCanonicalAltMain),
                StringComparer.OrdinalIgnoreCase);

            lock (_membershipSync)
            {
                if (_permanentMembers.SetEquals(canonical))
                    return;

                List<string> previous = SortedNames(_permanentMembers);
                _permanentMembers.Clear();
                _permanentMembers.UnionWith(canonical);

                try
                {
                    SavePermanentMembersLocked();
                    DevTrace(
                        $"MEMBER LIST canonicalized {previous.Count} -> {_permanentMembers.Count} mains.");
                }
                catch (Exception ex)
                {
                    _permanentMembers.Clear();
                    _permanentMembers.UnionWith(previous);
                    Logger.Error($"Unable to canonicalize permanent members: {ex.Message}");
                }
            }
        }

        private void LoadPermanentMembersLocked()
        {
            _permanentMembers.Clear();

            if (!File.Exists(_memberListPath))
            {
                TrySavePermanentMembersLocked();
                return;
            }

            try
            {
                PersistedMemberList state =
                    JsonConvert.DeserializeObject<PersistedMemberList>(
                        File.ReadAllText(_memberListPath));

                if (state == null ||
                    state.Version != MemberListVersion ||
                    state.Members == null)
                {
                    throw new InvalidDataException("Unsupported permanent member-list file.");
                }

                foreach (string candidate in state.Members)
                {
                    string normalized;
                    string error;
                    if (!TryNormalizeMemberName(candidate, out normalized, out error))
                        throw new InvalidDataException(error);

                    _permanentMembers.Add(normalized);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Unable to load permanent member list: {ex.Message}");
                PreserveInvalidMembershipFileLocked(_memberListPath);
                _permanentMembers.Clear();
                TrySavePermanentMembersLocked();
            }
        }

        private void LoadMembershipStateLocked()
        {
            _officialMembers.Clear();
            _liveAddedMembers.Clear();
            _liveRemovedMembers.Clear();

            if (!File.Exists(_membershipStatePath))
                return;

            try
            {
                PersistedMembershipState state =
                    JsonConvert.DeserializeObject<PersistedMembershipState>(
                        File.ReadAllText(_membershipStatePath));

                if (state == null || state.Version != MembershipStateVersion)
                    throw new InvalidDataException("Unsupported membership-state file.");

                _membershipOrgId = state.OrgId;
                _membershipDimension = state.Dimension > 0 ? state.Dimension : 5;
                _membershipOrgName = state.OrgName;
                _membershipLastSuccessfulFetchUtc = state.LastSuccessfulFetchUtc;
                _membershipSourceUpdatedUtc = state.SourceUpdatedUtc;
                _suspiciousRosterShrinkCount = state.SuspiciousRosterShrinkCount;

                AddPersistedNamesLocked(state.OfficialMembers, _officialMembers);
                AddPersistedNamesLocked(state.LiveAddedMembers, _liveAddedMembers);
                AddPersistedNamesLocked(state.LiveRemovedMembers, _liveRemovedMembers);
            }
            catch (Exception ex)
            {
                Logger.Error($"Unable to load membership state: {ex.Message}");
                PreserveInvalidMembershipFileLocked(_membershipStatePath);
                _membershipOrgId = 0;
                _membershipDimension = 5;
                _membershipOrgName = null;
                _membershipLastSuccessfulFetchUtc = null;
                _membershipSourceUpdatedUtc = null;
                _suspiciousRosterShrinkCount = 0;
                _officialMembers.Clear();
                _liveAddedMembers.Clear();
                _liveRemovedMembers.Clear();
            }
        }

        private void AddPersistedNamesLocked(
            IEnumerable<string> names,
            HashSet<string> destination)
        {
            if (names == null)
                return;

            foreach (string candidate in names)
            {
                string normalized;
                string error;
                if (!TryNormalizeMemberName(candidate, out normalized, out error))
                    throw new InvalidDataException(error);

                destination.Add(normalized);
            }
        }

        private void SavePermanentMembersLocked()
        {
            var state = new PersistedMemberList
            {
                Version = MemberListVersion,
                Members = SortedNames(_permanentMembers)
            };

            WriteJsonAtomically(
                _memberListPath,
                JsonConvert.SerializeObject(state, Formatting.Indented));
        }

        private void TrySavePermanentMembersLocked()
        {
            try
            {
                SavePermanentMembersLocked();
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"Unable to save permanent member list {_memberListPath}: {ex.Message}");
            }
        }

        private void TrySaveMembershipStateLocked()
        {
            if (string.IsNullOrWhiteSpace(_membershipStatePath))
                return;

            try
            {
                var state = new PersistedMembershipState
                {
                    Version = MembershipStateVersion,
                    OrgId = _membershipOrgId,
                    Dimension = _membershipDimension,
                    OrgName = _membershipOrgName,
                    LastSuccessfulFetchUtc = _membershipLastSuccessfulFetchUtc,
                    SourceUpdatedUtc = _membershipSourceUpdatedUtc,
                    SuspiciousRosterShrinkCount = _suspiciousRosterShrinkCount,
                    OfficialMembers = SortedNames(_officialMembers),
                    LiveAddedMembers = SortedNames(_liveAddedMembers),
                    LiveRemovedMembers = SortedNames(_liveRemovedMembers)
                };

                WriteJsonAtomically(
                    _membershipStatePath,
                    JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Logger.Error($"Unable to save membership state: {ex.Message}");
            }
        }

        private void PreserveInvalidMembershipFileLocked(string path)
        {
            if (!File.Exists(path))
                return;

            try
            {
                string backupPath =
                    path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(path, backupPath);
                Logger.Warning($"Preserved invalid membership file as {backupPath}.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Unable to preserve invalid membership file: {ex.Message}");
            }
        }

        private void WriteJsonAtomically(string path, string json)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Membership storage is not initialized.");

            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);

            if (File.Exists(path))
                File.Delete(path);

            File.Move(tempPath, path);
        }

        private static List<string> SortedNames(IEnumerable<string> names)
        {
            return names
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryNormalizeMemberName(
            string characterName,
            out string normalized,
            out string error)
        {
            normalized = (characterName ?? string.Empty).Trim();

            if (normalized.Length == 0 || normalized.Length > 30)
            {
                error = "Member name must be between 1 and 30 characters.";
                return false;
            }

            foreach (char character in normalized)
            {
                if (!char.IsLetterOrDigit(character))
                {
                    error = "Member names may contain only letters and digits.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private sealed class PersistedMemberList
        {
            public int Version;
            public List<string> Members;
        }

        private sealed class PersistedMembershipState
        {
            public int Version;
            public int OrgId;
            public int Dimension;
            public string OrgName;
            public DateTime? LastSuccessfulFetchUtc;
            public DateTime? SourceUpdatedUtc;
            public int SuspiciousRosterShrinkCount;
            public List<string> OfficialMembers;
            public List<string> LiveAddedMembers;
            public List<string> LiveRemovedMembers;
        }

        private sealed class RemoteOrganizationRoster
        {
            public int OrgId;
            public string OrgName;
            public int DeclaredCount;
            public DateTime? SourceUpdatedUtc;
            public HashSet<string> Members;
        }
    }
}
