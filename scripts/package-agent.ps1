#Requires -Version 5.1
<#
.SYNOPSIS
    Builds, tests, and packages CloudOrc Control Agent + Watchdog Agent into a
    self-contained win-x64 release ZIP with a SHA256 checksum.

.DESCRIPTION
    BUILD-MACHINE SCRIPT ONLY. Requires the .NET SDK on the machine running this script.
    The output it produces is self-contained and requires NO .NET SDK/runtime on the
    target Windows Server that later installs it (see scripts/install-agent.ps1).

    Produces:
        dist\CloudOrcAgents-win-x64\ControlAgent\    (complete published app)
        dist\CloudOrcAgents-win-x64\WatchdogAgent\   (complete published app)
        dist\CloudOrcAgents-win-x64.zip              (release asset)
        dist\CloudOrcAgents-win-x64.sha256           (release asset)

    Only published application output goes into the ZIP - no source code, no .sln, no
    tests, no docs, no .git, and no obj/bin intermediate files (dotnet publish never
    copies those in the first place).

    Exits non-zero and stops immediately if any verification step fails, so this is safe
    to use as a CI build gate.

.PARAMETER Configuration
    Build/publish configuration. Defaults to "Release".

.PARAMETER OutputRoot
    Directory the packaged output is written to, relative to the repo root unless an
    absolute path is given. Defaults to "dist".

.PARAMETER SkipTests
    Skip "dotnet test" (the GitHub Actions workflow already runs tests as a separate,
    earlier step - this flag avoids running them twice in CI). Local ad-hoc runs of this
    script should leave tests enabled.

.EXAMPLE
    .\scripts\package-agent.ps1
    Full local build: restore, build, test, publish both agents, zip, checksum.

.EXAMPLE
    .\scripts\package-agent.ps1 -SkipTests
    Used by CI after tests already ran as a separate workflow step.
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "dist",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

function Fail {
    param([string]$Message)
    Write-Host "FAIL: $Message" -ForegroundColor Red
    exit 1
}

function Step {
    param([string]$Message)
    Write-Host "`n>>> $Message" -ForegroundColor Cyan
}

# $PSScriptRoot is resolved here (script body), not in a parameter default expression -
# on some PowerShell hosts $PSScriptRoot is not yet populated at parameter-binding time.
$ScriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$RepoRoot = Split-Path -Parent $ScriptRoot

if (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot $OutputRoot
}

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host " CloudOrc Windows Agents - Release Packaging (BUILD MACHINE ONLY)" -ForegroundColor Cyan
Write-Host " Repo root:    $RepoRoot"
Write-Host " Output root:  $OutputRoot"
Write-Host " Configuration: $Configuration"
Write-Host "======================================================================" -ForegroundColor Cyan

# ----------------------------------------------------------------------------
# 1. Detect the solution and the two project paths FROM the solution file,
#    rather than assuming a fixed folder layout.
# ----------------------------------------------------------------------------
Step "Detecting solution and project paths"

$solutionPath = Get-ChildItem -Path $RepoRoot -Filter "*.sln" -File | Select-Object -First 1
if (-not $solutionPath) {
    Fail "No .sln file found under '$RepoRoot'."
}
Write-Host "  Solution: $($solutionPath.FullName)"

function Resolve-ProjectPathFromSolution {
    param(
        [Parameter(Mandatory)] [string] $SolutionFile,
        [Parameter(Mandatory)] [string] $ProjectFileName,
        [Parameter(Mandatory)] [string] $RepoRoot
    )

    $solutionText = Get-Content -Raw -Path $SolutionFile
    $pattern = [regex]::Escape($ProjectFileName)
    $match = [regex]::Match($solutionText, "\""([^\""]*$pattern)\""")
    if ($match.Success) {
        $relativePath = $match.Groups[1].Value -replace '\\', [System.IO.Path]::DirectorySeparatorChar
        $fullPath = Join-Path $RepoRoot $relativePath
        if (Test-Path $fullPath) {
            return (Resolve-Path $fullPath).Path
        }
    }

    # Fallback: search the repo tree directly if the .sln parse didn't resolve.
    $found = Get-ChildItem -Path $RepoRoot -Filter $ProjectFileName -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Select-Object -First 1
    if ($found) {
        return $found.FullName
    }

    return $null
}

$controlAgentProject = Resolve-ProjectPathFromSolution -SolutionFile $solutionPath.FullName -ProjectFileName "CloudOrc.ControlAgent.csproj" -RepoRoot $RepoRoot
$watchdogAgentProject = Resolve-ProjectPathFromSolution -SolutionFile $solutionPath.FullName -ProjectFileName "CloudOrc.WatchdogAgent.csproj" -RepoRoot $RepoRoot

if (-not $controlAgentProject) { Fail "Could not locate CloudOrc.ControlAgent.csproj from the solution or repo tree." }
if (-not $watchdogAgentProject) { Fail "Could not locate CloudOrc.WatchdogAgent.csproj from the solution or repo tree." }

Write-Host "  Control Agent project:  $controlAgentProject"
Write-Host "  Watchdog Agent project: $watchdogAgentProject"

# ----------------------------------------------------------------------------
# 2. Restore, build, (optionally) test
# ----------------------------------------------------------------------------
Step "dotnet restore"
& dotnet restore "$($solutionPath.FullName)"
if ($LASTEXITCODE -ne 0) { Fail "dotnet restore failed (exit code $LASTEXITCODE)." }

Step "dotnet build ($Configuration)"
& dotnet build "$($solutionPath.FullName)" -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { Fail "dotnet build failed (exit code $LASTEXITCODE)." }

if (-not $SkipTests) {
    Step "dotnet test"
    & dotnet test "$($solutionPath.FullName)" -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { Fail "dotnet test failed (exit code $LASTEXITCODE) - packaging aborted." }
} else {
    Write-Host "`n>>> Skipping dotnet test (-SkipTests) - assumed to have run as an earlier CI step." -ForegroundColor Yellow
}

# ----------------------------------------------------------------------------
# 3. Clean output root and publish both agents self-contained win-x64
# ----------------------------------------------------------------------------
Step "Cleaning output directory"
if (Test-Path $OutputRoot) {
    Remove-Item -Recurse -Force $OutputRoot
}
$packageName = "CloudOrcAgents-win-x64"
$packageRoot = Join-Path $OutputRoot $packageName
$controlAgentOut = Join-Path $packageRoot "ControlAgent"
$watchdogAgentOut = Join-Path $packageRoot "WatchdogAgent"
New-Item -ItemType Directory -Force -Path $controlAgentOut | Out-Null
New-Item -ItemType Directory -Force -Path $watchdogAgentOut | Out-Null

Step "dotnet publish Control Agent (win-x64, self-contained)"
& dotnet publish "$controlAgentProject" -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -o "$controlAgentOut"
if ($LASTEXITCODE -ne 0) { Fail "Publishing Control Agent failed (exit code $LASTEXITCODE)." }

Step "dotnet publish Watchdog Agent (win-x64, self-contained)"
& dotnet publish "$watchdogAgentProject" -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -o "$watchdogAgentOut"
if ($LASTEXITCODE -ne 0) { Fail "Publishing Watchdog Agent failed (exit code $LASTEXITCODE)." }

# ----------------------------------------------------------------------------
# 4. Verify the published output before packaging it - never trust a zero exit
#    code alone.
# ----------------------------------------------------------------------------
Step "Verifying published output"

$controlAgentExe = Join-Path $controlAgentOut "CloudOrc.ControlAgent.exe"
$watchdogAgentExe = Join-Path $watchdogAgentOut "CloudOrc.WatchdogAgent.exe"

if (-not (Test-Path $controlAgentExe)) { Fail "CloudOrc.ControlAgent.exe not found at '$controlAgentExe'." }
if (-not (Test-Path $watchdogAgentExe)) { Fail "CloudOrc.WatchdogAgent.exe not found at '$watchdogAgentExe'." }
Write-Host "  Control Agent EXE:  OK"
Write-Host "  Watchdog Agent EXE: OK"

foreach ($dir in @($controlAgentOut, $watchdogAgentOut)) {
    if (-not (Test-Path (Join-Path $dir "hostfxr.dll"))) { Fail "'$dir' is missing hostfxr.dll - this is not a self-contained publish." }
    if (-not (Test-Path (Join-Path $dir "coreclr.dll"))) { Fail "'$dir' is missing coreclr.dll - this is not a self-contained publish." }
    if (-not (Test-Path (Join-Path $dir "appsettings.json"))) { Fail "'$dir' is missing appsettings.json." }
}
Write-Host "  Self-contained runtime files present in both outputs: OK"
Write-Host "  appsettings.json present in both outputs: OK"

# ----------------------------------------------------------------------------
# 5. ZIP + SHA256
# ----------------------------------------------------------------------------
Step "Creating release ZIP"
$zipPath = Join-Path $OutputRoot "$packageName.zip"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal

if (-not (Test-Path $zipPath)) { Fail "ZIP was not created at '$zipPath'." }
$zipSize = (Get-Item $zipPath).Length
if ($zipSize -le 0) { Fail "ZIP at '$zipPath' is empty (0 bytes)." }
Write-Host "  Created: $zipPath ($([math]::Round($zipSize / 1MB, 1)) MB)"

Step "Generating SHA256 checksum"
$hash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
$checksumPath = Join-Path $OutputRoot "$packageName.sha256"
# Standard two-space sha256sum format - verifiable with either this repo's
# install-agent.ps1 or a plain `certutil`/`sha256sum` check.
"$hash  $packageName.zip" | Set-Content -Path $checksumPath -NoNewline -Encoding ascii

if (-not (Test-Path $checksumPath)) { Fail "Checksum file was not created at '$checksumPath'." }
$checksumContent = Get-Content -Raw -Path $checksumPath
if ([string]::IsNullOrWhiteSpace($checksumContent)) { Fail "Checksum file at '$checksumPath' is empty." }
Write-Host "  Created: $checksumPath"
Write-Host "  SHA256:  $hash"

Write-Host "`n======================================================================" -ForegroundColor Green
Write-Host " Packaging succeeded." -ForegroundColor Green
Write-Host " ZIP:      $zipPath"
Write-Host " Checksum: $checksumPath"
Write-Host "======================================================================" -ForegroundColor Green

exit 0
