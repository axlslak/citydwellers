# Froob buffers

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
  "Behavior": {}
}
```

Missing `Buffers`, or `Enabled: false`, starts no buffers. Individual entries can
also be disabled. Restart the host after configuration changes. Each enabled
entry needs a separate froob account; duplicate accounts/characters and accounts
already assigned to other City Dwellers services are rejected. Start with one
character; add one froob of each profession as they become ready. Paid characters
and their shared Flipper account are not supported by this first integration.

Each character gets a clientless AppDomain/update loop, remains online until host
shutdown, and uses the existing host log pipeline. Lines include the character
and CityBuffers assembly names. Static game data is preloaded under the same
mutex as the other clients. No separate TestClient or Mali clientless fork is used.

Mali discovers profession and known nanos from the logged-in character. Its
BuffsDb supplies tags and casting rules, not a configured per-character list.
Casting, team handling and buff queues remain Mali's. In game, stand near the
buffer and try `/tell Yourbuffer help`, then use its menu or `cast <tag>`.
The inherited startup delay is 30 seconds after entering play.

Use `#buffers` through Manager (or tell Manager `buffers`) to query each configured
buffer over City Dwellers local IPC. It reports readiness, known nano count and
buff queue length. Manager's existing command authorization still applies.
This is an initial status connection, not Manager-mediated buff requests or an
offline paid-buff catalogue. Mali's separate AOSharp IPC still coordinates its
froob casting group; its default channel is 255.

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
packaged assets. All froobs in the group must use the same IPC channel.
Mutable bans/ranks live in the SQL namespace `buffers/<character>/BanList.json` and
`UserRanks.json`. Rank lists start empty; add trusted character names to `Admin`
or `Moderator` while the host is stopped if Mali administrative commands are
needed. These permissions are Mali's and are separate from Manager permissions.
Bans received over Mali IPC are saved to that character's own state file.

## Dependency trial

This integration uses official AOSharp.Clientless 1.0.16 and the owner-approved
AOSharpSDK 1.0.91 trial. The previous repository baseline was SDK 1.0.84; the
historical ChatHeader.Size failure involved a newer SDK and does not establish
1.0.91 compatibility either way. No assistant build or live test was performed.
Use a full solution rebuild and deploy its complete output rather than mixing
old and new dependency DLLs. Report compile or runtime errors for correction.

Composite/duplicate buff balancing, paid scheduling, paid catalogue discovery
and Manager-mediated buff selection remain later work. Paid bots must never be
selected for froob requests when that layer is introduced.

Scriban is pinned to 7.4.0 in the shared project configuration for the plugin and
host, replacing the imported 5.7.0 version reported by NuGet audit. Its package
targets .NET Standard 2.0, compatible with this .NET Framework 4.8 solution;
shared supporting package versions meet its dependency floors. See the
[official package metadata](https://www.nuget.org/packages/Scriban/7.4.0) and
[System.Text.Json dependency metadata](https://www.nuget.org/packages/System.Text.Json/10.0.8).
After pulling build corrections, reload the solution and rebuild all projects.
