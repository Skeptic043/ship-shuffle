param(
    [string]$GameManagedDir = 'C:\Steam Games\steamapps\common\Sailwind\Sailwind_Data\Managed',
    [string]$ReferenceDir = '',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ReferenceDir)) {
    $localReferences = Join-Path $projectRoot '.local\references'
    if (Test-Path -LiteralPath (Join-Path $localReferences 'BepInEx.dll')) {
        $ReferenceDir = $localReferences
    } else {
        $ReferenceDir = Join-Path $env:APPDATA 'r2modmanPlus-local\Sailwind\profiles\Default\BepInEx\core'
    }
}

foreach ($required in @(
    (Join-Path $GameManagedDir 'Assembly-CSharp.dll'),
    (Join-Path $GameManagedDir 'UnityEngine.CoreModule.dll'),
    (Join-Path $GameManagedDir 'UnityEngine.PhysicsModule.dll'),
    (Join-Path $GameManagedDir 'UnityEngine.AnimationModule.dll'),
    (Join-Path $GameManagedDir 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $GameManagedDir 'UnityEngine.TerrainModule.dll'),
    (Join-Path $GameManagedDir 'UnityEngine.TerrainPhysicsModule.dll'),
    (Join-Path $ReferenceDir 'BepInEx.dll'),
    (Join-Path $ReferenceDir '0Harmony.dll')
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required build reference is missing: $required"
    }
}

& dotnet build (Join-Path $projectRoot 'ShipShuffle.csproj') -c $Configuration `
    "-p:GameManagedDir=$GameManagedDir" "-p:ReferenceDir=$ReferenceDir"
if ($LASTEXITCODE -ne 0) { throw "Ship Shuffle build failed with exit code $LASTEXITCODE." }
