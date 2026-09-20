using System;
using System.IO;
using CityDwellers.Shared;

namespace CityManager
{
    public partial class CityManager
    {
        private readonly object _shutdownSync = new object();
        private bool _shutdownRequested;

        private void BeginShutdown(string senderName, string[] parts, ReplyTarget target, bool isAdmin)
        {
            if (parts.Length != 1)
            {
                Reply(target, Usage(target, "shutdown"));
                return;
            }

            Action<string> authorized = approvedAuthority => PublishShutdown(senderName, target, approvedAuthority);
            Action<string> denied = detail =>
            {
                RecordDiagnostic("SHUTDOWN DENIED actor=" + senderName +
                    "; channel=" + target.Kind + "; reason=" + detail);
                Reply(target, "Shutdown requires Squad Commander rank or higher, or administrator access. " + detail);
            };
            if (isAdmin)
            {
                authorized("named administrator");
                return;
            }

            string authority;
            string character;
            if (TryGetCachedOfficerAuthority(senderName, out authority, out character) &&
                (string.Equals(senderName, character, StringComparison.OrdinalIgnoreCase) ||
                 IsAltIdentityGroupReliable(senderName)))
            {
                authorized(authority + " via " + character);
                return;
            }

            if (HasCachedOfficialRanks())
            {
                if (IsAltIdentityGroupReliable(senderName))
                {
                    denied("No officer authority in the verified alt group.");
                    return;
                }
                Reply(target, "Checking your current alt group for shutdown authority.");
                ResolveOfficerAltGroup(senderName, succeeded =>
                {
                    string refreshedAuthority;
                    string refreshedCharacter;
                    if (succeeded && TryGetCachedOfficerAuthority(senderName,
                        out refreshedAuthority, out refreshedCharacter))
                        authorized(refreshedAuthority + " via " + refreshedCharacter);
                    else
                        denied(succeeded ? "No officer authority found." : "Alt verification failed.");
                });
                return;
            }

            OrgRankAuthorizer.Authorize(target.SenderId, senderName, result =>
            {
                if (result.Allowed) authorized(result.Rank + " via " + senderName);
                else denied(result.Error ?? "No verified officer rank.");
            });
        }

        private void PublishShutdown(string senderName, ReplyTarget target, string authority)
        {
            lock (_shutdownSync)
            {
                if (_shutdownRequested || SqlFile.Exists(Path.Combine(_dataDir, ShutdownControl.RequestFile)))
                {
                    Reply(target, "City Dwellers shutdown is already requested.");
                    return;
                }

                var request = new ShutdownControl.Request
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Actor = senderName,
                    SenderId = target.SenderId,
                    Authority = authority,
                    Channel = target.Kind.ToString(),
                    RequestedUtc = DateTime.UtcNow
                };
                try
                {
                    SaveState();
                    ShutdownControl.Audit(_dataDir, request, "authorized-request");
                    ShutdownControl.Publish(_dataDir, request);
                    _shutdownRequested = true;
                }
                catch (Exception ex)
                {
                    RecordDiagnostic("SHUTDOWN HANDOFF FAILED actor=" + senderName + "; " + ex.Message);
                    Reply(target, "Shutdown was not accepted: " + ex.Message);
                    return;
                }

                RecordDiagnostic("SHUTDOWN ACCEPTED actor=" + senderName +
                    "; authority=" + authority + "; channel=" + target.Kind +
                    "; request=" + request.Id + "; stopping ALL City Dwellers components; no automatic restart.");
                Reply(target, "Shutdown accepted. All City Dwellers bots are stopping. A manual start is required to bring them back.");
            }
        }
    }
}
