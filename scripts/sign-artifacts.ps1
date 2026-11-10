<#
.SYNOPSIS
    Authenticode-signs CloudOrc's own binaries (and/or the installer) with signtool.

.DESCRIPTION
    CO-DEV-BKP-001 section 13 "signed MSI/EXE installer ... signed updater" and section 15
    "Tampered installer/update/engine binary -> signature verification fails closed": the
    updater refuses unsigned installers (WinVerifyTrust), so release builds MUST be signed.

    Only CloudOrc-built files are signed (CloudOrc.*.exe / CloudOrc.*.dll and the installer).
    Third-party engine binaries are not re-signed - they are pinned by SHA-256 in
    engines.manifest.json instead.

    The certificate comes from the environment (CI secret), never from the repository:
      CODE_SIGNING_PFX_BASE64   base64 of the .pfx
      CODE_SIGNING_PFX_PASSWORD its password
      CODE_SIGNING_TIMESTAMP_URL optional RFC 3161 timestamp server (defaults to DigiCert's)

.PARAMETER Path
    Files or folders to sign (folders are searched recursively for CloudOrc.* binaries).

.PARAMETER Required
    Fail when no signing certificate is configured (use for release tags).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [switch]$Required
)

$ErrorActionPreference = "Stop"

if (-not $env:CODE_SIGNING_PFX_BASE64) {
    if ($Required) { throw "Code signing is required for this build but CODE_SIGNING_PFX_BASE64 is not set." }
    Write-Host ">>> No code-signing certificate configured - skipping signing (development build)." -ForegroundColor Yellow
    exit 0
}

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe (Windows SDK) not found." }

$timestamp = if ($env:CODE_SIGNING_TIMESTAMP_URL) { $env:CODE_SIGNING_TIMESTAMP_URL } else { "http://timestamp.digicert.com" }
$pfx = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-" + [guid]::NewGuid().ToString("N") + ".pfx")
[System.IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:CODE_SIGNING_PFX_BASE64))

try {
    $files = foreach ($p in $Path) {
        if (Test-Path $p -PathType Container) {
            Get-ChildItem $p -Recurse -File | Where-Object { $_.Name -like "CloudOrc*.exe" -or $_.Name -like "CloudOrc*.dll" }
        } elseif (Test-Path $p) {
            Get-Item $p
        }
    }

    foreach ($file in $files) {
        & $signtool.FullName sign /fd SHA256 /td SHA256 /tr $timestamp /f $pfx /p $env:CODE_SIGNING_PFX_PASSWORD $file.FullName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Signing failed: $($file.FullName)" }

        $sig = Get-AuthenticodeSignature $file.FullName
        if ($sig.Status -ne "Valid") { throw "Signature not valid after signing: $($file.FullName) ($($sig.Status))" }
        Write-Host "  signed: $($file.Name)"
    }

    Write-Host "Signed $(@($files).Count) file(s)." -ForegroundColor Green
}
finally {
    Remove-Item $pfx -Force -ErrorAction SilentlyContinue
}
