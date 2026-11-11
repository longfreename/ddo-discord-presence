#requires -Version 7
<#
.SYNOPSIS
  Builds the DDO Discord Presence plugin for Dungeon Helper.

.DESCRIPTION
  Runs the tests, regenerates the Discord art (assets\discord\*.png, kept in the repository for the Discord application,
  not shipped), publishes the plugin for win-x64, strips the
  assemblies Dungeon Helper already provides, and writes build\DdoDiscordPresence_<version>.zip with the
  plugins\DdoDiscordPresence\... layout that Dungeon Helper's "install plugin from zip" accepts.

.PARAMETER Install
  Also copy the plugin into %APPDATA%\Dungeon Helper\plugins\DdoDiscordPresence. Close Dungeon Helper first, or restart
  it afterwards: a running host keeps the old copy loaded.

.PARAMETER SkipTests
  Do not run the test suite first.

.EXAMPLE
  .\build.ps1            # test + build + zip into .\build
  .\build.ps1 -Install   # ... and install into Dungeon Helper
#>
param(
    [switch]$Install,
    [switch]$SkipTests,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$name = "DdoDiscordPresence"
$proj = Join-Path $root "src\$name\$name.csproj"

[xml]$xml = Get-Content $proj
$version = ($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1

if (-not $SkipTests) {
    Write-Host "Running tests..."
    dotnet run --project (Join-Path $root "tests\$name.Tests") -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

Write-Host "Writing the Discord art..."
dotnet run --project (Join-Path $root "tools\AssetExport") -c $Configuration -- (Join-Path $root "assets\discord")
if ($LASTEXITCODE -ne 0) { throw "asset export failed" }

$out = Join-Path $root "build\plugins\$name\win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "Publishing $name $version ($Configuration)..."
dotnet publish $proj -c $Configuration -r win-x64 --no-self-contained -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Dungeon Helper ships these; a plugin must not carry its own copies (a stray log4net 2.0.15 starts a second,
# unconfigured logger that swallows every plugin log line).
$hostProvided = "VoK.Sdk.dll", "Newtonsoft.Json.dll", "log4net.dll", "System.Drawing.Common.dll",
                "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll"
foreach ($f in $hostProvided + "*.pdb", "*.xml") {
    Get-ChildItem -Path $out -Filter $f -ErrorAction SilentlyContinue | Remove-Item -Force
}
Get-ChildItem -Path $out -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force   # satellite/runtime folders

$left = Get-ChildItem $out -File | ForEach-Object Name
$expected = @("$name.dll", "$name.deps.json")
$unexpected = $left | Where-Object { $_ -notin $expected }
if ($unexpected) { throw "unexpected files in the package: $($unexpected -join ', ')" }

# Distribution layout: everything under plugins\DdoDiscordPresence (Dungeon Helper's zip reader keys off "plugins/").
$dist = Join-Path $root "build\dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
$pluginDist = Join-Path $dist "plugins\$name"
New-Item -ItemType Directory -Force (Join-Path $pluginDist "win-x64") | Out-Null
Copy-Item (Join-Path $out "*") -Destination (Join-Path $pluginDist "win-x64")
Copy-Item (Join-Path $root "README.md") (Join-Path $pluginDist "README.md")
foreach ($doc in "TERMS.md", "PRIVACY.md") { Copy-Item (Join-Path $root $doc) (Join-Path $pluginDist $doc) }

@"
DDO Discord Presence $version - a Dungeon Helper plugin by longfreename

Shows the Dungeons & Dragons Online quest or area you are in as your Discord status.

INSTALL
A. In Dungeon Helper, use "Install plugin" and pick this zip. Dungeon Helper extracts plugins\$name into
   %APPDATA%\Dungeon Helper\ and finds the plugin on its next start.
B. Or by hand: close Dungeon Helper, copy the "plugins\$name" folder from this zip into
   %APPDATA%\Dungeon Helper\plugins\ so that you have
   %APPDATA%\Dungeon Helper\plugins\$name\win-x64\$name.dll
Then start Dungeon Helper and enable "DDO Discord Presence" in its plugin list.

FIRST RUN
Nothing is shared until you say so. Click the plugin's icon (a speech bubble with a gold die) on the Dungeon Helper
toolbar: it asks once whether to show what you are playing on your Discord profile. Choose "Turn on" or "Not now";
you can change it, and each item it shows, at any time in the same panel.

NEEDS
- Discord desktop running on the same PC (not the browser version).
- In Discord: User Settings > Activity Privacy > "Share your detected activities with others" turned on.

More in README.md. Terms of use: TERMS.md. Privacy: PRIVACY.md.

UNINSTALL
Close Dungeon Helper and delete the plugins\$name folder.
"@ | Set-Content -Encoding UTF8 (Join-Path $pluginDist "INSTALL.txt")

# Reproducible package: fixed timestamps on every entry, so the same sources give the same zip.
$fixed = [datetime]::new(2026, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
Get-ChildItem $dist -Recurse | ForEach-Object { $_.LastWriteTimeUtc = $fixed }

$zip = Join-Path $root "build\${name}_$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dist "plugins") -DestinationPath $zip
Write-Host "Built $zip"

if ($Install) {
    if (Get-Process -Name DungeonHelper -ErrorAction SilentlyContinue) {
        Write-Warning "Dungeon Helper is running. The new build is copied now but only loads after you restart Dungeon Helper."
    }
    $destRoot = Join-Path $env:APPDATA "Dungeon Helper\plugins\$name"
    $dest = Join-Path $destRoot "win-x64"
    New-Item -ItemType Directory -Force $dest | Out-Null
    foreach ($f in $hostProvided) { Get-ChildItem -Path $dest -Filter $f -ErrorAction SilentlyContinue | Remove-Item -Force }
    Copy-Item -Path (Join-Path $out "*") -Destination $dest -Force
    Copy-Item (Join-Path $root "README.md") (Join-Path $destRoot "README.md") -Force
    foreach ($doc in "TERMS.md", "PRIVACY.md") { Copy-Item (Join-Path $root $doc) (Join-Path $destRoot $doc) -Force }
    Write-Host "Installed to $destRoot (settings in DdoDiscordPresence.json there are kept)"
}
