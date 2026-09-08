# Building City Dwellers

## Requirements

- Visual Studio 2022 with the **.NET desktop development** workload
- The .NET Framework 4.8 targeting pack
- NuGet package restore enabled (the Visual Studio default)

The repository can be cloned into any directory. It does not require a separate
AOSharp or AOSharp.Clientless source checkout.

## Build

Open `citydwellers.sln`, select the `Release` configuration, and build the
solution. Visual Studio restores the pinned dependencies from NuGet before it
compiles the projects. The unified host and three plugins write into one
portable runtime root:

- `release` for a Release build;
- `debug` for a Debug build.

There is no intermediate `bin` directory in either path.

From a Visual Studio Developer PowerShell, the equivalent command is:

```powershell
msbuild citydwellers.sln -restore -property:Configuration=Release
```

If automatic restore has been disabled, right-click the solution and select
**Restore NuGet Packages** before building.

Dependency versions are maintained once for all four projects in
`Directory.Build.props`. Restored packages live in the developer's global NuGet
cache rather than in this repository or at a hard-coded filesystem path.

### AOSharp.Clientless GameData

The AOSharp.Clientless 1.0.16 NuGet package omits five runtime data files that
are present in its source project. They are required to resolve static dynels,
including the city controller used by Flipper.

When the unified host project is built, a C# bootstrap command compiled into
`CityDwellers.exe` automatically downloads the matching files from the pinned
AOSharp.Clientless source revision, verifies their SHA-256 hashes, caches them
under `.dependencies`, and copies them to the runtime `GameData` directory. The first
build therefore requires access to GitLab. Later builds reuse the verified
cache, including after the runtime output is cleaned. No PowerShell script or other
external helper is used.

The build copies GameData to `release\GameData` or `debug\GameData`. The
`.dependencies` cache and compiled/static files in the runtime root are
reproducible. Do not delete the administrator JSON files or the `data`
directory when cleaning a live runtime.

## Portable runtime layout

The output directory is the complete City Dwellers runtime and is independent
of the Git checkout after it has been built. It may be a normal directory or a
Windows directory link to durable storage.

```text
release\
  CityDwellers.exe
  CityManager.dll
  CityFlipper.dll
  CityBuddies.dll
  citydwellers.json
  manager.json
  flipper.json
  buddies.json
  GameData\
  NavMeshes\
  data\
```

The four JSON files beside the executable are administrator settings. They
contain the information an administrator must supply for the services to work.
Every cache, state file, database, generated list, request/result marker, log,
diagnostic dump, and navigation trace created by City Dwellers lives under
`data`.

On first start from the new repository-relative output, City Dwellers
conservatively imports an existing repository `settings` directory and mutable
files from the old `bin\Release` or `bin\Debug` directory. It copies a file only
when its new destination does not exist; it never deletes or overwrites the old
installation. Once the new runtime is verified, the old directories are no
longer used.

For an interactive Windows account that already has `Y:` connected, the
runtime directory can be redirected before building:

```bat
mklink /D release Y:\CityDwellers\release
```

Use `/D`, not `/J`, for a network target. Junctions are for local filesystem
targets; directory symbolic links can target a mapped drive or UNC path. A
Windows service should use a UNC-backed link and a service account that has
share access because drive-letter mappings belong to an interactive logon and
normally are not visible to services.

## Settings and data

On first run, City Dwellers creates `citydwellers.json` and the Manager,
Flipper, and Buddies configuration templates beside the executable if they do
not exist. It prints the exact file to edit and exits. Fill in the
`user1`, `pass1`, and `char1` example values, then start the program again. The
host rejects unchanged examples before attempting to log in.

Examples of bot-owned files under `data` include `adminlist.json`,
`banlist.json`, `memberlist.json`, `alts.json`, cloak and raid state,
`cityflipper-cache.json`, process-coordination markers, diagnostic logs and
dumps, and `NavigationTraces`.

Plugin entries may be simple filenames because all Release assemblies share
the same runtime root:

```json
"Plugins": ["CityManager.dll"],
"Bot": "Bobsan"
```

`Bot` belongs in `manager.json`. Set it to the character name of the bot that
answers `alts <character>` tells, or leave it `null` to disable external alt
lookups. Existing Manager configurations receive the missing optional field on
their next start. Manager stores the last good answers in `data\alts.json`,
refreshes administrator identities after 24 hours, and keeps using the cache if
the alt bot is unavailable.

Use `CityFlipper.dll` in `flipper.json`. The `Plugins` field may be omitted
from `buddies.json`; Buddies then loads `CityBuddies.dll` automatically.

## Unified host and Windows service

Run `CityDwellers.exe` for an interactive console. It starts the persistent
Manager AO client and the idle Flipper and Buddies request services inside one
supervised process. Flipper does not log its character in until it receives an
operation; Buddies starts zero helper AO sessions until Manager requests them.
Press ENTER or CTRL+C to stop all three components.

All console output is also written to `data\citydwellers.log`, including when
the executable runs without a visible desktop. The log rotates at 10 MiB to
`citydwellers.log.previous`.

From an elevated console in the durable runtime directory:

```bat
CityDwellers.exe install-service
sc.exe start CityDwellers
```

The install command creates one delayed-automatic Windows service, declares
TCP/IP and Workstation service dependencies, and configures progressive
restart-on-failure delays. Remove it with:

```bat
CityDwellers.exe uninstall-service
```

For a network-backed runtime, open `services.msc` before the first start and
set the City Dwellers service's **Log On** account to an identity with read and
write permission on the UNC share. Do not configure a mapped drive letter in
the service path or settings; mapped drives belong to interactive logon
sessions.

Before starting Manager, Flipper, or Buddies, the host verifies that `data` is
writable and obtains independent UTC from the NTP servers in
`citydwellers.json`. If the system clock differs by more than the configured
limit, it asks Windows Time to rediscover/resynchronize and continues waiting
with monotonic retry timing. AO components never see the pre-gate untrusted
clock. The default configuration is:

```json
{
  "RequireTrustedTime": true,
  "NtpServers": [
    "time.cloudflare.com",
    "time.google.com",
    "time.windows.com"
  ],
  "MinimumNtpResponses": 1,
  "MaximumClockSkewSeconds": 120,
  "NtpTimeoutMilliseconds": 3000,
  "RetrySeconds": 30,
  "WindowsResyncEverySeconds": 300
}
```

If UDP port 123 is blocked at a location, change `NtpServers` to reachable NTP
servers or deliberately set `RequireTrustedTime` to `false`. Disabling the gate
is explicit and is logged as a warning; it is never an automatic fallback.

Manual Flipper diagnostics remain available through the unified executable:

```bat
CityDwellers.exe flipper-probe
CityDwellers.exe flipper-toggle
```

The Buddies account pool, raid population limit, and simultaneous AO login
handshakes are separate. For example, this configures indexes `0..12`, allows
at most 12 raid-owned buddies online, and starts up to four AO sessions at once:

```json
{
  "AccountPrefix": "user",
  "AccountCount": 13,
  "ActiveLimit": 12,
  "MaxParallelLogins": 4,
  "Password": "pass1",
  "Plugins": null
}
```

If one of the first accounts cannot log in, raid spinup continues through the
pool and can use index 12 as its spare. Public raid selection and automatic raid
spinup remain capped by `ActiveLimit`. Administrator `wakeup` and `spinup`
commands may use the entire configured account pool, including all 13 at once
for diagnostics. Existing configurations without `ActiveLimit` receive a value
no larger than 12 automatically; increase `AccountCount` explicitly when adding
a spare account. Existing configurations without `MaxParallelLogins` receive a
safe default of 4. Every account has its own serialized worker, so different
buddies can start, stop, report position, and later navigate independently;
raise `MaxParallelLogins` only if the AO login service and machine handle the
extra simultaneous handshakes reliably.
