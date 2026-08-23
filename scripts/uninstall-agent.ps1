#Requires -Version 5.1
<#
.SYNOPSIS
    Uninstalls CloudOrc Control Agent + Watchdog Agent Windows Services and application
    files from this server.

.DESCRIPTION
    Stops and removes both Windows Services and deletes the installed application binaries
    under C:\Program Files\CloudOrc\Agents\. By default it PRESERVES
    C:\ProgramData\CloudOrc\ (logs, command/result history, and each agent's
    appsettings.json) - pass -CleanupData to also delete that.

.PARAMETER InstallRoot
    Base directory both agents were installed under. Defaults to
    "C:\Program Files\CloudOrc\Agents" - must match what install-agent.ps1 used.

.PARAMETER CleanupData
    Also delete C:\ProgramData\CloudOrc\ (all logs and history for both agents,
    permanently). Off by default.

.EXAMPLE
    .\uninstall-agent.ps1
    Removes services and application files. Logs/data are retained.

.EXAMPLE
    .\uninstall-agent.ps1 -CleanupData
    Full removal: services, application files, and all logs/data.
#>

[CmdletBinding()]
param(
    [string]$InstallRoot = "C:\Program Files\CloudOrc\Agents",
    [switch]$CleanupData
)

$ErrorActionPreference = "Continue"

$ControlAgentServiceName = "CloudOrcControlAgent"
$WatchdogServiceName = "CloudOrcWatchdogAgent"

function Fail {
    param([string]$Message)
    Write-Host "`nFAIL: $Message" -ForegroundColor Red
    exit 1
}

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host " CloudOrc Windows Agents - Uninstaller" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail "This script must be run from an elevated (Administrator) PowerShell session."
}

function Test-ServiceExists {
    param([string]$Name)
    return $null -ne (Get-Service -Name $Name -ErrorAction SilentlyContinue)
}

Write-Host "`n[1/3] Stopping services (Watchdog first, then Control Agent)..." -ForegroundColor Yellow
foreach ($svc in @($WatchdogServiceName, $ControlAgentServiceName)) {
    if (Test-ServiceExists $svc) {
        Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue
        Write-Host "  Stopped '$svc'."
    } else {
        Write-Host "  '$svc' is not installed - nothing to stop."
    }
}
Start-Sleep -Seconds 2

Write-Host "`n[2/3] Removing Windows Service registrations..." -ForegroundColor Yellow
foreach ($svc in @($WatchdogServiceName, $ControlAgentServiceName)) {
    if (Test-ServiceExists $svc) {
        & sc.exe delete $svc | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Deleted service '$svc'."
        } else {
            Write-Host "  WARNING: sc.exe delete returned exit code $LASTEXITCODE for '$svc'." -ForegroundColor Yellow
        }
    }
}

Write-Host "`n[3/3] Removing installed application files..." -ForegroundColor Yellow
$controlAgentInstallDir = Join-Path $InstallRoot "ControlAgent"
$watchdogAgentInstallDir = Join-Path $InstallRoot "WatchdogAgent"

foreach ($dir in @($controlAgentInstallDir, $watchdogAgentInstallDir)) {
    if (Test-Path $dir) {
        Remove-Item -Recurse -Force $dir
        Write-Host "  Removed: $dir"
    }
}
# Remove the parent install root too, but only if it's now empty - never delete it if
# something else was placed there.
if ((Test-Path $InstallRoot) -and (-not (Get-ChildItem -Path $InstallRoot -Force -ErrorAction SilentlyContinue))) {
    Remove-Item -Force $InstallRoot
    Write-Host "  Removed empty install root: $InstallRoot"
}

if ($CleanupData) {
    $dataDir = "C:\ProgramData\CloudOrc"
    if (Test-Path $dataDir) {
        Remove-Item -Recurse -Force $dataDir
        Write-Host "  Removed ALL logs/data: $dataDir (not recoverable)"
    }
} else {
    Write-Host "  -CleanupData not specified - C:\ProgramData\CloudOrc\ (logs, history, config) is RETAINED." -ForegroundColor DarkYellow
}

Write-Host "`n======================================================================" -ForegroundColor Cyan
Write-Host " Uninstall complete." -ForegroundColor Cyan
Write-Host " Services removed:        yes"
Write-Host " Application files removed: yes"
Write-Host " Logs/data removed:       $($CleanupData.IsPresent)"
Write-Host "======================================================================" -ForegroundColor Cyan

exit 0
