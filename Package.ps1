param(
    [string]$GameManagedDir = 'C:\Steam Games\steamapps\common\Sailwind\Sailwind_Data\Managed',
    [string]$ReferenceDir = '',
    [string]$OutputDirectory = 'artifacts',
    [switch]$SkipBuild,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$utf8 = [System.Text.UTF8Encoding]::new($false, $true)

function Read-Utf8Text([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required package file is missing: $Path" }
    $text = $utf8.GetString([System.IO.File]::ReadAllBytes($Path)).TrimStart([char]0xFEFF)
    if ([string]::IsNullOrWhiteSpace($text)) { throw "Required package file is empty: $Path" }
    return $text
}

$manifest = Read-Utf8Text (Join-Path $projectRoot 'manifest.json') | ConvertFrom-Json
foreach ($field in @('name', 'version_number', 'website_url', 'description')) {
    if ($manifest.$field -isnot [string] -or [string]::IsNullOrWhiteSpace($manifest.$field)) {
        throw "Manifest field '$field' must be a nonempty string."
    }
}
if ($manifest.name -cne 'Ship_Shuffle') { throw 'Manifest name must be Ship_Shuffle.' }
if ($manifest.website_url -cne 'https://github.com/Skeptic043/ship-shuffle') { throw 'Manifest website must identify the Ship Shuffle repository.' }
if ($manifest.description.Length -gt 250) { throw 'Manifest description must be at most 250 characters.' }
if ($manifest.version_number -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Manifest version must have three numeric components.' }
if ($manifest.dependencies -isnot [System.Array] -or $manifest.dependencies.Count -ne 1 -or
    $manifest.dependencies[0] -cne 'BepInEx-BepInExPack-5.4.2305') {
    throw 'Manifest dependencies must contain only BepInEx-BepInExPack-5.4.2305.'
}

$pluginSource = Read-Utf8Text (Join-Path $projectRoot 'src\Plugin.cs')
$versionMatches = [regex]::Matches($pluginSource, 'public\s+const\s+string\s+Version\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"\s*;')
if ($versionMatches.Count -ne 1 -or $versionMatches[0].Groups[1].Value -cne $manifest.version_number) {
    throw 'Manifest version does not match the canonical Plugin.Version.'
}
$version = $manifest.version_number
$null = Read-Utf8Text (Join-Path $projectRoot 'README.md')
$changelog = Read-Utf8Text (Join-Path $projectRoot 'CHANGELOG.md')
$heading = [regex]::Match($changelog, '(?m)^##[ \t]+(?:\[)?(?<version>[0-9]+\.[0-9]+\.[0-9]+)(?:\])?(?:[ \t]|\r?$)')
if (-not $heading.Success -or $heading.Groups['version'].Value -cne $version) {
    throw 'The latest version heading in CHANGELOG.md must match Plugin.Version.'
}
$licensePath = Join-Path $projectRoot 'LICENSE'
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf) -or (Get-Item -LiteralPath $licensePath).Length -eq 0) {
    throw 'The package LICENSE is missing or empty.'
}

$iconPath = Join-Path $projectRoot 'icon.png'
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) { throw 'The package icon.png is missing.' }
$icon = [System.IO.File]::ReadAllBytes($iconPath)
$signature = @(137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82)
if ($icon.Length -lt 33) { throw 'icon.png must have a valid PNG signature and IHDR header.' }
for ($i = 0; $i -lt $signature.Count; ++$i) {
    if ($icon[$i] -ne $signature[$i]) { throw 'icon.png must have a valid PNG signature and IHDR header.' }
}
$width = [uint32]$icon[16] * 16777216 + [uint32]$icon[17] * 65536 + [uint32]$icon[18] * 256 + $icon[19]
$height = [uint32]$icon[20] * 16777216 + [uint32]$icon[21] * 65536 + [uint32]$icon[22] * 256 + $icon[23]
if ($width -ne 256 -or $height -ne 256) { throw 'icon.png must be 256 x 256 pixels.' }

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { throw 'OutputDirectory must not be empty.' }
if (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $projectRoot $OutputDirectory }
$packagePath = Join-Path $OutputDirectory ("ShipShuffle-$version.zip")
if ((Test-Path -LiteralPath $packagePath) -and -not $Force) { throw "Package already exists. Use -Force to replace it: $packagePath" }

if (-not $SkipBuild) {
    & (Join-Path $projectRoot 'Build.ps1') -GameManagedDir $GameManagedDir -ReferenceDir $ReferenceDir -Configuration Release
}
$dllPath = Join-Path $projectRoot 'bin\Release\net471\ShipShuffle.dll'
if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) { throw "Release DLL is missing: $dllPath" }
$fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath).FileVersion
if ($fileVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:\.0)?$') { throw 'Release DLL has missing or invalid file-version metadata. Rebuild it.' }
$dllVersion = [version]$fileVersion
$expectedVersion = [version]$version
if ($dllVersion.Major -ne $expectedVersion.Major -or $dllVersion.Minor -ne $expectedVersion.Minor -or
    $dllVersion.Build -ne $expectedVersion.Build -or $dllVersion.Revision -gt 0) {
    throw "Release DLL file version '$fileVersion' does not match Plugin.Version '$version'. Rebuild it."
}

$inventory = @(
    @{ Name = 'README.md'; Path = Join-Path $projectRoot 'README.md' },
    @{ Name = 'CHANGELOG.md'; Path = Join-Path $projectRoot 'CHANGELOG.md' },
    @{ Name = 'LICENSE'; Path = $licensePath },
    @{ Name = 'manifest.json'; Path = Join-Path $projectRoot 'manifest.json' },
    @{ Name = 'icon.png'; Path = $iconPath },
    @{ Name = 'BepInEx/plugins/ShipShuffle/ShipShuffle.dll'; Path = $dllPath }
)

Add-Type -AssemblyName System.IO.Compression
$null = [System.IO.Directory]::CreateDirectory($OutputDirectory)
$temporaryPath = Join-Path $OutputDirectory ('.ShipShuffle-' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    $stream = [System.IO.File]::Open($temporaryPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($item in $inventory) {
                $entry = $archive.CreateEntry($item.Name, [System.IO.Compression.CompressionLevel]::Optimal)
                $inputStream = [System.IO.File]::OpenRead($item.Path)
                try {
                    $entryStream = $entry.Open()
                    try { $inputStream.CopyTo($entryStream) } finally { $entryStream.Dispose() }
                } finally { $inputStream.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
    if (Test-Path -LiteralPath $packagePath) {
        if (-not $Force) { throw "Package already exists: $packagePath" }
        [System.IO.File]::Replace($temporaryPath, $packagePath, [System.Management.Automation.Language.NullString]::Value)
    } else {
        [System.IO.File]::Move($temporaryPath, $packagePath)
    }
} finally {
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) { [System.IO.File]::Delete($temporaryPath) }
}

Write-Output "Package: $packagePath"
Write-Output ('SHA256: ' + (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash)
Write-Output 'Entries:'
foreach ($item in $inventory) { Write-Output ('  ' + $item.Name) }
