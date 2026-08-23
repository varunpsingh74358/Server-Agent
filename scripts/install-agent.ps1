#Requires -Version 5.1
<#
.SYNOPSIS
    Installs (or upgrades) CloudOrc Control Agent + Watchdog Agent as Windows Services on
    a Windows Server, from a self-contained release package downloaded over HTTPS.

.DESCRIPTION
    TARGET-SERVER SCRIPT. Requires ONLY Windows + PowerShell 5.1+ (built into every
    supported Windows Server version) - no .NET SDK/runtime, no Git, no Visual Studio,
    no VS Code, no Node.js, no Docker, and no source code are required on this machine.
    The release package this script downloads is self-contained and carries its own
    .NET runtime.

    Downloads a release ZIP + its .sha256 checksum over HTTPS, verifies the checksum
    BEFORE extracting anything, then installs/updates both agents as Windows Services:

        CloudOrcControlAgent   -> C:\Program Files\CloudOrc\Agents\ControlAgent\
        CloudOrcWatchdogAgent  -> C:\Program Files\CloudOrc\Agents\WatchdogAgent\

    Runtime data (logs, command queue, results) always lives under
    C:\ProgramData\CloudOrc\... - this script never touches that location except to
    leave it alone; both agents create/manage it themselves on first run.

    Idempotent: running this script again (e.g. to install a newer -Version) updates the
    existing services and files in place - it does not create duplicate services, and it
    preserves each agent's existing appsettings.json / appsettings.Development.json
    (a literal file-level preserve, not a smart config merge - see docs/INSTALLATION.md).

.PARAMETER ReleaseUrl
    Direct HTTPS URL to the release ZIP asset. Use this for local/dev testing (e.g. an
    internal file share, a pre-signed private-release asset URL, or a locally-hosted
    test HTTP server) or any source other than a public GitHub Release download URL.
    Required unless -Version/-RepositoryOwner/-RepositoryName are all supplied instead.

.PARAMETER ChecksumUrl
    Direct HTTPS URL to the matching .sha256 file. Required together with -ReleaseUrl.

.PARAMETER Version
    Release tag, e.g. "v1.0.0". Combined with -RepositoryOwner/-RepositoryName to
    construct the standard GitHub Release download URLs. Ignored if -ReleaseUrl is given.

.PARAMETER RepositoryOwner
    GitHub organization/user that owns the repository, e.g. "your-org". Never hardcode a
    real value into this script - always pass it explicitly.

.PARAMETER RepositoryName
    GitHub repository name, e.g. "CloudOrcAgent".

.PARAMETER GitHubToken
    Optional. Required ONLY when downloading a release asset from a PRIVATE GitHub
    repository via -Version/-RepositoryOwner/-RepositoryName (a plain browser_download_url
    is not fetchable unauthenticated for a private repo - see docs/INSTALLATION.md,
    section "Private GitHub repository limitations"). Pass this as a runtime parameter
    (e.g. from a secret store, a CI secret, or an interactive prompt) - never hardcode a
    token into this file, a wrapper script, or source control. When set, this script uses
    the GitHub REST API asset-download flow instead of a plain HTTPS GET.

.PARAMETER InstallRoot
    Base directory both agents are installed under. Defaults to
    "C:\Program Files\CloudOrc\Agents" per the standard layout.

.PARAMETER SkipHealthCheck
    Skip the post-install health check (the install still succeeds/fails on its own
    steps regardless).

.EXAMPLE
    .\install-agent.ps1 -Version v1.0.0 -RepositoryOwner "your-org" -RepositoryName "CloudOrcAgent"
    Standard installation/upgrade from a public (or already-authenticated) GitHub Release.

.EXAMPLE
    .\install-agent.ps1 -ReleaseUrl "https://internal-mirror.example/CloudOrcAgents-win-x64.zip" -ChecksumUrl "https://internal-mirror.example/CloudOrcAgents-win-x64.sha256"
    Install from an internal mirror / pre-authorized URL - no GitHub involved at all.

.EXAMPLE
    .\install-agent.ps1 -Version v1.0.0 -RepositoryOwner "your-org" -RepositoryName "CloudOrcAgent" -GitHubToken $env:CLOUDORC_INSTALL_TOKEN
    Install from a PRIVATE GitHub repository release, authenticating via a token supplied
    at runtime (never stored in this script).
#>

[CmdletBinding()]
param(
    [string]$ReleaseUrl = "",
    [string]$ChecksumUrl = "",
    [string]$Version = "",
    [string]$RepositoryOwner = "",
    [string]$RepositoryName = "",
    [string]$GitHubToken = "",
    [string]$InstallRoot = "C:\Program Files\CloudOrc\Agents",
    [switch]$SkipHealthCheck
)

$ErrorActionPreference = "Stop"
$PackageAssetName = "CloudOrcAgents-win-x64.zip"
$ChecksumAssetName = "CloudOrcAgents-win-x64.sha256"
$ControlAgentServiceName = "CloudOrcControlAgent"
$WatchdogServiceName = "CloudOrcWatchdogAgent"

function Fail {
    param([string]$Message)
    Write-Host "`nFAIL: $Message" -ForegroundColor Red
    exit 1
}

function Step {
    param([string]$Message)
    Write-Host "`n>>> $Message" -ForegroundColor Cyan
}

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host " CloudOrc Windows Agents - Installer" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

# ----------------------------------------------------------------------------
# 1. Preconditions: Administrator + Windows x64
# ----------------------------------------------------------------------------
Step "Verifying prerequisites"

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail "This script must be run from an elevated (Administrator) PowerShell session."
}
Write-Host "  Administrator privileges: OK"

if ($env:OS -ne "Windows_NT") {
    Fail "This installer only supports Windows."
}
if (-not [Environment]::Is64BitOperatingSystem) {
    Fail "This installer requires a 64-bit (x64) Windows Server - the release package is win-x64 only."
}
Write-Host "  Windows x64: OK ($([Environment]::OSVersion.VersionString))"

# ----------------------------------------------------------------------------
# 2. Resolve download URLs
# ----------------------------------------------------------------------------
Step "Resolving release download URLs"

$usingGitHubApi = $false
$githubAssetIds = $null

if ($ReleaseUrl -and $ChecksumUrl) {
    Write-Host "  Using explicit -ReleaseUrl / -ChecksumUrl."
} elseif ($Version -and $RepositoryOwner -and $RepositoryName) {
    $ReleaseUrl = "https://github.com/$RepositoryOwner/$RepositoryName/releases/download/$Version/$PackageAssetName"
    $ChecksumUrl = "https://github.com/$RepositoryOwner/$RepositoryName/releases/download/$Version/$ChecksumAssetName"
    Write-Host "  Constructed from -Version/-RepositoryOwner/-RepositoryName:"
    Write-Host "    Release:  $ReleaseUrl"
    Write-Host "    Checksum: $ChecksumUrl"

    if ($GitHubToken) {
        $usingGitHubApi = $true
        Write-Host "  -GitHubToken supplied - will use the authenticated GitHub API asset-download flow (required for a private repository)."
    } else {
        Write-Host "  No -GitHubToken supplied. This will only work if the repository is PUBLIC or the" -ForegroundColor Yellow
        Write-Host "  download URL is otherwise reachable unauthenticated. See docs/INSTALLATION.md," -ForegroundColor Yellow
        Write-Host "  section 'Private GitHub repository limitations', if this repository is private." -ForegroundColor Yellow
    }
} else {
    Fail "Provide either (-ReleaseUrl AND -ChecksumUrl), or all of (-Version AND -RepositoryOwner AND -RepositoryName)."
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ----------------------------------------------------------------------------
# 3. Download release + checksum
# ----------------------------------------------------------------------------
$workDir = Join-Path $env:TEMP "CloudOrcAgent-install-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
$zipPath = Join-Path $workDir $PackageAssetName
$checksumPath = Join-Path $workDir $ChecksumAssetName

function Get-GitHubReleaseAsset {
    <#
        Downloads a named asset from a (possibly private) GitHub Release using the REST
        API asset-download flow, which is the only mechanism that works for a private
        repository - a plain browser_download_url 404s without a browser session cookie.
        Requires an Authorization header; the token is passed in at runtime by the caller
        and is never written to disk or logged here.
    #>
    param(
        [Parameter(Mandatory)] [string]$Owner,
        [Parameter(Mandatory)] [string]$Repo,
        [Parameter(Mandatory)] [string]$Tag,
        [Parameter(Mandatory)] [string]$AssetName,
        [Parameter(Mandatory)] [string]$Token,
        [Parameter(Mandatory)] [string]$OutFile
    )

    $apiHeaders = @{
        Authorization = "Bearer $Token"
        Accept        = "application/vnd.github+json"
        "User-Agent"  = "CloudOrcAgent-Installer"
    }
    $releaseInfo = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/tags/$Tag" -Headers $apiHeaders -UseBasicParsing
    $asset = $releaseInfo.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
    if (-not $asset) {
        throw "Asset '$AssetName' was not found on release '$Tag' of $Owner/$Repo."
    }

    $downloadHeaders = @{
        Authorization = "Bearer $Token"
        Accept        = "application/octet-stream"
        "User-Agent"  = "CloudOrcAgent-Installer"
    }
    Invoke-WebRequest -Uri $asset.url -Headers $downloadHeaders -OutFile $OutFile -UseBasicParsing
}

Step "Downloading release package"
try {
    if ($usingGitHubApi) {
        Get-GitHubReleaseAsset -Owner $RepositoryOwner -Repo $RepositoryName -Tag $Version -AssetName $PackageAssetName -Token $GitHubToken -OutFile $zipPath
        Get-GitHubReleaseAsset -Owner $RepositoryOwner -Repo $RepositoryName -Tag $Version -AssetName $ChecksumAssetName -Token $GitHubToken -OutFile $checksumPath
    } else {
        Invoke-WebRequest -Uri $ReleaseUrl -OutFile $zipPath -UseBasicParsing
        Invoke-WebRequest -Uri $ChecksumUrl -OutFile $checksumPath -UseBasicParsing
    }
} catch {
    Fail "Download failed: $($_.Exception.Message)`nIf this is a private GitHub repository, see docs/INSTALLATION.md 'Private GitHub repository limitations' and pass -GitHubToken, or supply -ReleaseUrl/-ChecksumUrl pointing at an already-authorized source."
}

if (-not (Test-Path $zipPath) -or (Get-Item $zipPath).Length -eq 0) {
    Fail "Downloaded release package is missing or empty."
}
if (-not (Test-Path $checksumPath) -or (Get-Item $checksumPath).Length -eq 0) {
    Fail "Downloaded checksum file is missing or empty."
}
Write-Host "  Downloaded: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB)"

# ----------------------------------------------------------------------------
# 4. Verify SHA256 BEFORE extracting anything
# ----------------------------------------------------------------------------
Step "Verifying SHA256 checksum"

$checksumContent = (Get-Content -Raw -Path $checksumPath).Trim()
$expectedHash = ($checksumContent -split '\s+')[0].ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($expectedHash) -or $expectedHash.Length -ne 64) {
    Fail "Checksum file content is not a recognizable SHA256 hash: '$checksumContent'"
}

$actualHash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()

if ($actualHash -ne $expectedHash) {
    Fail "SHA256 MISMATCH - downloaded package does not match the published checksum.`n  Expected: $expectedHash`n  Actual:   $actualHash`nRefusing to install a package that fails integrity verification."
}
Write-Host "  SHA256 verified: $actualHash"

# ----------------------------------------------------------------------------
# 5. Extract to a temporary directory (never directly into the install path)
# ----------------------------------------------------------------------------
Step "Extracting package"
$extractDir = Join-Path $workDir "extracted"
Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

$extractedControlAgent = Join-Path $extractDir "CloudOrcAgents-win-x64\ControlAgent"
$extractedWatchdogAgent = Join-Path $extractDir "CloudOrcAgents-win-x64\WatchdogAgent"

if (-not (Test-Path (Join-Path $extractedControlAgent "CloudOrc.ControlAgent.exe"))) {
    Fail "Extracted package does not contain CloudOrc.ControlAgent.exe at the expected path."
}
if (-not (Test-Path (Join-Path $extractedWatchdogAgent "CloudOrc.WatchdogAgent.exe"))) {
    Fail "Extracted package does not contain CloudOrc.WatchdogAgent.exe at the expected path."
}
Write-Host "  Extracted and verified package layout: OK"

# ----------------------------------------------------------------------------
# 6. Install directories
# ----------------------------------------------------------------------------
$controlAgentInstallDir = Join-Path $InstallRoot "ControlAgent"
$watchdogAgentInstallDir = Join-Path $InstallRoot "WatchdogAgent"

function Test-ServiceExists {
    param([string]$Name)
    return $null -ne (Get-Service -Name $Name -ErrorAction SilentlyContinue)
}

$isUpgrade = (Test-Path $controlAgentInstallDir) -or (Test-ServiceExists $ControlAgentServiceName)
Write-Host "`n>>> $(if ($isUpgrade) { 'Upgrading existing installation' } else { 'Fresh installation' })" -ForegroundColor Cyan

# ----------------------------------------------------------------------------
# 7. Stop existing services (Watchdog first, so it doesn't react to the
#    Control Agent stopping intentionally), if present
# ----------------------------------------------------------------------------
Step "Stopping existing services (if present)"
foreach ($svc in @($WatchdogServiceName, $ControlAgentServiceName)) {
    if (Test-ServiceExists $svc) {
        Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue
        Write-Host "  Stopped '$svc'."
    } else {
        Write-Host "  '$svc' not installed yet - nothing to stop."
    }
}
Start-Sleep -Seconds 2

# ----------------------------------------------------------------------------
# 8. Preserve existing configuration (literal file preserve, not a merge),
#    then deploy the new application files
# ----------------------------------------------------------------------------
Step "Deploying application files"

function Backup-ExistingConfig {
    param([string]$InstallDir)
    $preserved = @{}
    foreach ($configFile in @("appsettings.json", "appsettings.Development.json")) {
        $path = Join-Path $InstallDir $configFile
        if (Test-Path $path) {
            $preserved[$configFile] = Get-Content -Raw -Path $path
        }
    }
    return $preserved
}

function Deploy-Agent {
    param(
        [string]$Name,
        [string]$SourceDir,
        [string]$TargetDir
    )
    $preservedConfig = Backup-ExistingConfig -InstallDir $TargetDir

    if (Test-Path $TargetDir) {
        Remove-Item -Recurse -Force $TargetDir
    }
    New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
    Copy-Item -Path (Join-Path $SourceDir "*") -Destination $TargetDir -Recurse -Force

    foreach ($configFile in $preservedConfig.Keys) {
        $path = Join-Path $TargetDir $configFile
        Set-Content -Path $path -Value $preservedConfig[$configFile] -NoNewline
        Write-Host "  Preserved existing $configFile for $Name."
    }

    Write-Host "  Deployed $Name to $TargetDir"
}

New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
Deploy-Agent -Name "Control Agent" -SourceDir $extractedControlAgent -TargetDir $controlAgentInstallDir
Deploy-Agent -Name "Watchdog Agent" -SourceDir $extractedWatchdogAgent -TargetDir $watchdogAgentInstallDir

# ----------------------------------------------------------------------------
# 9. Create or update Windows Services (idempotent - never duplicates)
# ----------------------------------------------------------------------------
Step "Creating/updating Windows Services"

function Install-OrUpdateService {
    param(
        [string]$Name,
        [string]$DisplayName,
        [string]$Description,
        [string]$ExePath
    )

    if (-not (Test-ServiceExists $Name)) {
        & sc.exe create $Name binPath= "`"$ExePath`"" DisplayName= "$DisplayName" start= auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed for '$Name' (exit code $LASTEXITCODE)." }
        Write-Host "  Created service '$Name'."
    } else {
        & sc.exe config $Name binPath= "`"$ExePath`"" start= auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed for '$Name' (exit code $LASTEXITCODE)." }
        Write-Host "  Updated existing service '$Name' (no duplicate created)."
    }

    & sc.exe description $Name "$Description" | Out-Null

    # Failure recovery: restart automatically if the process itself exits/crashes.
    # This is a Windows-Service-level "the process is gone" safety net; it is
    # complementary to (not a replacement for) the Watchdog Agent's own health-check-based
    # recovery logic, which detects an unresponsive-but-still-running Control Agent - a
    # case OS-level failure actions cannot see.
    & sc.exe failure $Name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    Write-Host "  Configured automatic startup + failure recovery for '$Name'."
}

Install-OrUpdateService -Name $ControlAgentServiceName -DisplayName "CloudOrc Control Agent" `
    -Description "Generic local PowerShell execution engine for CloudOrc." `
    -ExePath (Join-Path $controlAgentInstallDir "CloudOrc.ControlAgent.exe")

Install-OrUpdateService -Name $WatchdogServiceName -DisplayName "CloudOrc Watchdog Agent" `
    -Description "Monitors and recovers the CloudOrc Control Agent." `
    -ExePath (Join-Path $watchdogAgentInstallDir "CloudOrc.WatchdogAgent.exe")

# ----------------------------------------------------------------------------
# 10. Start services (Control Agent first, then Watchdog)
# ----------------------------------------------------------------------------
Step "Starting services"
Start-Service -Name $ControlAgentServiceName
Start-Sleep -Seconds 2
Start-Service -Name $WatchdogServiceName
Start-Sleep -Seconds 2

# ----------------------------------------------------------------------------
# 11. Verify + basic health check
# ----------------------------------------------------------------------------
Step "Verifying services"

$controlAgentStatus = (Get-Service -Name $ControlAgentServiceName).Status
$watchdogStatus = (Get-Service -Name $WatchdogServiceName).Status

Write-Host "  $ControlAgentServiceName : $controlAgentStatus"
Write-Host "  $WatchdogServiceName : $watchdogStatus"

$installOk = ($controlAgentStatus -eq "Running") -and ($watchdogStatus -eq "Running")
if (-not $installOk) {
    Fail "One or both services failed to reach the Running state. Check C:\ProgramData\CloudOrc\ControlAgent\logs\ and C:\ProgramData\CloudOrc\WatchdogAgent\logs\ for the reason, or run scripts\health-check.ps1."
}

if (-not $SkipHealthCheck) {
    Step "Running basic health check"
    $scriptRootForHealthCheck = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $localHealthCheck = Join-Path $scriptRootForHealthCheck "health-check.ps1"
    if (Test-Path $localHealthCheck) {
        & $localHealthCheck
    } else {
        Write-Host "  scripts\health-check.ps1 not found alongside this script - skipping the detailed check." -ForegroundColor Yellow
        Write-Host "  (Service status above already confirms both agents are Running.)"
    }
}

# ----------------------------------------------------------------------------
# 12. Cleanup + summary
# ----------------------------------------------------------------------------
Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue

Write-Host "`n======================================================================" -ForegroundColor Green
Write-Host " Installation summary" -ForegroundColor Green
Write-Host "======================================================================" -ForegroundColor Green
Write-Host " Mode:                 $(if ($isUpgrade) { 'Upgrade' } else { 'Fresh install' })"
Write-Host " Control Agent path:   $controlAgentInstallDir"
Write-Host " Watchdog Agent path:  $watchdogAgentInstallDir"
Write-Host " Control Agent service: $controlAgentStatus"
Write-Host " Watchdog Agent service: $watchdogStatus"
Write-Host " Data/logs:            C:\ProgramData\CloudOrc\ControlAgent\ and \WatchdogAgent\"
Write-Host " Next steps:           review appsettings.json in each install path for"
Write-Host "                       AgentId/ServerId/BackendConnection.Url before"
Write-Host "                       connecting to a real backend - see docs\DEPLOYMENT.md"
Write-Host "======================================================================" -ForegroundColor Green

exit 0
