# Buffers

CityBuffers imports Mali's buff engine into the unified CityDwellers host.
Build and run the solution as usual. The owner performs compilation and AO testing.
Deploy the complete output, including `CityBuffers.dll`, `Scriban.dll` and the
`Buffers/JSON` and `Buffers/Templates` asset folders.

Add this optional section alongside Manager, Flipper, Buddies and Bankers in the
local runtime `citydwellers.json` (replace the example values):

```json
"Buffers": {
  "Enabled": true,
  "Froobs": [
    {
      "Enabled": true,
      "Username": "your-froob-account",
      "Password": "your-local-password",
      "Character": "Yourbuffer"
    }
  ],
  "Paid": [
    {
      "Enabled": true,
      "Username": "your-paid-account",
      "Password": "your-local-password",
      "Character": "Yourhighlevelbuffer"
    }
  ],
  "Behavior": {}
}
```

Missing `Buffers`, or `Enabled: false`, starts no froob buffers. Individual
entries can also be disabled. Restart the host after configuration changes. Each
enabled `Froobs` entry needs a dedicated froob account; duplicate froob
accounts/characters and froob accounts already assigned to other City Dwellers
services are rejected. Start with one character; add one froob of each profession
as they become ready.

`Paid` is a separate credential category for high-level buffer characters. Each
enabled paid entry has its own explicit `Username`, `Password` and `Character`.
Paid entries may intentionally share a username with another paid character or
with another City Dwellers service such as Flipper; that shared-account
exclusivity will be handled by later scheduling. Paid character names must still
be unique across `Froobs` and `Paid`.

In the current implementation `Paid` is **configuration-only**. Paid entries are
not part of `BufferSettings.Active`, are not started by BuffersHost, are not
listed by `#buffers`, and cannot yet be started by buffer `#wakeup`. This is
intentional: this step records credentials only and adds no paid login,
scheduling, account arbitration, or buff behavior.

Each character gets a clientless AppDomain/update loop and normally remains online
until host shutdown. Administrators may temporarily hand one configured buffer
account back to the normal AO client without changing configuration:

- `#sleep Yourbuffer` unloads only that buffer's clientless AppDomain.
- `#wakeup Yourbuffer` recreates that buffer from its existing configuration.

This sleep state exists only in ManagerMemory for the current host lifetime. It is
not written to configuration or SQL; a full City Dwellers restart forgets it and
starts every configured enabled buffer normally. Numeric Buddy forms are unchanged:
`sleep <index>` and `wakeup <level> <index>` still use the existing Buddy rules.
Buffer lifecycle control uses ManagerMemory rather than a new named pipe.

Buffers deliberately have no separate login-burst setting. Every initial buffer
start and every `#wakeup` passes through Governor's host-wide AO login admission
gate. The wave size is the existing `Bankers.MaxParallelLogins` deployment
value; with 4, buffers share the same four starts with Manager, Flipper, Buddies
and Bankers, followed by a one-second pause before the next wave. Buffer count
is not limited by that value.

Buffer log lines include the character and CityBuffers assembly names. Static game
data is preloaded under the same mutex as the other clients. No separate TestClient
or Mali clientless fork is used.

Mali discovers profession and known nanos from the logged-in character. AOSharp.Clientless
normally supplies these from the server's uploaded-nano list. A small
`KnownNanoOverrides` map in `Buffers/JSON/Settings.json` may add a nano for a
specific character when the server omits a known/usable nano from that array; the
merged effective list is used consistently for catalogue, routing, queue admission
and final cast selection. `Kbadvy -> 268697` records the observed Veterans L33t
Transformation exception. BuffsDb supplies tags and casting rules.
Casting, team handling and buff queues remain Mali's. Direct buffer tells and
private-group commands are retired; Apcmanager owns the user-facing catalogue.
Use `#bufflist` (or tell Apcmanager `bufflist`) to open the cached capability
list. Last-known capabilities remain visible while a buffer is offline, marked
cached rather than ready. Buff tags in the list are Apcmanager command links.

The old Mali public buff actions also belong to Apcmanager now:
- `cast <tag...>` routes the requested Mali tags through the existing queue/profession engine;
- `rebuff` scans the requester's currently visible NCU buffs and re-requests the recognized ones;
- `buffmacro` scans the same NCU state and returns a `/tell Apcmanager cast ...` preset macro.

A public action is placed in bounded ManagerMemory work and is claimed atomically
by the first ready buffer that can actually resolve the requester in its local AO
player view. The claiming buffer runs Mali's existing request logic on its AO
update thread; Apcmanager remains the only public conversation surface. If no
ready buffer can see the requester, Manager reports that explicitly instead of
silently dropping the command. The inherited startup delay is 30 seconds after
entering play.

Use `#buffers` through Manager (or tell Manager `buffers`) to query each configured
buffer over the existing City Dwellers status bridge. It reports readiness, known
nano count and buff queue length. Manager's existing command authorization still
applies. This status bridge is separate from peer coordination.

Same-host buffer coordination now uses ManagerMemory for bot identity/capability,
presence, queue snapshots, cross-buffer cast routing, bans and the shared tell
queue. Mali AOSharp IPC remains active only for the team-coordination paths
(`TeamInfo`, `TeamTracker` and `RequestTeamInvite`); its default channel is 255.

All buffer tells enter the shared Manager-scheduled tell queue. Mali's own replies
remain pinned to their originating buffer so team prompts and menu links retain
the right sender. Buffers can also send ordinary shared-queue tells. If a pinned
sender is offline, other deliverable tells can continue. Manager must be running
for queued output to be delivered; no direct-send fallback bypasses pacing.

`Behavior` overrides Mali's defaults in memory, for example:

```json
"Behavior": {
  "DanceOnCast": false,
  "InitConnectionDelay": 30,
  "IPCChannelId": 255
}
```

Other original options remain in the packaged `Buffers/JSON/Settings.json`.
Use the unified `Behavior` overrides for local configuration; builds may refresh
packaged assets. The IPC channel now matters only to the retained Mali team
coordination, so all froobs participating in that team layer must still share it.
Mali's command/rank call sites remain in place, but `UserRanks.json` is retired.
CityManager publishes its effective alt-aware authority into ManagerMemory and
Mali's existing `UserRank.MeetsRank` seam reads it directly: `Admin` uses City
Dwellers administrators, `Moderator` uses City Dwellers officer/ranked authority
(Squad Commander or higher, plus administrators), and ordinary `Unranked` buffer
commands require City Dwellers membership. Mali's special `Warper` role has no
City Dwellers equivalent and is therefore false; it was not repurposed into an
unrelated organization rank. Existing legacy UserRanks records are ignored rather
than rewritten or deleted.

Buffer access now uses City Dwellers' existing Manager-owned ban authority.
CityManager expands its canonical ban list through the alt cache and publishes the
effective banned identities with the other buffer authority data in ManagerMemory.
Mali's local `BanJson`, buffer-local ban commands, ban IPC messages and
per-character `BanList.json` runtime path are retired. Existing old per-character
BanList files, if still present on disk from an earlier build, are ignored.

The optional Mali meeper detector may still request an automatic ban, but it does
not write authority itself: the request goes to Manager through ManagerMemory and
Manager applies the normal City Dwellers canonicalization, administrator protection
and `banlist.json` persistence before republishing buffer authority. Administrators
manage this authority with `ban add <character>`, `ban del|rem|remove <character>`
and `ban list|print`. Team
coordination is deliberately still Mali IPC and is the remaining migration area.

## Dependency trial

This integration uses official AOSharp.Clientless 1.0.16 and the owner-approved
AOSharpSDK 1.0.91 trial. The previous repository baseline was SDK 1.0.84; the
historical ChatHeader.Size failure involved a newer SDK and does not establish
1.0.91 compatibility either way. No assistant build or live test was performed.
Use a full solution rebuild and deploy its complete output rather than mixing
old and new dependency DLLs. Report compile or runtime errors for correction.

Composite/duplicate buff balancing, paid login/scheduling, shared-account
arbitration, paid catalogue discovery and Manager-mediated buff selection remain
later work. Declaring a character under `Paid` does not make it runnable yet.
Paid bots must never be selected for froob requests when that layer is introduced.

Scriban is pinned to 7.4.0 in the shared project configuration for the plugin and
host, replacing the imported 5.7.0 version reported by NuGet audit. Its package
targets .NET Standard 2.0, compatible with this .NET Framework 4.8 solution;
shared supporting package versions meet its dependency floors. See the
[official package metadata](https://www.nuget.org/packages/Scriban/7.4.0) and
[System.Text.Json dependency metadata](https://www.nuget.org/packages/System.Text.Json/10.0.8).
After pulling build corrections, reload the solution and rebuild all projects.
