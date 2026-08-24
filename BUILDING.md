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
compiles the projects. Output is written to `bin\Release`.

From a Visual Studio Developer PowerShell, the equivalent command is:

```powershell
msbuild citydwellers.sln -restore -property:Configuration=Release
```

If automatic restore has been disabled, right-click the solution and select
**Restore NuGet Packages** before building.

Dependency versions are maintained once for all six projects in
`Directory.Build.props`. Restored packages live in the developer's global NuGet
cache rather than in this repository or at a hard-coded filesystem path.

### AOSharp.Clientless GameData

The AOSharp.Clientless 1.0.16 NuGet package omits five runtime data files that
are present in its source project. They are required to resolve static dynels,
including the city controller used by Flipper.

When the Flipper project is built, City Dwellers automatically downloads the
matching files from the pinned AOSharp.Clientless source revision, verifies
their SHA-256 hashes, caches them under `.dependencies`, and copies them to
`bin\Release\GameData`. The first build therefore requires access to GitLab.
Later builds reuse the verified cache, including after `bin\Release` is cleaned.

Both `.dependencies` and `bin\Release` are disposable. Deleting either is safe;
the next build downloads or copies the files again as needed. No separate
AOSharp.Clientless checkout or manual `GameData` copy is required.

## Settings

City Dwellers keeps credentials and persistent runtime state in the ignored
`settings` directory at the repository root. Cleaning or replacing
`bin\Release` therefore does not remove the bot configuration, administrator
list, membership cache, cloak history, or raid state.

On first run, Manager, Flipper, and Buddies create the `settings` directory and
their respective configuration templates if they do not exist. Each program
prints the exact file to edit and waits for ENTER before exiting. Fill in the
`user1`, `pass1`, and `char1` example values, then start the program again. The
programs reject unchanged examples before attempting to log in.

`cityflipper-cache.json` remains beside the Release executables because it is
disposable cached observation data. It is safe to delete with `bin\Release` and
should not be preserved in `settings`. The Flipper result, toggle-request, and
buddy-ready files are also short-lived process-coordination files and remain in
the disposable output directory.

Plugin entries may be simple filenames because all Release assemblies share
`bin\Release`:

```json
"Plugins": ["CityManager.dll"]
```

Use `CityFlipper.dll` in `flipper.json`. The `Plugins` field may be omitted
from `buddies.json`; Buddies then loads `CityBuddies.dll` automatically.
