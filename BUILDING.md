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
compiles the projects. The unified host and five plugins build into one
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

Runtime dependency versions are maintained in `Directory.Build.props`. Restored packages live in the developer's global NuGet
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
reproducible. Keep the administrator settings file when cleaning the runtime. Mutable state
lives in MySQL. Existing completed databases upgrade at startup; see
[storage operations](docs/MYSQL_MIGRATION.md). Preserve existing data.

## Portable runtime layout

The output directory is the complete City Dwellers runtime and is independent
of the Git checkout after it has been built. It may be a normal directory or a
Windows directory link to durable storage.

The deployed files are `CityDwellers.exe`, the five plugin
DLLs, their dependencies, `citydwellers.json`, and immutable `GameData`,
`NavMeshes`, and `Buffers` assets. The `data` directory holds the catalogue, runtime log and requested diagnostic dumps.

`citydwellers.json` is the administrator bootstrap configuration, including
MySQL connection settings and the Manager, Flipper, Buddies, Bankers, Buffers,
and time-gate settings. It remains beside the executable because connecting to
SQL and signing bots in must be possible before reading mutable runtime state.

Inventory and ledger items use relational MySQL tables. Other business state
still uses the existing SQL document APIs. SQL loss stops the host.
`data/items.json`, its index, `data/citydweller.log`, and requested dumps stay
on disk. The presence of `data` is not a startup error.

The host binds the runtime root before creating AOSharp child AppDomains.
The old DataMigration utility is retired; do not run an old copy against the
current database. No database reset or reimport is required.

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

Keep the existing `MySql` configuration and completed database. Startup upgrades
the schema and schedules cleanup of redundant import archives and disposable
SQL diagnostics. See [storage operations](docs/MYSQL_MIGRATION.md). This build
does not initialize a missing database or reconstruct donor history from inventory.

Plugin DLLs are fixed parts of the unified runtime and are not administrator
settings. Manager always loads `CityManager.dll`, Flipper always loads
`CityFlipper.dll`, Buddies always loads `CityBuddies.dll`, and the banker
clients always load `CityBankers.dll` from beside `CityDwellers.exe`. Plugin
paths are not represented in `citydwellers.json`.

`Manager.Bot` belongs in `citydwellers.json`. Set it to the character name of the bot that
answers `alts <character>` tells, or leave it `null` to disable external alt
lookups. Manager loads durable alt identity state from relational SQL into RAM at
startup and writes only changed alt rows. Passive Bobsan observations remain the
normal discovery path; a known missing paged response may trigger one private
verification, and Manager queues one startup private `online` request when its
notify-off login did not produce a complete snapshot.

## Unified host and Windows service

Run `CityDwellers.exe` for an interactive console. It starts the persistent
Manager AO client, the idle Flipper and Buddies request services, and all nine
banker AO clients under one supervisor. Flipper uses an isolated AOSharp client AppDomain within the unified host;
all components remain concurrent. Flipper does not log its
character in until it receives an operation; Buddies starts zero helper AO
sessions until Manager requests them. Press ENTER or CTRL+C to stop every
component.

Runtime diagnostics are written to `data/citydweller.log` before console
filtering. Explicit `#dump` output is also on disk. Automatic incident archives
and duplicate service-event persistence are removed; optional syslog remains.

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

Before starting Manager, Flipper, Buddies, or Bankers, the host verifies the
mandatory MySQL schema and exclusive runtime lease, then obtains independent UTC from the NTP servers in
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

AO login starts are paced host-wide by Governor. For deployment compatibility,
the single global wave-size setting remains `Bankers.MaxParallelLogins`.
It is **not** a limit on configured bankers. With a value of `4`, Governor
admits four `ClientDomain.Start()` calls total across Manager, Flipper,
Buddies, Buffers and Bankers, then keeps the gate closed for one full second
before admitting another wave of up to four. Manual Flipper and banker bag-audit
modes use the same admission mechanism. Client ownership remains inside each
component; Governor only grants permission to start the AO login.

The Buddies account pool, raid population limit, and its older local startup
ceiling are separate. For example, this configures indexes `0..12`, allows
at most 12 raid-owned buddies online, and permits four Buddy startup operations
to contend for the global Governor gate at once:

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
a spare account. Existing Buddies configurations without their local `MaxParallelLogins`
receive a safe default of 4. Every account has its own serialized worker, so
different buddies can start, stop, report position, and later navigate
independently. This Buddies-local ceiling is only an additional restraint; it
cannot create extra AO starts beyond the host-wide Governor wave configured by
`Bankers.MaxParallelLogins`.

## Optional froob buffers

CityBuffers builds with the solution. Copy the complete output, including its
Buffers asset directory and Scriban dependency. See [buffer setup](docs/BUFFERS.md).
The current owner-approved dependency trial is Clientless 1.0.16 / SDK 1.0.91.
