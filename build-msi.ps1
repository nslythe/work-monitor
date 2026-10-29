<#
.SYNOPSIS
    Builds the application and packages it as a per-user MSI.

.DESCRIPTION
    Compiles "work monitor.sln" in Release, then builds the WiX installer project with the
    same version number. The resulting package installs to %LOCALAPPDATA%\Programs\Work Monitor
    and does not require administrator rights.

.PARAMETER Version
    Overrides the version taken from "work monitor\work monitor.csproj" (for example 1.0.1).

.PARAMETER Configuration
    Build configuration used for the application. Defaults to Release.

.EXAMPLE
    .\build-msi.ps1

.EXAMPLE
    .\build-msi.ps1 -Version 1.0.1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $root 'work monitor.sln'
$project = Join-Path $root 'work monitor\work monitor.csproj'
$installerProject = Join-Path $root 'installer\WorkMonitor.Installer.wixproj'
$sourceDirectory = Join-Path $root "work monitor\bin\$Configuration\net48"
$outputDirectory = Join-Path $root 'dist'

function Invoke-DotNet {
    param([string[]]$Arguments)

    & dotnet @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

if (-not $Version) {
    [xml]$projectFile = Get-Content $project
    $Version = [string]$projectFile.Project.PropertyGroup.Version

    if (-not $Version) {
        throw "No <Version> element was found in $project."
    }
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "The MSI version must have the form major.minor.build (for example 1.0.1), not '$Version'."
}

Write-Host "Building Work Monitor $Version ($Configuration)..."
Invoke-DotNet @('build', $solution, '-c', $Configuration, '-v', 'minimal', "-p:Version=$Version")

$payload = @('work monitor.exe', 'work monitor.exe.config', 'Newtonsoft.Json.dll')
foreach ($file in $payload) {
    if (-not (Test-Path (Join-Path $sourceDirectory $file))) {
        throw "Expected build output '$file' was not found in $sourceDirectory."
    }
}

Write-Host 'Building the installer...'
Invoke-DotNet @(
    'build', $installerProject,
    '-c', $Configuration,
    '-v', 'minimal',
    "-p:AppVersion=$Version",
    "-p:AppSourceDir=$sourceDirectory"
)

$builtPackage = Get-ChildItem (Join-Path $root 'installer\bin') -Filter '*.msi' -File -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $builtPackage) {
    throw 'The installer project did not produce an MSI file under installer\bin.'
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$package = Join-Path $outputDirectory "WorkMonitor-$Version-x64.msi"
Copy-Item $builtPackage.FullName $package -Force

Write-Host ''
Write-Host "Installer created: $package"
Write-Host "  size: $([math]::Round((Get-Item $package).Length / 1MB, 2)) MB"
Write-Host '  installs to: %LOCALAPPDATA%\Programs\Work Monitor (per user, no elevation)'
