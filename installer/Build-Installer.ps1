<#
.SYNOPSIS
    Builds WarriorHotkeyBridge-Setup-x64.msi from a clean self-contained publish.

.DESCRIPTION
    One command, repeatable, no manual steps:

        pwsh -File installer/Build-Installer.ps1

    The version comes from VersionPrefix in Directory.Build.props so the MSI, the executable and
    the tray "about" text can never disagree. Nothing else in the repository hard-codes a
    version number.

.PARAMETER SkipPublish
    Reuse the existing publish output instead of rebuilding it. For iterating on the .wxs only.

.PARAMETER Configuration
    Build configuration. Release unless you are deliberately packaging a debug build.
#>
[CmdletBinding()]
param(
    [switch] $SkipPublish,
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot    = Split-Path -Parent $PSScriptRoot
$Project     = Join-Path $RepoRoot 'src\WarriorHotkeyBridge\WarriorHotkeyBridge.csproj'
$Wxs         = Join-Path $PSScriptRoot 'WarriorHotkeyBridge.wxs'
$PublishDir  = Join-Path $RepoRoot "artifacts\publish\WarriorHotkeyBridge\$($Configuration.ToLowerInvariant())_win-x64"
$OutputDir   = Join-Path $RepoRoot 'artifacts\installer'
$Runtime     = 'win-x64'

# WiX is pinned to 5.0.2 on purpose. WiX v6 and v7 require accepting the Open Source
# Maintenance Fee EULA before they will build anything; v5 is plain MS-RL with no such gate.
# The .wxs uses the v4 schema namespace, which every version from v4 to v7 shares unchanged,
# so moving to v7 later needs no source edits - only `wix eula accept wix7`.
$WixVersion = '5.0.2'

function Get-ProductVersion {
    $props = Join-Path $RepoRoot 'Directory.Build.props'
    $xml = [xml](Get-Content -LiteralPath $props -Raw)

    # XPath rather than dotted property access: Directory.Build.props has several PropertyGroup
    # elements, and only one carries VersionPrefix.
    $node = $xml.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')

    if (-not $node) {
        throw "VersionPrefix not found in $props"
    }

    $version = $node.InnerText.Trim()

    # MSI stores ProductVersion in fixed-width fields: major and minor are 8 bits, build is 16.
    # Windows Installer does not reject an out-of-range value, it TRUNCATES it - 1.0.70000 is
    # stored as 1.0.4464. The Upgrade table's VersionMax carries the same string, so upgrade
    # detection then compares against a version that was never installed, and the next release is
    # treated as an unrelated product and installed alongside. Checked by value, not by digit
    # count: '^\d{1,3}' would happily accept 999.
    if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
        throw "VersionPrefix '$version' is not major.minor.build."
    }

    $major, $minor, $build = [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]

    if ($major -gt 255 -or $minor -gt 255 -or $build -gt 65535) {
        throw ("VersionPrefix '$version' exceeds the MSI ProductVersion field limits " +
               "(max 255.255.65535). Windows Installer would silently truncate it and break " +
               "upgrade detection.")
    }

    return $version
}

function Assert-WixToolset {
    $wix = Get-Command wix -ErrorAction SilentlyContinue

    if (-not $wix) {
        Write-Host "WiX not found; installing the $WixVersion global tool..." -ForegroundColor Yellow
        & dotnet tool install --global wix --version $WixVersion | Out-Host

        if ($LASTEXITCODE -ne 0) {
            throw "Failed to install the WiX global tool."
        }

        $env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
    }

    $installed = (& wix --version) -replace '\+.*', ''

    if ($installed -ne $WixVersion) {
        Write-Warning "WiX $installed is on PATH but this installer is verified against $WixVersion."
    }
}

Write-Host '=== Warrior Hotkey Bridge installer ===' -ForegroundColor Cyan

$ProductVersion = Get-ProductVersion
Write-Host "Version       : $ProductVersion"
Write-Host "Configuration : $Configuration"

Assert-WixToolset

if (-not $SkipPublish) {
    Write-Host "`n--- publish ---" -ForegroundColor Cyan

    # Cleared first so a file dropped from the project cannot survive in the payload and get
    # harvested into the MSI forever.
    if (Test-Path -LiteralPath $PublishDir) {
        Remove-Item -LiteralPath $PublishDir -Recurse -Force
    }

    & dotnet publish $Project `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        --nologo | Out-Host

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed."
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $PublishDir 'WarriorHotkeyBridge.exe'))) {
    throw "Publish output not found at $PublishDir. Run without -SkipPublish."
}

$payload = Get-ChildItem -LiteralPath $PublishDir -Recurse -File
Write-Host ("Payload       : {0} files, {1:N1} MB" -f $payload.Count, (($payload | Measure-Object Length -Sum).Sum / 1MB))

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$Msi = Join-Path $OutputDir 'WarriorHotkeyBridge-Setup-x64.msi'

Write-Host "`n--- wix build ---" -ForegroundColor Cyan

& wix build $Wxs `
    -arch x64 `
    -define "ProductVersion=$ProductVersion" `
    -define "PublishDir=$PublishDir" `
    -out $Msi | Out-Host

if ($LASTEXITCODE -ne 0) {
    throw "wix build failed."
}

$size = (Get-Item -LiteralPath $Msi).Length / 1MB
Write-Host "`nBuilt $Msi" -ForegroundColor Green
Write-Host ("Size          : {0:N1} MB" -f $size)
Write-Host ("SHA256        : {0}" -f (Get-FileHash -LiteralPath $Msi -Algorithm SHA256).Hash)
