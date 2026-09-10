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
compiles the projects. The unified host and four plugins write into one
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

Dependency versions are maintained once for all five projects in
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
  CityBankers.dll
  citydwellers.json
  GameData\
  NavMeshes\
  data\
```

`citydwellers.json` is the one administrator settings file. It contains the
trusted-time gate plus the Manager, Flipper, Buddies, and Bankers sections
needed for all services to work.
Every cache, state file, database, generated list, request/result marker, log,
diagnostic dump, and navigation trace created by City Dwellers lives under
`data`.

At every unified-host startup, City Dwellers inventories both locations. It
logs a warning for each unknown entry and a more specific warning when a known
setting, data file, binary, or static directory is on the wrong side. The
inventory is diagnostic only: it never deletes, moves, opens, or chooses
between duplicate files. Only the copy in the documented location is used.
The bot-owned `NavigationTraces` and `diagnostic-dumps` directories are also
checked for unexpected contents.

The unified host binds this runtime root at process scope before creating any
AOSharp child AppDomain. Manager, Flipper, Buddies, Bankers, and their plugins
therefore
resolve the same settings and `data` paths even when AOSharp assigns a different
base directory to a child domain. Plugin location never changes the runtime
root.

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

On first run, City Dwellers creates one complete `citydwellers.json` template
beside the executable and exits. Fill in the `user1`, `pass1`, and `char1`
example values in its `Manager`, `Flipper`, and `Buddies` sections, and replace
the placeholder shared password and nine role mappings in `Bankers`, then start
the program again. The host rejects unchanged examples before attempting to
log in.

Examples of bot-owned files under `data` include `adminlist.json`,
`banlist.json`, `memberlist.json`, `alts.json`, cloak and raid state,
`cityflipper-cache.json`, process-coordination markers, diagnostic logs and
dumps, and `NavigationTraces`.

Plugin DLLs are fixed parts of the unified runtime and are not administrator
settings. Manager always loads `CityManager.dll`, Flipper always loads
`CityFlipper.dll`, Buddies always loads `CityBuddies.dll`, and the banker
clients always load `CityBankers.dll` from beside `CityDwellers.exe`. Plugin
paths are not represented in `citydwellers.json`.

`Manager.Bot` belongs in `citydwellers.json`. Set it to the character name of the bot that
answers `alts <character>` tells, or leave it `null` to disable external alt
lookups. Manager stores the last good answers in `data\alts.json`,
refreshes administrator identities after 24 hours, and keeps using the cache if
the alt bot is unavailable.

## Unified host and Windows service

Run `CityDwellers.exe` for an interactive console. It starts the persistent
Manager AO client, the idle Flipper and Buddies request services, and all nine
banker AO clients under one supervisor. The live-proven Flipper host runs in a
separate process so AOSharp state cannot leak between it and the persistent
Manager/Banker clients; all components remain concurrent. Flipper does not log its
character in until it receives an operation; Buddies starts zero helper AO
sessions until Manager requests them. Press ENTER or CTRL+C to stop every
component.

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

Before starting Manager, Flipper, Buddies, or Bankers, the host verifies that `data` is
writable and obtains independent UTC from the NTP servers in
`citydwellers.json`. If the system clock differs by more than the configured
limit, it asks Windows Time to rediscover/resynchronize and continues waiting
with monotonic retry timing. AO components never see the pre-gate untrusted
clock. The trusted-time portion of the configuration is:

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

The same file also requires top-level `Manager`, `Flipper`, and `Buddies`
objects. The obsolete `manager.json`, `flipper.json`, and `buddies.json` files
are ignored and reported at startup so an administrator cannot accidentally
maintain two competing copies of the same settings.

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
  "Password": "pass1"
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
