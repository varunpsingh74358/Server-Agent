<#
.SYNOPSIS
    Downloads the pinned backup engines (Kopia / Restic / rclone), verifies them, and writes
    engines\dist\engines.manifest.json for packaging.

.DESCRIPTION
    CO-DEV-BKP-001 section 15: "Tampered installer/update/engine binary -> Signature verification
    fails closed". Every archive listed in engines\engines.lock.json is downloaded over HTTPS and
    its SHA-256 compared with the pinned value BEFORE anything is extracted. The extracted exe's
    own SHA-256 goes into engines.manifest.json, which ships inside the signed installer and is
    re-checked by CloudOrc.BackupWorker before every engine launch.

    Engines with an empty url in the lock file are skipped. Nothing here is a secret.

.EXAMPLE
    .\scripts\fetch-engines.ps1
#>
[CmdletBinding()]
param(
    [string]$LockFile = "",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$ScriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$RepoRoot = Split-Path -Parent $ScriptRoot
if (-not $LockFile) { $LockFile = Join-Path $RepoRoot "engines\engines.lock.json" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $RepoRoot "engines\dist" }

$lock = Get-Content -Raw -Path $LockFile | ConvertFrom-Json
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("cloudorc-engines-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
if (Test-Path $OutputDirectory) { Remove-Item -Recurse -Force $OutputDirectory }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$manifest = @()
try {
    foreach ($engine in $lock.engines) {
        if ([string]::IsNullOrWhiteSpace($engine.url)) {
            Write-Host "Skipping $($engine.name): no url pinned." -ForegroundColor Yellow
            continue
        }

        if (-not $engine.url.StartsWith("https://")) { throw "$($engine.name): url must be https." }
        if ($engine.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "$($engine.name): sha256 must be pinned (64 hex chars)." }

        $fileName = [System.IO.Path]::GetFileName(([Uri]$engine.url).AbsolutePath)
        $download = Join-Path $work $fileName
        Write-Host "Downloading $($engine.name) $($engine.version)..."
        Invoke-WebRequest -Uri $engine.url -OutFile $download -UseBasicParsing

        $actual = (Get-FileHash -Algorithm SHA256 -Path $download).Hash
        if ($actual -ne $engine.sha256.ToUpperInvariant()) {
            throw "$($engine.name): SHA-256 mismatch (expected $($engine.sha256), got $actual). Refusing to package."
        }

        $exeTarget = Join-Path $OutputDirectory $engine.exe
        if ($fileName.EndsWith(".zip")) {
            $extract = Join-Path $work ($engine.name + "-x")
            Expand-Archive -Path $download -DestinationPath $extract -Force
            $found = Get-ChildItem -Path $extract -Recurse -File -Filter $engine.exe | Select-Object -First 1
            if (-not $found) { throw "$($engine.name): $($engine.exe) not found inside the archive." }
            Copy-Item $found.FullName $exeTarget -Force
        } elseif ($fileName.EndsWith(".exe")) {
            Copy-Item $download $exeTarget -Force
        } else {
            throw "$($engine.name): unsupported archive type '$fileName' (expected .zip or .exe)."
        }

        $exeHash = (Get-FileHash -Algorithm SHA256 -Path $exeTarget).Hash.ToLowerInvariant()
        $manifest += [ordered]@{ name = $engine.name; version = $engine.version; file = $engine.exe; sha256 = $exeHash }
        Write-Host "  $($engine.name) OK ($exeHash)" -ForegroundColor Green
    }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

if ($manifest.Count -eq 0) {
    Write-Host "No engines were pinned - engines\dist\engines.manifest.json not written." -ForegroundColor Yellow
    exit 0
}

[ordered]@{ engines = $manifest } | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $OutputDirectory "engines.manifest.json") -Encoding utf8
Write-Host "Wrote $(Join-Path $OutputDirectory 'engines.manifest.json')" -ForegroundColor Green
