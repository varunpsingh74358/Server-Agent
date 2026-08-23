#Requires -Version 5.1
<#
.SYNOPSIS
    Bootstrap installer for CloudOrc Agent - downloads CloudOrcAgentSetup.exe from a
    GitHub Release, verifies its SHA256 checksum, and runs it elevated.

.DESCRIPTION
    This is the ONLY script meant to be piped into `iex` (see the one-liner below) - it
    is small, does not execute any downloaded code as a script, and only ever launches
    the downloaded installer as a native Windows process (`Start-Process`), never via
    `Invoke-Expression` or any other script-evaluation mechanism. Read it before running
    it, exactly as you should for anything fetched from the internet and executed.

    Documented one-line usage (PowerShell, elevated):

        irm https://raw.githubusercontent.com/REPLACE_OWNER/REPLACE_REPO/main/install-cloudorc.ps1 | iex

    IMPORTANT - read this before publishing your own fork of this repository:
    `iex` evaluates a script with NO parameters, so the repository owner/name below
    cannot be supplied on the command line for that exact one-liner form. Edit
    $DefaultRepositoryOwner / $DefaultRepositoryName below to your actual GitHub
    organization/user and repository name ONCE, as part of your GitHub repository setup,
    before publishing this file. The script deliberately refuses to run against the
    literal placeholder values, rather than silently failing against a nonsense URL.

    If you'd rather not edit this file, download it and run it directly with explicit
    parameters instead - no edit needed in that case:

        .\install-cloudorc.ps1 -RepositoryOwner "your-org" -RepositoryName "CloudOrcAgent"

.PARAMETER RepositoryOwner
    GitHub organization/user that owns the repository. Required unless the placeholder
    defaults below have been edited.

.PARAMETER RepositoryName
    GitHub repository name. Required unless the placeholder defaults below have been
    edited.

.PARAMETER Version
    Release tag to install, e.g. "v1.0.0". Defaults to "latest", which uses GitHub's
    stable `releases/latest/download/<asset>` URL - this only works for a PUBLIC
    repository; see docs/INSTALLATION.md for the private-repository limitation.

.PARAMETER InstallArgs
    Arguments passed to CloudOrcAgentSetup.exe. Defaults to a fully unattended install
    (Inno Setup's actual silent switches - see docs/INSTALLATION.md for why `/quiet` is
    not the correct switch for this installer). Pass an empty string ("") to run the
    normal interactive wizard instead.

.PARAMETER AllowUnverified
    Proceed even if the .sha256 checksum file could not be downloaded (e.g. an older
    release that predates checksum publishing). Off by default - the installer will not
    run without a verified checksum unless this is explicitly set.

.EXAMPLE
    .\install-cloudorc.ps1 -RepositoryOwner "your-org" -RepositoryName "CloudOrcAgent" -Version v1.0.0
#>

[CmdletBinding()]
param(
    [string]$RepositoryOwner = "",
    [string]$RepositoryName = "",
    [string]$Version = "latest",
    [string]$InstallArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
    [switch]$AllowUnverified
)

$ErrorActionPreference = "Stop"

# Edit these two lines to your actual GitHub org/user and repo name as part of your
# one-time GitHub repository setup - see docs/INSTALLATION.md. Never a real value here by
# default; the script refuses to run until either this is edited or -RepositoryOwner/
# -RepositoryName are passed explicitly.
$DefaultRepositoryOwner = "varunpsingh74358"
$DefaultRepositoryName = "Server-Agent"

function Fail {
    param([string]$Message)
    Write-Host "`nFAIL: $Message" -ForegroundColor Red
    exit 1
}

if (-not $RepositoryOwner) { $RepositoryOwner = $DefaultRepositoryOwner }
if (-not $RepositoryName) { $RepositoryName = $DefaultRepositoryName }

if ($RepositoryOwner -eq "REPLACE_OWNER" -or $RepositoryName -eq "REPLACE_REPO") {
    Fail "RepositoryOwner/RepositoryName are still the placeholder values. Either edit `$DefaultRepositoryOwner/`$DefaultRepositoryName at the top of this script to your real GitHub org/repo, or pass -RepositoryOwner and -RepositoryName explicitly."
}

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host " CloudOrc Agent - Bootstrap Installer" -ForegroundColor Cyan
Write-Host " Repository: $RepositoryOwner/$RepositoryName  Version: $Version" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$assetName = "CloudOrcAgentSetup.exe"
$checksumName = "CloudOrcAgentSetup.exe.sha256"

if ($Version -eq "latest") {
    $exeUrl = "https://github.com/$RepositoryOwner/$RepositoryName/releases/latest/download/$assetName"
    $checksumUrl = "https://github.com/$RepositoryOwner/$RepositoryName/releases/latest/download/$checksumName"
} else {
    $exeUrl = "https://github.com/$RepositoryOwner/$RepositoryName/releases/download/$Version/$assetName"
    $checksumUrl = "https://github.com/$RepositoryOwner/$RepositoryName/releases/download/$Version/$checksumName"
}

$workDir = Join-Path $env:TEMP "CloudOrcAgent-bootstrap-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
$exePath = Join-Path $workDir $assetName
$checksumPath = Join-Path $workDir $checksumName

try {
    Write-Host "`nDownloading $exeUrl"
    Invoke-WebRequest -Uri $exeUrl -OutFile $exePath -UseBasicParsing

    if (-not (Test-Path $exePath) -or (Get-Item $exePath).Length -eq 0) {
        Fail "Downloaded installer is missing or empty."
    }

    $checksumAvailable = $false
    try {
        Write-Host "Downloading $checksumUrl"
        Invoke-WebRequest -Uri $checksumUrl -OutFile $checksumPath -UseBasicParsing
        $checksumAvailable = (Test-Path $checksumPath) -and ((Get-Item $checksumPath).Length -gt 0)
    } catch {
        Write-Host "Could not download checksum file: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    if ($checksumAvailable) {
        $expected = ((Get-Content -Raw -Path $checksumPath).Trim() -split '\s+')[0].ToLowerInvariant()
        $actual = (Get-FileHash -Algorithm SHA256 -Path $exePath).Hash.ToLowerInvariant()
        if ($expected -ne $actual) {
            Fail "SHA256 MISMATCH - downloaded installer does not match the published checksum.`n  Expected: $expected`n  Actual:   $actual`nRefusing to run an installer that fails integrity verification."
        }
        Write-Host "SHA256 verified: $actual" -ForegroundColor Green
    } elseif ($AllowUnverified) {
        Write-Host "WARNING: proceeding WITHOUT checksum verification (-AllowUnverified was set)." -ForegroundColor Yellow
    } else {
        Fail "No checksum file was available to verify the download against. Pass -AllowUnverified to proceed anyway (not recommended), or use a release that publishes $checksumName."
    }

    $isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    Write-Host "`nLaunching installer $(if ($isElevated) { '(already elevated - launching directly)' } else { '(requesting UAC elevation)' }): $assetName $InstallArgs"

    # Start-Process on a native .exe - never Invoke-Expression on downloaded content.
    $startParams = @{ FilePath = $exePath; ArgumentList = $InstallArgs; PassThru = $true }
    if (-not $isElevated) {
        # Only request UAC elevation when this session is not already elevated.
        # Requesting -Verb RunAs from an ALREADY-elevated session is a confirmed source
        # of Start-Process hanging indefinitely - the elevation broker's process handle
        # is not always tracked reliably in that "elevated re-elevating" case (seen live
        # over an RDP session: the installer fully completed - files deployed, no
        # process left running, services created - while the wait below never returned).
        $startParams['Verb'] = 'RunAs'
    }
    $process = Start-Process @startParams

    # Poll instead of a bare `-Wait`, so a stuck wait (see above) is visible and
    # diagnosable instead of silently hanging forever with no output.
    $maxWaitSeconds = 900
    $waited = 0
    while (-not $process.HasExited -and $waited -lt $maxWaitSeconds) {
        Start-Sleep -Seconds 5
        $waited += 5
        if ($waited % 30 -eq 0) {
            Write-Host "Still installing... (${waited}s elapsed)"
        }
    }

    if (-not $process.HasExited) {
        Write-Host "`nWARNING: no completion signal after ${maxWaitSeconds}s. This can happen even after a" -ForegroundColor Yellow
        Write-Host "successful install due to a known Windows process-tracking limitation when" -ForegroundColor Yellow
        Write-Host "elevating from an already-elevated session. Check manually:" -ForegroundColor Yellow
        Write-Host "  sc.exe query CloudOrcControlAgent" -ForegroundColor Yellow
        Write-Host "  sc.exe query CloudOrcWatchdogAgent" -ForegroundColor Yellow
        Write-Host "If both show RUNNING, installation succeeded - this window can be closed safely." -ForegroundColor Yellow
        exit 0
    }

    $exitCode = $process.ExitCode

    if ($exitCode -eq 0) {
        Write-Host "`nInstallation completed successfully (exit code 0)." -ForegroundColor Green
    } else {
        Write-Host "`nInstaller exited with code $exitCode - see docs/INSTALLATION.md 'Troubleshooting' or C:\ProgramData\CloudOrc\...\logs\ for details." -ForegroundColor Red
    }
} finally {
    Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
}

exit $exitCode
