# Installation Guide - CloudOrc Agent Installer

This document covers the single-EXE installer (`CloudOrcAgentSetup.exe`) built from this
repository: how to build it, release it via GitHub Actions, and install/upgrade/uninstall
it on a target Windows Server. This is still the **local-development/test version** of
the agents themselves - it does **not** connect to the real CloudOrc backend and has no
production authentication; see [FUTURE_BACKEND_INTEGRATION.md](FUTURE_BACKEND_INTEGRATION.md).
This document is only about packaging/distribution/installation.

For the older ZIP-based deployment path (no installer, plain scripts), see
[DEPLOYMENT.md](DEPLOYMENT.md) - both exist side by side; this installer path is the
recommended one for deploying to many servers.

## Target-server prerequisites (read this first)

| Requirement | Status |
|---|---|
| Windows Server (x64) | Required |
| Administrator privileges | Required (to create Windows Services) |
| Network access to GitHub (to download the installer) | Required for the one-line install command; not required if you copy the EXE over manually |
| **.NET SDK** | **NOT REQUIRED** |
| **.NET Runtime** | **NOT REQUIRED** |
| **Git** | **NOT REQUIRED** |
| **Visual Studio** | **NOT REQUIRED** |
| **VS Code** | **NOT REQUIRED** |
| **Node.js** | **NOT REQUIRED** |
| **Docker** | **NOT REQUIRED** |
| **Source code** | **NOT REQUIRED** |

`CloudOrcAgentSetup.exe` deploys the **self-contained** published output of both agents -
the .NET 10 runtime and every dependency (including the PowerShell SDK) ship inside the
installer itself.

---

## A. Development build

```powershell
cd CloudOrcAgent
dotnet restore CloudOrc.WindowsAgents.sln
dotnet build CloudOrc.WindowsAgents.sln -c Release
dotnet test CloudOrc.WindowsAgents.sln -c Release
```

## B. Local package creation

Publishing both agents self-contained and producing the release ZIP (used as an input by
the installer build, and independently useful on its own - see [DEPLOYMENT.md](DEPLOYMENT.md)):

```powershell
.\scripts\package-agent.ps1
```

Produces `dist\CloudOrcAgents-win-x64\ControlAgent\`, `...\WatchdogAgent\`,
`dist\CloudOrcAgents-win-x64.zip`, and `dist\CloudOrcAgents-win-x64.sha256`.

### Building the installer itself

Requires [Inno Setup](https://jrsoftware.org/isinfo.php) 6.x (`ISCC.exe`) on the build
machine only - never on the target server. After `scripts\package-agent.ps1` has produced
`dist\CloudOrcAgents-win-x64\...`:

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=1.0.0 installer\CloudOrcAgentSetup.iss
```

Produces `installer\Output\CloudOrcAgentSetup.exe`. Generate its checksum:

```powershell
$hash = (Get-FileHash -Algorithm SHA256 -Path "installer\Output\CloudOrcAgentSetup.exe").Hash.ToLowerInvariant()
"$hash  CloudOrcAgentSetup.exe" | Set-Content "installer\Output\CloudOrcAgentSetup.exe.sha256" -NoNewline -Encoding ascii
```

(`/DMyAppVersion=...` is optional locally - it defaults to `0.0.0-dev` if omitted.)

## C. GitHub repository setup

1. Create the GitHub repository (public or private - see
   [R. Private GitHub repository limitations](#r-private-github-repository-limitations)
   if private). This project assumes a **public** repository, which is what makes the
   plain `releases/latest/download/...` URLs work without authentication.
2. Push this repository's contents (see exact first-time commands in the final report /
   README.md).
3. Edit `install-cloudorc.ps1` at the repo root: replace the placeholder
   `$DefaultRepositoryOwner = "REPLACE_OWNER"` / `$DefaultRepositoryName = "REPLACE_REPO"`
   with your actual GitHub org/user and repository name, then commit that change. This is
   the one piece of repo-specific customization required to make the pure
   `irm ... | iex` one-liner work without extra parameters (see
   [F. New Windows Server installation](#f-new-windows-server-installation) for why).

## D. GitHub Actions behavior

`.github/workflows/build-installer.yml`:

| Trigger | What runs | Creates a GitHub Release? |
|---|---|---|
| Push to `main` | restore, build, test, publish both agents, build `CloudOrcAgentSetup.exe`, upload as a CI artifact | No |
| Pull request into `main` | same as above | No |
| Push of a tag matching `v*` (e.g. `v1.0.0`) | same as above, **then** creates a GitHub Release | **Yes** |

Uses the built-in `GITHUB_TOKEN` (via `permissions: contents: write` at the workflow
level) - no personal access token is created, stored, or required for normal releases.

## E. Creating a v1.0.0 release

```powershell
git tag v1.0.0
git push origin v1.0.0
```

This triggers the workflow, which (after build+test+package succeed) creates a GitHub
Release titled **"CloudOrc Agent v1.0.0"** with two assets attached:

```
CloudOrcAgentSetup.exe
CloudOrcAgentSetup.exe.sha256
```

## F. New Windows Server installation

### One-line install (recommended)

From an elevated PowerShell prompt on the target server:

```powershell
irm https://raw.githubusercontent.com/varunpsingh74358/Server-Agent/main/install-cloudorc.ps1 | iex
```

Replace `varunpsingh74358/Server-Agent` with your actual GitHub org/user and repository name. This works
as a bare one-liner **only if** you completed step 3 in
[C. GitHub repository setup](#c-github-repository-setup) - editing the placeholder
defaults inside `install-cloudorc.ps1` - because piping a script into `iex` does not allow
passing it parameters. If you'd rather not edit that file, download it and run it with
explicit parameters instead (see below) - no edit needed in that case.

`install-cloudorc.ps1` downloads `CloudOrcAgentSetup.exe` + its `.sha256` from the
**latest** GitHub Release, verifies the checksum, and runs the installer elevated with
Inno Setup's fully unattended switches (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`) by
default. It never uses `Invoke-Expression` on the downloaded installer - only the small
bootstrap script itself is ever piped into `iex`; the installer is always launched as a
native process via `Start-Process`.

### Explicit-parameter form (no file edit required)

```powershell
.\install-cloudorc.ps1 -RepositoryOwner "varunpsingh74358" -RepositoryName "Server-Agent" -Version v1.0.0
```

### Manual download + run (equivalent, matches the task's example structure)

```powershell
$ProgressPreference = 'SilentlyContinue'

Invoke-WebRequest `
  -Uri "https://github.com/varunpsingh74358/Server-Agent/releases/latest/download/CloudOrcAgentSetup.exe" `
  -OutFile "$env:TEMP\CloudOrcAgentSetup.exe"

Start-Process `
  "$env:TEMP\CloudOrcAgentSetup.exe" `
  -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" `
  -Wait `
  -Verb RunAs
```

**Note on silent switches**: this installer is built with Inno Setup. Its real, verified
silent-install switches are `/VERYSILENT` (fully unattended, no UI at all - recommended
for scripted deployment to many servers) and `/SILENT` (progress bar only). `/quiet` is
**not** a native Inno Setup switch and does nothing special here - use `/VERYSILENT`.
Both `/VERYSILENT` and `/SILENT` were tested for real during development (see
[Testing performed](#testing-performed) below); `/quiet` was deliberately not implemented
as an alias rather than faking support for it.

### Normal (interactive) installation

Just run `CloudOrcAgentSetup.exe` with no arguments (or double-click it). It shows:

```
CloudOrc Agent Setup

Components:
[x] CloudOrc Control Agent
[x] CloudOrc Watchdog Agent

Installation directory: C:\Program Files\CloudOrc\Agents
```

Both components are selected by default; accepting every default requires zero manual
configuration - no `appsettings.json` editing, no .NET install, no Git install.

### Enrollment (connecting to a real backend with zero manual configuration)

Pass `--token` to enroll the agent with a backend automatically - no backend URL, no IP
address, and no `appsettings.json` editing, ever:

```powershell
CloudOrcAgentSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART --token "ENR-..."
```

The installer redeems the token (via the Control Agent's own `enroll` CLI mode) **before**
starting any service - a bad/expired/already-used token fails the whole install with a
clear message and a non-zero exit code, rather than starting a misconfigured service. See
[ENROLLMENT.md](ENROLLMENT.md) for the full architecture (token format, why it's designed
this way, security properties, and what a real backend needs to implement). Omitting
`--token` installs in local-only mode exactly as described above - both are fully
supported, permanently.

## G. Upgrade

Run the same installer (any distribution method above) again, pointing at a newer
version. The installer:

- Detects the existing installation via a fixed product ID.
- Stops both services before overwriting files (so binaries are never locked).
- Preserves each agent's existing `appsettings.json` / `appsettings.Development.json`
  (a literal file-level preserve, not a smart merge - a config field added in a newer
  default `appsettings.json` will not automatically appear in an already-customized file).
- Reconfigures (never duplicates) the two Windows Services.
- Restarts both services and verifies they reach `Running`.

Confirmed live during development: re-running the installer after customizing
`AgentId` left the customized value intact, and `sc.exe query` afterward showed exactly
one instance of each service - no duplicates.

## H. Uninstall

**Via Windows** ("Apps & Features" / "Add or Remove Programs" - search "CloudOrc Agent")
or:

```powershell
& "C:\Program Files\CloudOrc\Agents\unins000.exe"
```

**Silent uninstall:**

```powershell
& "C:\Program Files\CloudOrc\Agents\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Both stop and remove `CloudOrcControlAgent`/`CloudOrcWatchdogAgent` and delete
`C:\Program Files\CloudOrc\Agents\`. **`C:\ProgramData\CloudOrc\` (logs, command/result
history, and each agent's runtime config) is never touched by uninstall** - confirmed live:
after uninstalling, a result file written during earlier testing was still present under
`C:\ProgramData\CloudOrc\ControlAgent\results\`. Delete that directory manually if you
want a completely clean removal.

## I. Health check

The installer itself verifies both services reach `Running` before reporting success (a
non-zero exit code means it did not - see
[K. Troubleshooting](#k-troubleshooting)). For an ongoing/repeatable health check after
installation, the script-based deployment path's checker also works against an
installer-based deployment (same service names, same install root convention):

```powershell
.\scripts\health-check.ps1
```

Reports OS architecture, both services' status, executable existence, install/data paths,
process status, and recent related Windows event log entries. Exit code `0` = healthy,
`1` = unhealthy.

## J. Service names, install directories, data directories

| Item | Value |
|---|---|
| Control Agent service name | `CloudOrcControlAgent` |
| Watchdog Agent service name | `CloudOrcWatchdogAgent` |
| Control Agent install path | `C:\Program Files\CloudOrc\Agents\ControlAgent\` |
| Watchdog Agent install path | `C:\Program Files\CloudOrc\Agents\WatchdogAgent\` |
| Control Agent data/logs | `C:\ProgramData\CloudOrc\ControlAgent\` |
| Watchdog Agent data/logs | `C:\ProgramData\CloudOrc\WatchdogAgent\` |

## K. Troubleshooting

| Symptom | Likely cause | What to check |
|---|---|---|
| Installer exits with code `10` | Control Agent service did not reach `Running` within ~10s of `sc start` | `C:\ProgramData\CloudOrc\ControlAgent\logs\controlagent-*.log`; confirm no other process is already using the same Named Pipe/port |
| Installer exits with code `11` | Watchdog Agent service did not reach `Running` | `C:\ProgramData\CloudOrc\WatchdogAgent\logs\watchdogagent-*.log` |
| `install-cloudorc.ps1` fails with "still the placeholder values" | Repo owner/name not configured | See [C. GitHub repository setup](#c-github-repository-setup) step 3, or pass `-RepositoryOwner`/`-RepositoryName` explicitly |
| `install-cloudorc.ps1` fails with a SHA256 mismatch | Corrupted download, or a release published without a matching checksum | Re-download; never pass `-AllowUnverified` to work around a genuine mismatch - only use it for an old release that predates checksum publishing |
| Installer silently does nothing under `/quiet` | `/quiet` is not a real Inno Setup switch | Use `/VERYSILENT` instead - see [F](#f-new-windows-server-installation) |
| `sc.exe`-related errors during upgrade | A stale/corrupted service registration from a previous partial install | Run the silent uninstaller first (`unins000.exe /VERYSILENT`), then reinstall |
| A PowerShell command run through the agent always fails with a module-loading/execution-policy error | Target server's PowerShell execution policy blocks that specific module - unrelated to the installer | This is environment configuration, not an installer or agent defect |

## L. Required permissions

- Installing/uninstalling: elevated (Administrator) PowerShell/Command Prompt - enforced
  by the installer's manifest (`PrivilegesRequired=admin`); Windows will prompt for
  elevation if not already elevated.
- Both services run as `LocalSystem` by default (Inno's `sc.exe create` with no
  `obj=`/`password=`), sufficient to run arbitrary PowerShell locally and for the
  Watchdog to start/stop the Control Agent service. See
  [WINDOWS_SERVICE_INSTALLATION.md §3](WINDOWS_SERVICE_INSTALLATION.md#3-required-permissions)
  for configuring a narrower dedicated service account instead, if required.

## M. Network requirements

- The target server needs outbound HTTPS access to `github.com` (and
  `objects.githubusercontent.com`, GitHub's release-asset CDN) only for the one-line
  install command. If the server has no internet access, copy `CloudOrcAgentSetup.exe`
  and its `.sha256` over by any other secure channel and run it locally - no network
  access is required for the installer itself to work.
- Neither agent opens any inbound network listener. The Watchdog talks to the Control
  Agent over a local Named Pipe only. `BackendConnection` (disabled by default) is the
  only outbound network feature, and connecting it to anything beyond the local
  `tools/CloudOrc.AgentTestServer` dev stand-in is out of scope for this phase.

## N. Checksum verification

Every release publishes `CloudOrcAgentSetup.exe.sha256` alongside the installer.
`install-cloudorc.ps1` downloads and verifies it **before** launching the installer, and
refuses to proceed on a mismatch unless `-AllowUnverified` is explicitly passed (not
recommended). To verify manually:

```powershell
$expected = (Get-Content "CloudOrcAgentSetup.exe.sha256").Split(" ")[0]
$actual = (Get-FileHash -Algorithm SHA256 -Path "CloudOrcAgentSetup.exe").Hash.ToLowerInvariant()
if ($expected -eq $actual) { "OK" } else { "MISMATCH" }
```

## R. Private GitHub repository limitations

This installer/bootstrap setup **assumes a public repository**, per this deployment
model's explicit design choice - the plain `releases/latest/download/<asset>` and
`releases/download/<tag>/<asset>` URLs, and the `raw.githubusercontent.com/.../main/...`
URL for the bootstrap script, all require **no authentication for a public repo** and
**do require authentication for a private one** (a private repo's `browser_download_url`
404s without a valid session/token; raw file URLs likewise 404 without one).

If this repository is ever made private:

- `install-cloudorc.ps1`'s plain `Invoke-WebRequest` downloads will fail with a 404, not a
  permissions error - this is expected, not a bug.
- Do **not** solve this by hardcoding a personal access token into `install-cloudorc.ps1`,
  the installer, or any file in source control - a token committed to a repository
  (private or not) is a real exposure risk (contributor access, CI logs, forks, etc.).
- The safe, practical options for a private repository:
  1. **Authenticated download with a runtime-supplied token** - pass a token as a
     parameter or environment variable at install time (never store it in a file), and
     use the GitHub REST API asset-download flow (`GET
     /repos/{owner}/{repo}/releases/tags/{tag}` to find the asset, then `GET` its
     `url` with `Accept: application/octet-stream` and an `Authorization: Bearer <token>`
     header) instead of the plain `browser_download_url`. `scripts/install-agent.ps1` in
     this repository already implements exactly this pattern for the ZIP-based
     deployment path (see its `-GitHubToken` parameter) - the same technique would need
     to be added to `install-cloudorc.ps1` if this repository becomes private.
  2. **A dedicated CloudOrc download endpoint** - a small authenticated proxy/mirror
     (part of the real CloudOrc backend, once it exists) that serves the installer to
     enrolled servers via short-lived, scoped authorization instead of a GitHub
     credential at all. This is the better long-term answer once Phase 3
     (backend integration/enrollment) exists - see
     [FUTURE_BACKEND_INTEGRATION.md](FUTURE_BACKEND_INTEGRATION.md).
  3. **Manual/internal distribution** - copy `CloudOrcAgentSetup.exe` +
     `.sha256` to target servers via an internal file share, deployment tool, or golden
     image, and skip the GitHub download step entirely (run the EXE directly - still
     supports `/VERYSILENT`).

This limitation is stated explicitly here, as required, rather than worked around with a
hardcoded credential.

## Testing performed

Everything below was actually built and executed during development of this installer -
not assumed from the `.iss` script alone:

- `scripts/package-agent.ps1` run for real: build, 95/95 tests, both self-contained
  publishes, ZIP, checksum - all verified.
- `installer/CloudOrcAgentSetup.iss` compiled with Inno Setup 6.7.3 (`ISCC.exe`), zero
  warnings.
- Fresh silent install (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`): both services
  created, set to `AUTO_START`, reached `RUNNING`, confirmed via `sc.exe query`/`qc`;
  failure-recovery actions confirmed via `sc.exe qfailure`; a real PowerShell command
  (`Get-Date`) submitted to the installer-deployed Control Agent completed successfully.
- Upgrade (re-running the installer): confirmed no duplicate service registration
  (`sc.exe query type= service state= all` showed exactly one of each), and a
  customized `AgentId` in `appsettings.json` survived the upgrade unchanged.
- Silent uninstall (`unins000.exe /VERYSILENT`): both services removed, install directory
  removed, `C:\ProgramData\CloudOrc\` (including prior result history) left untouched.
- **Failure-path exit code**: a deliberately broken build (fake Control Agent
  executable) was installed to confirm the installer detects the service failing to
  reach `Running` and exits with code `10` (a real, distinguishable non-zero code) rather
  than falsely reporting success - `RaiseException` was found during this testing to
  **not** reliably propagate to the process exit code from `ssPostInstall` under
  `/VERYSILENT` and was replaced with a direct `ExitProcess` Win32 call, verified with an
  isolated test (`ExitProcess(17)` -> confirmed process exit code `17`) before being
  applied to the real installer.
- `install-cloudorc.ps1`: placeholder-rejection path confirmed (refuses to run without a
  real repo configured); full download -> SHA256 verify -> elevated install -> exit-code
  propagation -> cleanup flow confirmed end-to-end against a local mock release server;
  checksum-mismatch rejection confirmed (deliberately corrupted checksum -> exit code 1,
  installer never launched).
