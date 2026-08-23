#Requires -Version 5.1
<#
.SYNOPSIS
    Reports the health of an installed CloudOrc Control Agent + Watchdog Agent
    deployment on this server.

.DESCRIPTION
    Read-only diagnostic - makes no changes. Reports OS architecture, both services'
    status, executable existence, installation/data paths, running process status, and
    recent relevant Windows service errors from the System event log (if readable).

    Exit code 0 = healthy, 1 = unhealthy (at least one required check failed).

.PARAMETER InstallRoot
    Base directory both agents are expected to be installed under. Defaults to
    "C:\Program Files\CloudOrc\Agents".

.EXAMPLE
    .\health-check.ps1
#>

[CmdletBinding()]
param(
    [string]$InstallRoot = "C:\Program Files\CloudOrc\Agents"
)

$ControlAgentServiceName = "CloudOrcControlAgent"
$WatchdogServiceName = "CloudOrcWatchdogAgent"
$ControlAgentInstallDir = Join-Path $InstallRoot "ControlAgent"
$WatchdogAgentInstallDir = Join-Path $InstallRoot "WatchdogAgent"
$ControlAgentDataDir = "C:\ProgramData\CloudOrc\ControlAgent"
$WatchdogAgentDataDir = "C:\ProgramData\CloudOrc\WatchdogAgent"

$issues = @()

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host " CloudOrc Windows Agents - Health Check" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

# ----------------------------------------------------------------------------
# OS architecture
# ----------------------------------------------------------------------------
Write-Host "`n-- OS --" -ForegroundColor Yellow
$is64Bit = [Environment]::Is64BitOperatingSystem
Write-Host "  OS:           $([Environment]::OSVersion.VersionString)"
Write-Host "  Architecture: $(if ($is64Bit) { 'x64' } else { 'x86 (UNSUPPORTED - this package is win-x64 only)' })"
if (-not $is64Bit) { $issues += "OS is not 64-bit; this package requires win-x64." }

# ----------------------------------------------------------------------------
# Installation paths
# ----------------------------------------------------------------------------
Write-Host "`n-- Installation paths --" -ForegroundColor Yellow
$controlAgentExe = Join-Path $ControlAgentInstallDir "CloudOrc.ControlAgent.exe"
$watchdogAgentExe = Join-Path $WatchdogAgentInstallDir "CloudOrc.WatchdogAgent.exe"

$controlAgentExeExists = Test-Path $controlAgentExe
$watchdogAgentExeExists = Test-Path $watchdogAgentExe

Write-Host "  Control Agent path:  $ControlAgentInstallDir"
Write-Host "  Control Agent exe:   $(if ($controlAgentExeExists) { 'FOUND' } else { 'MISSING' })"
Write-Host "  Watchdog Agent path: $WatchdogAgentInstallDir"
Write-Host "  Watchdog Agent exe:  $(if ($watchdogAgentExeExists) { 'FOUND' } else { 'MISSING' })"

if (-not $controlAgentExeExists) { $issues += "CloudOrc.ControlAgent.exe not found at '$controlAgentExe'." }
if (-not $watchdogAgentExeExists) { $issues += "CloudOrc.WatchdogAgent.exe not found at '$watchdogAgentExe'." }

# ----------------------------------------------------------------------------
# ProgramData paths
# ----------------------------------------------------------------------------
Write-Host "`n-- ProgramData paths --" -ForegroundColor Yellow
Write-Host "  Control Agent data:  $ControlAgentDataDir $(if (Test-Path $ControlAgentDataDir) { '(exists)' } else { '(not yet created - normal before first start)' })"
Write-Host "  Watchdog Agent data: $WatchdogAgentDataDir $(if (Test-Path $WatchdogAgentDataDir) { '(exists)' } else { '(not yet created - normal before first start)' })"

# ----------------------------------------------------------------------------
# Service status
# ----------------------------------------------------------------------------
Write-Host "`n-- Windows Service status --" -ForegroundColor Yellow

function Get-ServiceStatusSafe {
    param([string]$Name)
    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $svc) { return "NOT INSTALLED" }
    return $svc.Status.ToString()
}

$controlAgentStatus = Get-ServiceStatusSafe -Name $ControlAgentServiceName
$watchdogStatus = Get-ServiceStatusSafe -Name $WatchdogServiceName

Write-Host "  $ControlAgentServiceName : $controlAgentStatus"
Write-Host "  $WatchdogServiceName : $watchdogStatus"

if ($controlAgentStatus -ne "Running") { $issues += "$ControlAgentServiceName is '$controlAgentStatus', expected 'Running'." }
if ($watchdogStatus -ne "Running") { $issues += "$WatchdogServiceName is '$watchdogStatus', expected 'Running'." }

# ----------------------------------------------------------------------------
# Running process status (service Status=Running should imply a live process,
# but this double-checks against the actual OS process table)
# ----------------------------------------------------------------------------
Write-Host "`n-- Process status --" -ForegroundColor Yellow

$controlAgentProcess = Get-Process -Name "CloudOrc.ControlAgent" -ErrorAction SilentlyContinue
$watchdogProcess = Get-Process -Name "CloudOrc.WatchdogAgent" -ErrorAction SilentlyContinue

Write-Host "  CloudOrc.ControlAgent.exe process:  $(if ($controlAgentProcess) { "running (PID $($controlAgentProcess.Id))" } else { 'not running' })"
Write-Host "  CloudOrc.WatchdogAgent.exe process: $(if ($watchdogProcess) { "running (PID $($watchdogProcess.Id))" } else { 'not running' })"

if ($controlAgentStatus -eq "Running" -and -not $controlAgentProcess) {
    $issues += "$ControlAgentServiceName reports Running but no matching process was found."
}
if ($watchdogStatus -eq "Running" -and -not $watchdogProcess) {
    $issues += "$WatchdogServiceName reports Running but no matching process was found."
}

# ----------------------------------------------------------------------------
# Recent relevant Windows Service Control Manager errors (best-effort)
# ----------------------------------------------------------------------------
Write-Host "`n-- Recent related System event log entries (last 24h, best-effort) --" -ForegroundColor Yellow
try {
    $since = (Get-Date).AddHours(-24)
    $events = Get-WinEvent -FilterHashtable @{ LogName = "System"; Level = 1, 2, 3; StartTime = $since } -ErrorAction Stop |
        Where-Object { $_.Message -match "CloudOrcControlAgent|CloudOrcWatchdogAgent" } |
        Select-Object -First 10

    if ($events) {
        foreach ($e in $events) {
            Write-Host "  [$($e.TimeCreated)] $($e.LevelDisplayName): $($e.Message.Split("`n")[0])"
        }
    } else {
        Write-Host "  No related error/warning events in the last 24 hours."
    }
} catch {
    Write-Host "  Could not read the System event log (insufficient permissions or none present): $($_.Exception.Message)"
}

# ----------------------------------------------------------------------------
# Verdict
# ----------------------------------------------------------------------------
Write-Host "`n======================================================================" -ForegroundColor Cyan
if ($issues.Count -eq 0) {
    Write-Host " RESULT: HEALTHY" -ForegroundColor Green
    Write-Host "======================================================================" -ForegroundColor Cyan
    exit 0
} else {
    Write-Host " RESULT: UNHEALTHY" -ForegroundColor Red
    foreach ($issue in $issues) {
        Write-Host "  - $issue" -ForegroundColor Red
    }
    Write-Host "======================================================================" -ForegroundColor Cyan
    exit 1
}
