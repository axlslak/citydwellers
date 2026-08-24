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

## Settings

City Dwellers keeps credentials and persistent runtime state in the ignored
`settings` directory at the repository root. Cleaning or replacing
`bin\Release` therefore does not remove the bot configuration, administrator
list, membership cache, cloak history, or raid state.

On first run, Manager, Flipper, and Buddies create the `settings` directory and
their respective configuration templates if they do not exist. Each program
prints the exact file to edit and waits for ENTER before exiting. Fill in the
placeholder account values, then start the program again.

Plugin entries may be simple filenames because all Release assemblies share
`bin\Release`:

```json
"Plugins": ["CityManager.dll"]
```

Use `CityFlipper.dll` in `flipper.json` and `CityBuddies.dll` in
`buddies.json`.
