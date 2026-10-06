# Building Ship Shuffle

## Requirements

- Windows with Windows PowerShell 5.1 or PowerShell 7, and a .NET SDK capable of building the `net471` project.
- .NET Framework 4.7.1 reference assemblies, available through the developer pack or compatible SDK tooling.
- A local Sailwind installation and BepInExPack 5.4.2305.

Game and loader assemblies are build references only. They are not included in this repository. Configuration Manager is optional at runtime and is not needed to build.

## Build

From the project directory, run:

```powershell
powershell -NoProfile -File .\Build.ps1 -GameManagedDir 'C:\Games\Sailwind\Sailwind_Data\Managed' -ReferenceDir 'C:\Games\Sailwind\BepInEx\core'
```

Use the paths for your own installation or mod profile. Alternatively, place `BepInEx.dll` and `0Harmony.dll` in `.local/references/` and omit `-ReferenceDir`. Without either, the script falls back to the default r2modman profile's `BepInEx/core`.

The Release output is `bin/Release/net471/ShipShuffle.dll`. Copy it into `BepInEx/plugins/ShipShuffle/` to test.

The build script does not install the mod or change the game.

## Gameplay checks

Follow [Testing Ship Shuffle](TESTING.md) for boat discovery, placement, purchase and save/load checks.

## Package

Run from the project directory:

```powershell
powershell -NoProfile -File .\Package.ps1 -GameManagedDir 'C:\Games\Sailwind\Sailwind_Data\Managed' -ReferenceDir 'C:\Games\Sailwind\BepInEx\core'
```

The script builds Release and creates `artifacts/ShipShuffle-<version>.zip`, suitable for Thunderstore or manual installation. It checks the DLL, manifest and changelog versions and requires a 256 x 256 PNG icon. The ZIP contains the plugin under `BepInEx/plugins/ShipShuffle/`, plus the README, changelog, license, manifest and icon at its root.

Use `-SkipBuild` to package an already-built Release DLL, or `-Force` to replace an existing ZIP. PowerShell 7 can run either script with `pwsh` in place of `powershell`. Packaging does not install or publish the mod.
