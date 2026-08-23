# CloudOrc Windows Agents — Project Status Report

**Last updated:** 2026-08-23
**Phase 1 (local file execution):** **COMPLETE and VERIFIED**
**Phase 2 (local WebSocket testing layer):** **COMPLETE and VERIFIED**
**Phase 2.5 (final validation + self-contained deployment packaging):** **COMPLETE and VERIFIED**
**Phase 2.6 (GitHub-based installer distribution):** **COMPLETE and VERIFIED — live on a real public GitHub repository, two real releases shipped (`v1.0.0`, `v1.1.0`)**
**Phase 3a (environment-agnostic enrollment architecture):** **COMPLETE and VERIFIED — live end-to-end**
**Phase 3b (real CloudOrc backend connection + production security):** **NOT STARTED**

---

## 1. What This Project Is

Two .NET 10 Worker Services that will eventually replace WinRM for running PowerShell
commands on Windows Servers from an existing backend:

1. **CloudOrc Control Agent** — executes PowerShell commands generically (no command is
   hardcoded — any valid script can be sent), receives those commands over a WebSocket
   connection and/or local JSON files, and — as of this update — **enrolls itself with a
   backend using a one-time token**, with zero hardcoded backend URL anywhere in its
   source or binary, and zero manual `appsettings.json` editing required.
2. **CloudOrc Watchdog Agent** — monitors the Control Agent and restarts it in a
   controlled, rate-limited way if it becomes unhealthy. Confirmed to never confuse a
   disconnected backend with a dead Control Agent, and confirmed to perform a real
   Windows-Service restart (see §3.5).

A third piece, `tools/CloudOrc.AgentTestServer`, is a **local, dev-only stand-in for the
real CloudOrc backend**, now including a **reference (dev/test-only) enrollment backend**
(token issuance/redemption, credential issuance/revocation) so the entire enrollment
architecture can be exercised end-to-end before a real backend exists. It is not part of,
and never ships with, the production agent.

**The repository is now live on GitHub as a public repo:**
`https://github.com/varunpsingh74358/Server-Agent` — with a working CI/CD pipeline
(GitHub Actions), two published releases (`v1.0.0`, `v1.1.0`), and a one-line installer
bootstrap script. See §13 for the full release history, including two real bugs the
release process itself surfaced and how they were fixed.

Nothing in this project connects to the **real** CloudOrc backend yet, and there is still
no production TLS certificate, credential rotation, or RBAC — see §5. What changed this
update is that the **agent-side enrollment/identity/authentication architecture is now
fully built and verified**, closing the single biggest gap called out in every previous
version of this report.

---

## 2. What Has Been Built

| Item | Status |
|---|---|
| Solution `CloudOrc.WindowsAgents.sln` — 8 projects | ✅ Done |
| `CloudOrc.Agent.Contracts` — shared models, interfaces, wire protocol, enrollment contracts | ✅ Done |
| `CloudOrc.ControlAgent` — full Worker Service + enrollment client | ✅ Done |
| `CloudOrc.WatchdogAgent` — full Worker Service | ✅ Done |
| `tools/CloudOrc.AgentTestServer` — local backend stand-in + reference enrollment backend (dev/test only) | ✅ Done |
| `CloudOrc.ControlAgent.Tests` — 107 automated tests | ✅ Done |
| `CloudOrc.WatchdogAgent.Tests` — 15 automated tests | ✅ Done |
| `CloudOrc.AgentTestServer.Tests` — 14 automated tests | ✅ **New** |
| **136 automated tests total** (up from 95) | ✅ All passing |
| Full documentation set (`docs/`, 10 files including `ENROLLMENT.md`) | ✅ Done |
| Self-contained `win-x64` publish for both agents | ✅ Done, produced by `scripts\package-agent.ps1` |
| `scripts/install-agent.ps1` / `scripts/uninstall-agent.ps1` / `scripts/health-check.ps1` / `scripts/package-agent.ps1` | ✅ Done, all run for real |
| Real Windows Service install + real Watchdog-triggered restart | ✅ Done, performed for real |
| `CloudOrcAgentSetup.exe` (single-file Inno Setup installer) | ✅ Done — now supports `--token` for enrollment |
| `install-cloudorc.ps1` (one-line bootstrap: `irm ... \| iex`) | ✅ Done, installs from GitHub Releases with SHA256 verification |
| `.github/workflows/build-agent-release.yml` + `build-installer.yml` | ✅ Done — build+test+package on every push, publish a GitHub Release with all assets on a `v*` tag |
| **Public GitHub repository with two real releases** | ✅ **`github.com/varunpsingh74358/Server-Agent`** — `v1.0.0` (installer distribution) and `v1.1.0` (+ enrollment) |
| **Environment-agnostic enrollment architecture** | ✅ **New this update — see §2a, §3.10, and `docs/ENROLLMENT.md`** |

### 2a. Enrollment architecture — what it actually does

One line, no manual configuration, works with any backend without rebuilding anything:

```
CloudOrcAgentSetup.exe --token "ENR-<opaque-token>"
```

| Capability | Status |
|---|---|
| One opaque enrollment token is the ONLY server-specific input | ✅ Verified live |
| Zero hardcoded backend URL/IP/hostname anywhere in agent source, installer, or compiled binary | ✅ Verified by repository-wide grep (see §3.10) |
| Same agent build + same installer works with any backend environment | ✅ By design — token carries the routing, not the binary |
| Agent identity (`AgentId`/`ServerId`) issued by the backend, not configured | ✅ Verified live, stable across 3 real service restarts |
| Permanent credential presented as `Authorization: Bearer` on every WebSocket connection | ✅ Verified live — accepted by the reference backend |
| Credential/identity persisted encrypted at rest (Windows DPAPI, machine scope) | ✅ Verified — real DPAPI blob confirmed non-plaintext (hex-dumped) |
| Reconnect/service-restart never re-enrolls, never asks for a token again | ✅ Verified live across 3 real `sc.exe stop`/`start` cycles |
| Invalid / expired / reused / revoked-before-use tokens are rejected, exit code 20 | ✅ All four verified live against the real reference backend |
| A failed enrollment attempt never corrupts an existing good enrollment | ✅ Verified live (4 failed attempts in a row, agent kept working) |
| No secret/token/credential ever appears in logs | ✅ Verified by grepping real log output |
| Existing local-file mode / manual-config mode still works unchanged | ✅ Verified — installing with no `--token` behaves exactly as before enrollment existed |

Full design rationale (including the one deliberate, disclosed trade-off — the token
encodes its own redemption URL rather than relying on a fixed hardcoded bootstrap host)
is in [docs/ENROLLMENT.md](docs/ENROLLMENT.md).

### Control Agent — feature checklist

| Feature | Status |
|---|---|
| Generic PowerShell execution (no hardcoded commands) | ✅ Working |
| Local JSON file command source (`commands\`) | ✅ Working |
| WebSocket command source (receives COMMAND messages) | ✅ Working, verified live |
| Both sources active simultaneously, sharing one queue/executor | ✅ Verified live — no interference |
| Duplicate protection (per-source + cross-source via shared on-disk signal) | ✅ Working (at-least-once, not exactly-once — see docs/ARCHITECTURE.md) |
| Sequential execution (one command at a time, regardless of source) | ✅ Working |
| Timeout handling (actually stops the pipeline) | ✅ Working — reconfirmed at 5005ms for a 5s timeout |
| HELLO / HEARTBEAT / TELEMETRY over WebSocket | ✅ Working, verified live including with enrollment-issued auth |
| Automatic reconnect with exponential backoff | ✅ Working — 2s→4s→8s→16s→32s reconfirmed live |
| Stays alive & keeps processing local commands during a backend outage | ✅ Working — reconfirmed live |
| Refuses to start with an insecure `ws://` URL unless explicitly allowed (or the URL came from enrollment) | ✅ Working |
| Local health reporting via Named Pipe | ✅ Working |
| Runs as a real installed Windows Service (`CloudOrcControlAgent`) | ✅ Actually installed, started, and exercised as a real service |
| **Enrolls with a one-time token; persists identity/credential DPAPI-encrypted** | ✅ **New — full live verification, see §3.10** |
| **Presents `Authorization: Bearer <credential>` on the WebSocket handshake when enrolled** | ✅ **New — accepted live by the reference backend** |

### Watchdog Agent — feature checklist

| Feature | Status |
|---|---|
| Checks Control Agent Windows Service status | ✅ Working |
| Checks Control Agent health over Named Pipe | ✅ Working |
| Logs backend connection state for visibility only (never used in decision logic) | ✅ Reconfirmed: stayed `HEALTHY` through a full backend outage |
| Consecutive-failure counter (ignores single blips) | ✅ Working |
| Attempts recovery only after threshold (default 3) | ✅ Working |
| Restart via `ServiceController` against a real installed service | ✅ A real `CloudOrcControlAgent` Windows Service was stopped, the Watchdog detected it (1/3 → 2/3 → 3/3), issued a real restart, and logged "Recovery succeeded; Control Agent is healthy again." |
| Restart rate limiting + exponential backoff | ✅ Working (unit tested; live backoff suppression also reconfirmed) |
| Runs as a real installed Windows Service (`CloudOrcWatchdogAgent`) | ✅ Actually installed, started, and used to perform a real recovery |
| Not touched by enrollment work | ✅ By design — Watchdog never connects to a backend, has no identity/credential concept |

---

## 3. Verification Performed (Not Just Claimed — Actually Tested)

### 3.1 Build + automated tests (current)

```
dotnet restore CloudOrc.WindowsAgents.sln
dotnet build CloudOrc.WindowsAgents.sln
dotnet test CloudOrc.WindowsAgents.sln
```

**Result: 0 Warnings / 0 Errors, 136 / 136 tests passed** (107 Control Agent + 15
Watchdog + 14 AgentTestServer — up from 95 before enrollment work). Re-verified from a
genuine fresh `git clone` of the GitHub repository, not just the local working copy (see
§3.11).

### 3.2 Local file mode (console mode)

| Test | Command | Result |
|---|---|---|
| Basic execution | `Get-Date` | `Success` |
| Multi-line output | `Get-Service \| Select-Object -First 5` | `Success`, 5 services |
| Process listing | `Get-Process \| Select-Object -First 5` | `Success` |
| Drive listing | `Get-PSDrive` | `Success` |
| Network cmdlets | `Get-NetIPAddress`, `Test-NetConnection localhost` | `Failed` — this specific machine's PowerShell execution policy blocks the `NetTCPIP` module; the executor correctly captured the real error and stayed alive. Environment condition, not an agent defect. |
| Error handling | `Get-Service -Name "DefinitelyDoesNotExist"` | `Failed`, real error message, agent kept running |
| Timeout handling | `Start-Sleep -Seconds 30`, 5s timeout | `Timeout` at **5005ms** |
| Duplicate protection | Same `commandId` resubmitted after completion | Diverted to `failed\{id}.duplicate-*.json`; original result untouched |
| Recovery after failure | Fresh `Get-Date` after the above | `Success` |

### 3.3 WebSocket mode (against `tools/CloudOrc.AgentTestServer`, pre-enrollment manual config)

| Test | Result |
|---|---|
| HELLO handshake | Real `machineId` (Windows `MachineGuid`), real `machineName` |
| HEARTBEAT (15s interval) | `status=HEALTHY, workerAlive=true` every tick |
| TELEMETRY (10s interval) | Real CPU%, memory, all 10 mounted drives, uptime |
| `Get-Date`, `Get-Service` over WebSocket | `COMMAND_STATUS: Queued→Running` then `COMMAND_RESULT: Success` |
| Failing command over WebSocket | `COMMAND_RESULT: Failed`, real error |
| `Start-Sleep -Seconds 30`, 4s timeout, over WebSocket | `COMMAND_RESULT: Timeout` at 4017ms |
| Backend outage (test server killed) | Backoff confirmed: 2s → 4s → 8s → 16s → 32s |
| Local file command during the outage | `Success` in 17ms |
| Watchdog throughout the outage | Logged `status=HEALTHY` every cycle |
| Test server restarted | Control Agent reconnected automatically, sent a fresh HELLO |

### 3.4 Console-mode Watchdog recovery (Control Agent process killed, no service installed)

| Step | Result |
|---|---|
| Control Agent process killed | Watchdog health checks began failing: 1/3 → 2/3 → 3/3 |
| Recovery attempt at threshold | Triggered correctly; failed gracefully — "service not installed" (expected in console mode) |
| Backoff after the failed attempt | Correctly suppressed on the next cycle |
| Watchdog process itself | Never crashed |

### 3.5 Real Windows Service installation and real Watchdog restart

1. Published self-contained `win-x64` builds of both agents.
2. `sc.exe create` for both — `[SC] CreateService SUCCESS`.
3. Started both — `sc.exe query` confirmed `STATE: 4 RUNNING` for both.
4. Watchdog, pointed at a **real installed service**, logged
   `Control Agent service 'CloudOrcControlAgent' status: Running.` and `status=HEALTHY`.
5. Submitted a command to the **service-hosted** Control Agent — real `Success` result,
   logs written to `C:\ProgramData\CloudOrc\ControlAgent\logs\` (no console attached).
6. `sc.exe stop CloudOrcControlAgent` — simulated a real failure.
7. Watchdog logged 1/3 → 2/3 → 3/3, then a real `ServiceController.Start()` call,
   independently confirmed via `sc.exe query` showing `RUNNING` again, and
   `"Recovery succeeded; Control Agent is healthy again."`
8. Both services stopped and removed afterward, leaving the development machine clean.

### 3.6 Self-contained publish verification

```
dotnet publish src\CloudOrc.ControlAgent\CloudOrc.ControlAgent.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64\ControlAgent
dotnet publish src\CloudOrc.WatchdogAgent\CloudOrc.WatchdogAgent.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64\WatchdogAgent
```

Both output folders contain `hostfxr.dll`/`coreclr.dll` and all
`Microsoft.PowerShell.SDK` dependencies, and each `.exe` was launched directly from its
publish folder with no `dotnet run` and no SDK involved.

### 3.7 Deployment scripts verified live

`scripts/install-agent.ps1`, `uninstall-agent.ps1`, `health-check.ps1`, and
`package-agent.ps1` were all run for real (not just syntax-checked). A real bug was found
and fixed during this (`$PSScriptRoot` not populated inside a parameter *default-value*
expression — fixed by resolving it in the script body instead).

### 3.8 Real bugs found and fixed (across all sessions, in chronological order)

1. **(Phase 1)** `PowerShell.Stop()` was sometimes wired up before the pipeline had
   actually started, so external cancellation had no effect.
2. **(Phase 2)** The local test server's console-input loop crashed on end-of-input in a
   non-interactive context — fixed to only shut down on an explicit `exit`/`quit`.
3. **(Phase 2.5)** `scripts/install-agent.ps1` used `$PSScriptRoot` inside a parameter
   default value, which is empty on this PowerShell host at parameter-binding time —
   fixed by resolving the script root inside the script body instead.
4. **(Phase 2.6)** `installer/CloudOrcAgentSetup.iss`'s `RaiseException` did **not**
   reliably propagate to the process exit code from `ssPostInstall` under `/VERYSILENT`
   (confirmed empirically: returned exit code 0 even after "raising") — replaced with a
   direct Win32 `ExitProcess` call, verified with an isolated test first.
5. **(Phase 2.6)** Both GitHub Actions workflows triggered on the same `v*` tag and both
   tried to `gh release create` for it — whichever finished second failed with HTTP 422
   "tag_name already exists". Fixed by checking for an existing release first and falling
   back to `gh release upload`, with a race-safe retry.
6. **(Phase 2.6)** `install-cloudorc.ps1` used `Start-Process -Verb RunAs -Wait`
   unconditionally. Confirmed live on the real target server: when the calling session
   was **already elevated**, re-elevating via `-Verb RunAs` caused `-Wait` to hang
   indefinitely even though the installer had already finished completely — a known
   Windows process-tracking limitation seen over RDP. Fixed by skipping `-Verb RunAs`
   when already elevated, and polling `HasExited` with progress messages instead of a
   bare `-Wait`.
7. **(Phase 3a, enrollment)** `EnrolledStateStore` took a raw `ControlAgentOptions`
   constructor parameter, but only `IOptions<ControlAgentOptions>` was registered in the
   DI container. First live install with a real token: the Control Agent service started,
   passed the installer's health check within its window, then **crashed immediately
   after** with `InvalidOperationException: Unable to resolve service for type
   'ControlAgentOptions'`. Fixed by changing the constructor to accept
   `IOptions<ControlAgentOptions>` (matching every other options consumer in the
   codebase), rebuilt, republished, and re-verified the full live flow end-to-end
   afterward.
8. **(Phase 2.6/3a, release process)** `.gitignore`'s blanket `*credential*`/`*secrets*`
   patterns (added earlier to block accidental secret-file commits) silently matched and
   dropped the legitimate source files `CredentialStore.cs` and `CredentialStoreTests.cs`
   from every `git add`. This broke the `v1.1.0` GitHub Actions build with
   `CS0246: The type or namespace name 'CredentialStore' could not be found` — the local
   working copy always built fine because the files still existed on disk, only git never
   tracked them. Root-caused (not just guessed) by cloning the pushed commit fresh into a
   clean directory and reproducing the identical compiler error locally. Fixed by
   narrowing the patterns to actual secret-file conventions (`*.credentials`,
   `credentials.json`, `*.secrets`, `secrets.json`) and committing the two missing files;
   confirmed fixed by cloning fresh from GitHub itself and rebuilding/retesting
   (136/136 passed) before re-tagging `v1.1.0`.

### 3.9 GitHub-based installer distribution (`v1.0.0`) — verified against a real, live repository

1. Repository pushed for real (`git init`/`add`/`commit`/`push`), with `.claude/` (local
   AI-assistant tool state) correctly excluded from version control.
2. Both GitHub Actions workflows confirmed running automatically on push, both completing
   with `conclusion: success`.
3. `git tag v1.0.0 && git push origin v1.0.0` confirmed to trigger the release jobs and
   (after the race-condition fix, bug #5) produce one GitHub Release, **"CloudOrc Agent
   v1.0.0"**, with all four assets: `CloudOrcAgentSetup.exe`, `.sha256`,
   `CloudOrcAgents-win-x64.zip`, `.sha256`.
4. `install-cloudorc.ps1` run for real via the one-liner (and directly with
   `-Version v1.0.0`): downloaded the real release asset, verified its SHA256 against the
   real published checksum, installed silently, confirmed both services `RUNNING` with a
   real command executed — all against the actual published release, not a local mock.
5. The elevation-hang bug (#6) was discovered on this real run, fixed, pushed to `main`,
   and re-verified live before being considered resolved.

### 3.10 Environment-agnostic enrollment architecture — full live end-to-end verification (`v1.1.0`)

Every item below was executed for real against the actual compiled installer and the real
(dev/test) reference enrollment backend — not simulated, not assumed:

1. **Design decision confirmed with the user first**: since a token-only enrollment
   command architecturally requires *some* way to know where to redeem it, and a fixed
   hardcoded bootstrap host was explicitly ruled out, the enrollment endpoint is encoded
   *inside* the opaque token itself. Disclosed trade-off: the token is opaque, not
   encrypted — its security comes from the embedded secret being single-use/short-lived/
   backend-validated, not from the encoding being secret.
2. **Real token generated** via the reference backend's `POST /api/enrollment-tokens`
   (not a fake example token).
3. **Real installer run with `--token`**: `CloudOrcAgentSetup.exe /VERYSILENT
   /SUPPRESSMSGBOXES /NORESTART --token "ENR-..."` — exit code 0.
4. **DPAPI encryption confirmed for real**: `C:\ProgramData\CloudOrc\ControlAgent\enrollment.dat`
   hex-dumped and confirmed to start with the standard DPAPI blob header
   (`01 00 00 00 D0 8C 9D DF ...`), not plaintext JSON.
5. **Real HELLO with Authorization header** accepted by the reference backend; agent's own
   log confirms `BackendConnection=true, Enrolled=true` and the enrolled `BackendUrl`,
   with zero lines of `appsettings.json` ever edited.
6. **HEARTBEAT/TELEMETRY** confirmed flowing (real CPU/memory/all 10 disks/uptime) over
   the authenticated connection.
7. **`Get-Date` sent live** → `COMMAND_STATUS: Queued→Running` → `COMMAND_RESULT: Success`.
8. **Disconnect resilience**: backend process stopped — agent stayed alive, retried with
   exponential backoff, never attempted to re-enroll.
9. **Service-restart identity/credential persistence**: `CloudOrcControlAgent` service
   stopped/started **3 times in a row** against a stable backend — the exact same
   `AgentId` and credential were presented and accepted every single time, confirmed both
   in the agent's own log and the backend's log.
10. **Four live negative enrollment tests**, each rejected with a clear message and
    process exit code `20`:
    - Invalid/garbage token format.
    - An already-consumed (reused) token.
    - An expired token (issued with a 1-second validity, then redeemed after expiry).
    - A token revoked before use (`POST /api/enrollment-tokens/revoke`).
11. **Failed attempts don't corrupt state**: after all four negative tests above, the
    agent's existing good enrollment was confirmed still intact — service still
    `RUNNING`, still the same `AgentId`, and a fresh `Get-Date` command still succeeded.
12. **No secret leakage**: grepped the real Control Agent log file and the real test
    server's console output for the words "credential"/"secret" and for `ENR-` token
    fragments — both came back completely empty.
13. **Static repository audit** (see exact commands/results in the conversation log):
    grepped `src/` and `installer/` for `localhost`, `127.0.0.1`, private IP ranges
    (`10.x`, `172.16-31.x`, `192.168.x`), literal `ws://`/`wss://`/`http://`/`https://`
    URLs, and `cloudorc.com`/`api.cloudorc.com`-style hostnames. The only hits were a
    documentation *comment* illustrating the config format (not runtime data) and
    `localhost` inside `tools/CloudOrc.AgentTestServer` (the explicitly-allowed dev/test
    tool). `BackendConnection.Url` in `appsettings.json` is, and remains, `""`.
14. Manual CLI re-enrollment also verified independently: `CloudOrc.ControlAgent.exe
    enroll --token "..."` run directly against an already-installed agent, succeeded, and
    the running service picked up the new identity after a restart.

### 3.11 GitHub release lifecycle for `v1.1.0` — including a real CI failure, root-caused and fixed

This is intentionally documented in detail because it is a genuine example of "trust but
verify" catching a real problem before it reached anyone relying on the release:

1. `v1.1.0` tag pushed — both GitHub Actions workflows **failed** (bug #8 above).
2. Root cause confirmed, not guessed: cloned the exact pushed (broken) commit into a
   clean temp directory and reproduced the identical `CS0246: CredentialStore` compiler
   error locally, matching the CI annotation exactly.
3. Fix committed and pushed to `main`.
4. **Before re-tagging**, the fix was verified by cloning fresh from `github.com` itself
   (not the local working copy) into a new clean directory and running a full
   `dotnet build` + `dotnet test` — `136/136` passed.
5. `v1.1.0` tag moved to the fixed commit (`git tag -f` + `git push --force` on the tag
   only, never on `main`) and re-pushed.
6. Both workflows re-ran and completed with `conclusion: success`.
7. Release assets confirmed present via the GitHub API: `CloudOrcAgentSetup.exe`,
   `CloudOrcAgentSetup.exe.sha256`, `CloudOrcAgents-win-x64.zip`,
   `CloudOrcAgents-win-x64.sha256` — all with real `created_at` timestamps from the
   successful run.

---

## 4. Environment Issues Hit and Resolved (informational, not code defects)

1. A build-cache folder got locked by VS Code's C# language server — fixed by restarting
   that process.
2. Leftover `obj_verify` folders from an earlier verification build caused duplicate
   compilation — fixed by deleting them.
3. This validation machine's PowerShell execution policy blocks loading the `NetTCPIP`
   module, so `Get-NetIPAddress`/`Test-NetConnection` return `Failed` here — a property of
   this specific machine, not the agent.
4. **(This update)** Inno Setup's `Start-Process`-equivalent elevation handling and a
   `.gitignore` authoring mistake both turned out to be genuine, reproducible bugs rather
   than one-off environment noise — see §3.8 items 6–8. Both are now fixed and
   independently re-verified.

All fully resolved / understood.

---

## 5. What Is NOT Ready Yet (By Design — Not a Gap)

| Item | Status |
|---|---|
| Connection to the **real** CloudOrc backend | ❌ Not built — only tested against the local `tools/CloudOrc.AgentTestServer` stand-in (including its reference enrollment backend) |
| Production TLS (`wss://` against a real trusted certificate) | ❌ Not tested — the code supports it and an enrollment response can hand back a `wss://` URL, but only `ws://localhost` has actually been run |
| Agent enrollment / issued identity / permanent credentials | ✅ **Now implemented and verified live** (§3.10) — this item is no longer outstanding |
| Credential rotation | ❌ Not built — today's issued credential is long-lived (revocable, but not automatically rotated on a schedule) |
| RBAC / audit logs | ❌ Not built (explicitly out of scope for this phase) |
| A real, persistent (database-backed) enrollment backend | ❌ Not built — the reference implementation in `tools/CloudOrc.AgentTestServer` is deliberately in-memory/dev-test-only |
| Windows Service actually installed & restart-tested | ✅ Performed for real (§3.5) |
| Database / persistent command history | ❌ Not built (explicitly out of scope) |
| Live incremental output streaming (only final result today) | ❌ Not built |
| Reboot-survival (services auto-starting after a genuine server reboot) | ⚠️ Proxied via real service stop/start cycles (§3.10 item 9), not an actual OS reboot — deliberately not performed on this development machine. See `docs/DEPLOYMENT_TEST_PLAN.md` TESTs 23–26. |
| Revoking an **active** credential mid-connection, live | ⚠️ Unit-tested (`CredentialStoreTests`) but not re-verified end-to-end live in this pass, since doing so would require adding a test-only endpoint to leak the plaintext credential externally, which was deliberately not built |

**In short:** the agent now has a full, working, authenticated enrollment story on top of
its already-working WebSocket protocol and Windows-Service deployment/recovery story —
but the backend it enrolls against is still the local reference stand-in, not the real
CloudOrc backend. Building the real backend (with a persistent database, real TLS
certificate, and credential rotation) is the one remaining "prove it for real" step; see
[docs/FUTURE_BACKEND_INTEGRATION.md](docs/FUTURE_BACKEND_INTEGRATION.md) and
[docs/ENROLLMENT.md](docs/ENROLLMENT.md) for exactly what's left.

---

## 6. Deploying To Another Server — Two Supported Paths

There are now **two fully-supported, independent ways** to get the agent onto a server.
Both are real and tested; pick based on how many servers you're deploying to.

### 6.1 Recommended for most cases: the GitHub installer (`CloudOrcAgentSetup.exe`)

One line, on an elevated PowerShell prompt on the target server:

```powershell
irm https://raw.githubusercontent.com/varunpsingh74358/Server-Agent/main/install-cloudorc.ps1 | iex
```

This downloads the **latest** release (currently `v1.1.0`), verifies its SHA256, installs
both agents as Windows Services, and — if you pass a token — enrolls automatically:

```powershell
# Explicit version + repo (no local file edit needed):
.\install-cloudorc.ps1 -RepositoryOwner "varunpsingh74358" -RepositoryName "Server-Agent" -Version v1.1.0

# With enrollment (one real, generated token - never a placeholder):
CloudOrcAgentSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART --token "ENR-..."
```

Full detail: [docs/INSTALLATION.md](docs/INSTALLATION.md) and
[docs/ENROLLMENT.md](docs/ENROLLMENT.md).

**Checking what's currently installed on a server:**

```powershell
# Installed version
Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{944FC679-C8D0-45F9-8B9B-5F3A1E9259AC}_is1" | Select DisplayName, DisplayVersion

# Or directly from the exe
(Get-Item "C:\Program Files\CloudOrc\Agents\ControlAgent\CloudOrc.ControlAgent.exe").VersionInfo.ProductVersion

# Whether it's enrolled
Test-Path "C:\ProgramData\CloudOrc\ControlAgent\enrollment.dat"

# Service status
sc.exe query CloudOrcControlAgent
sc.exe query CloudOrcWatchdogAgent
```

**Upgrading** an already-installed server: run the exact same one-liner again — it
upgrades in place, preserves existing configuration/enrollment, and never creates
duplicate services (verified live, §3.9).

**Uninstalling:**

```powershell
& "C:\Program Files\CloudOrc\Agents\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

### 6.2 Alternative: copy the self-contained publish folders directly (no installer)

Still fully supported for cases where you don't want to go through GitHub Releases at
all - e.g. an air-gapped server, or just copying files by hand.

**Which folders to copy** (in full - every file inside is required):

```
E:\CloudOrcAgent\publish\win-x64\ControlAgent\
E:\CloudOrcAgent\publish\win-x64\WatchdogAgent\
```

Self-contained - the target server needs **no .NET SDK, no .NET runtime, no Visual
Studio, no VS Code, no Node.js, no Docker**. Regenerate if needed:

```powershell
dotnet publish src\CloudOrc.ControlAgent\CloudOrc.ControlAgent.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64\ControlAgent
dotnet publish src\CloudOrc.WatchdogAgent\CloudOrc.WatchdogAgent.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64\WatchdogAgent
```

**Where to place them:** any local path (avoid `Program Files` to sidestep ACL friction),
e.g. `E:\CloudOrc\ControlAgent\`, `E:\CloudOrc\WatchdogAgent\`.

**Quick manual check first:**

```powershell
cd E:\CloudOrc\ControlAgent
.\CloudOrc.ControlAgent.exe
```

```powershell
'{ "commandId": "test-1", "script": "Get-Date", "timeoutSeconds": 30 }' | Set-Content "C:\ProgramData\CloudOrc\ControlAgent\commands\test-1.json"
Get-Content "C:\ProgramData\CloudOrc\ControlAgent\results\test-1.result.json"
```

**As a Windows Service** (scripted):

```powershell
.\install-agent.ps1 -InstallRoot "E:\CloudOrc"
```

**Manual equivalent:**

```powershell
sc.exe create CloudOrcControlAgent  binPath= "E:\CloudOrc\ControlAgent\CloudOrc.ControlAgent.exe"   DisplayName= "CloudOrc Control Agent"  start= auto
sc.exe create CloudOrcWatchdogAgent binPath= "E:\CloudOrc\WatchdogAgent\CloudOrc.WatchdogAgent.exe" DisplayName= "CloudOrc Watchdog Agent" start= auto
sc.exe start CloudOrcControlAgent
sc.exe start CloudOrcWatchdogAgent
sc.exe query CloudOrcControlAgent
sc.exe query CloudOrcWatchdogAgent
```

**Stop/remove:**

```powershell
sc.exe stop CloudOrcWatchdogAgent
sc.exe stop CloudOrcControlAgent
sc.exe delete CloudOrcWatchdogAgent
sc.exe delete CloudOrcControlAgent
```

or `scripts\uninstall-agent.ps1` (add `-RemoveFiles`/`-RemoveData` for full removal - both
retained by default).

Logs: `C:\ProgramData\CloudOrc\ControlAgent\logs\` and `...\WatchdogAgent\logs\`, whether
run as a console app or a service, either deployment path.

### 6.3 Connecting to your own backend/code — two ways, both supported

**Way 1 — Enrollment (recommended, zero manual config):** generate a token from your
backend (or, for now, from the reference `tools/CloudOrc.AgentTestServer`) and install
with `--token "ENR-..."` as shown in §6.1. Nothing in `appsettings.json` needs editing.
See [docs/ENROLLMENT.md](docs/ENROLLMENT.md) for the exact request/response contract your
backend needs to implement (`POST /api/enroll`, WebSocket bearer-credential validation).

**Way 2 — Manual configuration (still fully supported, e.g. for quick local testing):**
edit `appsettings.json` directly:

```json
{
  "BackendConnection": {
    "Enabled": true,
    "Url": "wss://your-backend-domain/agent",
    "DevelopmentAllowInsecureWs": false
  },
  "AgentIdentity": {
    "AgentId": "your-agent-id",
    "ServerId": "your-server-id"
  }
}
```

then `Restart-Service CloudOrcControlAgent`. **Your backend must speak the protocol**
already implemented in `CloudOrc.Agent.Contracts.Protocol` — `HelloMessage`,
`CommandMessage`, `CommandStatusMessage`, `CommandResultMessage`, `HeartbeatMessage`,
`TelemetryMessage`, `PingMessage`. `tools/CloudOrc.AgentTestServer/AgentConnectionHandler.cs`
is a complete, readable reference implementation of the backend side.

For local/test connection before your backend is fully ready: `ws://` + `DevelopmentAllowInsecureWs: true`.
For production: `wss://`, leave `DevelopmentAllowInsecureWs: false` (refused otherwise).
Local file mode (`ControlAgent.LocalFileModeEnabled`) can stay `true` at the same time as
`BackendConnection.Enabled` — both sources feed the same queue/executor with zero
interference, confirmed live.

**Either way, there is still no production-grade authentication beyond what enrollment
already provides** — see [docs/FUTURE_BACKEND_INTEGRATION.md](docs/FUTURE_BACKEND_INTEGRATION.md)
for exactly what a real production backend still needs (persistent database, RBAC, audit
logs, credential rotation schedule).

---

## 7. How To Test It Yourself (Quick Reference)

**Local file mode (unchanged):**

```powershell
cd E:\CloudOrcAgent\src\CloudOrc.ControlAgent
dotnet run

# second terminal
'{ "commandId": "test-1", "script": "Get-Date", "timeoutSeconds": 30 }' | Set-Content "C:\ProgramData\CloudOrc\ControlAgent\commands\test-1.json"
Get-Content "C:\ProgramData\CloudOrc\ControlAgent\results\test-1.result.json"
```

**WebSocket mode (manual config, no enrollment):**

```powershell
# Terminal 1
cd E:\CloudOrcAgent\tools\CloudOrc.AgentTestServer
dotnet run

# Terminal 2
cd E:\CloudOrcAgent\src\CloudOrc.ControlAgent
dotnet run --BackendConnection:Enabled=true --BackendConnection:Url=ws://localhost:5299/agent --BackendConnection:DevelopmentAllowInsecureWs=true
```

Then in Terminal 1: `send Get-Date`. Full walkthrough:
[docs/BACKEND_WEBSOCKET_TESTING.md](docs/BACKEND_WEBSOCKET_TESTING.md).

**Enrollment mode (generates and uses a real token):**

```powershell
# Terminal 1 - reference backend
cd E:\CloudOrcAgent\tools\CloudOrc.AgentTestServer
dotnet run

# Terminal 2 - issue a token
Invoke-RestMethod -Uri "http://localhost:5299/api/enrollment-tokens" -Method Post -ContentType "application/json" -Body "{}"
# -> { "token": "ENR-..." }

# Terminal 3 - enroll an already-built Control Agent
cd E:\CloudOrcAgent\src\CloudOrc.ControlAgent\bin\Debug\net10.0-windows
.\CloudOrc.ControlAgent.exe enroll --token "ENR-..."

# then run it normally - it will use the enrolled identity/backend automatically
.\CloudOrc.ControlAgent.exe
```

Full detail with real confirmed output: [docs/ENROLLMENT.md](docs/ENROLLMENT.md).

**Deploying the self-contained package or the installer to another server:** see §6
above, or the full versions in [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md),
[docs/INSTALLATION.md](docs/INSTALLATION.md), and
[docs/DEPLOYMENT_TEST_PLAN.md](docs/DEPLOYMENT_TEST_PLAN.md).

---

## 8. Key File Locations

| Purpose | Location |
|---|---|
| Control Agent data root | `C:\ProgramData\CloudOrc\ControlAgent\` |
| → drop new commands here | `...\ControlAgent\commands\` |
| → see results here | `...\ControlAgent\results\` |
| → Control Agent logs | `...\ControlAgent\logs\` |
| → **enrolled identity/credential (DPAPI-encrypted)** | `...\ControlAgent\enrollment.dat` |
| Watchdog logs | `C:\ProgramData\CloudOrc\WatchdogAgent\logs\` |
| Control Agent config (source) | `src\CloudOrc.ControlAgent\appsettings.json` |
| Watchdog config (source) | `src\CloudOrc.WatchdogAgent\appsettings.json` |
| Local test server config | `tools\CloudOrc.AgentTestServer\appsettings.json` |
| Control Agent self-contained publish | `publish\win-x64\ControlAgent\` |
| Watchdog self-contained publish | `publish\win-x64\WatchdogAgent\` |
| **Installer source** | `installer\CloudOrcAgentSetup.iss` → builds `installer\Output\CloudOrcAgentSetup.exe` |
| **Enrollment code (agent side)** | `src\CloudOrc.ControlAgent\Enrollment\` (`EnrollmentClient`, `EnrolledStateStore`, `EnrollmentCommandLine`), `src\CloudOrc.Agent.Contracts\Enrollment\` (shared models/token codec) |
| **Reference enrollment backend** | `tools\CloudOrc.AgentTestServer\Enrollment\` (`EnrollmentTokenStore`, `CredentialStore`) |
| Deployment install/uninstall/health-check/package scripts | `scripts\install-agent.ps1`, `uninstall-agent.ps1`, `health-check.ps1`, `package-agent.ps1` |
| One-line bootstrap installer | `install-cloudorc.ps1` (repo root) |
| GitHub Actions workflows | `.github\workflows\build-agent-release.yml`, `build-installer.yml` |
| Full deployment guide | `docs\DEPLOYMENT.md` |
| Installer guide | `docs\INSTALLATION.md` |
| **Enrollment architecture guide** | `docs\ENROLLMENT.md` |
| Deployment test checklist | `docs\DEPLOYMENT_TEST_PLAN.md` |
| **Live public repository** | `https://github.com/varunpsingh74358/Server-Agent` |

---

## 9. NuGet Packages

| Package | Reason |
|---|---|
| `Microsoft.PowerShell.SDK` | Generic PowerShell execution engine |
| `Microsoft.Extensions.Hosting.WindowsServices` | Windows Service hosting for both agents |
| `System.ServiceProcess.ServiceController` | Watchdog's service query/restart |
| `System.Diagnostics.PerformanceCounter` | CPU usage percentage in TELEMETRY |
| `System.Security.Cryptography.ProtectedData` | **New** — DPAPI encryption for `EnrolledStateStore` |
| `Serilog.*` | Structured console + rolling file logging |

No database, no message broker, no cloud SDK, no Docker — everything runs from a plain
Windows Server with only these packages bundled in (fully bundled in the self-contained
publish — no separate install step needed on the target machine).

---

## 10. Recommended Next Steps

1. ~~Deploy to an actual second Windows Server~~ — **done** (§3.9): the installer has been
   run against a real second server via the public GitHub Release.
2. ~~Build an environment-agnostic enrollment architecture~~ — **done** (§3.10, §2a): one
   token, one binary, one installer, zero hardcoded backend URLs, verified live.
3. **Stand up a real (or real-shaped) production enrollment backend** — a persistent
   database replacing the reference `EnrollmentTokenStore`/`CredentialStore`, admin
   authentication on the token-issuing endpoint, and a `wss://` endpoint with a real
   trusted certificate. Nothing on the agent/installer side needs to change for this —
   see [docs/ENROLLMENT.md](docs/ENROLLMENT.md) "What a real production backend still
   needs to add."
4. **Credential rotation** — today's issued credential is long-lived (revocable, not
   auto-rotated). Add a rotation endpoint/message the agent calls on a timer.
5. **A genuine OS reboot test** on a target server — the reboot-survival behavior has
   only been proxied via real service restarts so far (§3.10 item 9), never an actual
   reboot.
6. When ready, start **Phase 3b**: point enrollment at the real CloudOrc backend and add
   RBAC, audit logs, command expiry, and replay protection — see
   [docs/FUTURE_BACKEND_INTEGRATION.md](docs/FUTURE_BACKEND_INTEGRATION.md) for the exact
   remaining gap list.

---

## 11. Full Documentation Index

- [README.md](README.md) — overview, quick start
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — full component design, honest limitations
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) — build/run/configure
- [docs/TESTING.md](docs/TESTING.md) — every automated + manual test scenario
- [docs/BACKEND_WEBSOCKET_TESTING.md](docs/BACKEND_WEBSOCKET_TESTING.md) — the WebSocket layer, step by step, with confirmed live output, including cross-machine (LAN) testing
- [docs/WINDOWS_SERVICE_INSTALLATION.md](docs/WINDOWS_SERVICE_INSTALLATION.md) — publish & install as a service (framework-dependent variant)
- [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) — self-contained package deployment to another server, end to end
- [docs/DEPLOYMENT_TEST_PLAN.md](docs/DEPLOYMENT_TEST_PLAN.md) — the fixed 26-test deployment validation checklist and what's already verified vs. manual
- [docs/INSTALLATION.md](docs/INSTALLATION.md) — the `CloudOrcAgentSetup.exe` installer: build, GitHub Actions/release, one-line install, upgrade, uninstall, private-repo limitations
- [docs/ENROLLMENT.md](docs/ENROLLMENT.md) — **the environment-agnostic enrollment architecture: token design, full flow, security properties, what a real backend needs to add**
- [docs/FUTURE_BACKEND_INTEGRATION.md](docs/FUTURE_BACKEND_INTEGRATION.md) — what's left before the real backend

---

## 12. Using CloudOrc Agent With Your Own Backend Code

Everything above gets the agent installed, enrolled, and running. This section is the
practical answer to **"the agent is built and installable — how do I actually make my own
backend/project talk to it?"** — both right now, on your local machine, and after your own
backend is itself deployed somewhere.

### 12.1 The one fact that drives everything below

The Control Agent's WebSocket client, including enrollment and bearer-credential
authentication, is **fully built and already works** — verified live in §3.3, §3.9, and
§3.10, against `tools/CloudOrc.AgentTestServer` (a small reference backend, now including
a reference enrollment implementation). What does **not** exist yet is the **real**
backend on the other end. "Using this with your code" means: **your backend needs to
speak the same protocol and implement the same enrollment contract** that
`tools/CloudOrc.AgentTestServer` already does — there is no separate "integration API" to
learn beyond that.

The runtime protocol lives in `CloudOrc.Agent.Contracts.Protocol` (plain JSON over
WebSocket):

| Message | Direction | Purpose |
|---|---|---|
| `HELLO` | Agent → Backend | Sent once on connect: `agentId`, `serverId`, real `machineId`/`machineName`, agent version |
| `HEARTBEAT` | Agent → Backend | Periodic: liveness + current command status |
| `TELEMETRY` | Agent → Backend | Periodic: CPU/memory/disks/uptime |
| `COMMAND` | Backend → Agent | A script to run (`commandId`, `script`, `timeoutSeconds`) |
| `COMMAND_STATUS` | Agent → Backend | `Queued` → `Running` for a given `commandId` |
| `COMMAND_RESULT` | Agent → Backend | Terminal result: `Success`/`Failed`/`Timeout`/`Cancelled` + output/error |
| `PING` | Backend → Agent | Optional keepalive |

The enrollment contract lives in `CloudOrc.Agent.Contracts.Enrollment` (plain JSON over
HTTPS, one endpoint):

| Message | Direction | Purpose |
|---|---|---|
| `EnrollmentRequest` | Agent → Backend, `POST` to the token's embedded URL | `secret` (the token's one-time value), `machineId`, `machineName`, `agentVersion` |
| `EnrollmentResponse` | Backend → Agent | `agentId`, `serverId`, `backendUrl`, `credential` |

Plus: the backend must validate `Authorization: Bearer <credential>` on the WebSocket
handshake for every subsequent connection from an enrolled agent.

Read `tools/CloudOrc.AgentTestServer/AgentConnectionHandler.cs` (protocol) and
`tools/CloudOrc.AgentTestServer/Enrollment/` (enrollment) for complete, working reference
implementations of both — intentionally small and readable.

### 12.2 Right now, on your local machine (development)

Two ways to integrate, both fully supported:

**A. With enrollment (recommended — matches how it'll work in production):**

1. Implement `POST /api/enroll` on your dev backend, modeled on
   `tools/CloudOrc.AgentTestServer/Enrollment/EnrollmentTokenStore.cs` +
   the `/api/enroll` route in `Program.cs`.
2. Generate a token for your own backend's URL:
   ```csharp
   var token = CloudOrc.Agent.Contracts.Enrollment.EnrollmentToken.Encode("https://your-dev-backend/api/enroll", "a-random-secret-you-generate-and-remember");
   ```
   (or issue one via an endpoint on your own backend that does the same thing).
3. Enroll: `CloudOrc.ControlAgent.exe enroll --token "ENR-..."`.
4. Run normally — it connects to your backend automatically, no `appsettings.json`
   editing.

**B. Manual configuration (quicker for a first smoke test):**

```powershell
cd src\CloudOrc.ControlAgent
dotnet run --BackendConnection:Enabled=true --BackendConnection:Url=ws://localhost:<your-backend-port>/agent --BackendConnection:DevelopmentAllowInsecureWs=true
```

Either way: everything already proven in this repo works identically against your
backend once it speaks the protocol — multiple commands, timeouts, failures,
reconnect-with-backoff, and local file commands continuing to work at the same time with
zero interference (§3.3). Keep `tools/CloudOrc.AgentTestServer` around as a
reference/fallback while building — useful for isolating "is this an agent problem or a
my-backend problem" by swapping back to it.

### 12.3 After your backend is deployed (not local anymore)

1. **Get the agent onto each server** — the one-line installer bootstrap (§6.1), with or
   without `--token` depending on whether your backend's enrollment endpoint is ready yet.
2. **Enroll each server** against your real backend's enrollment endpoint (a real,
   HTTPS-reachable one this time, issuing a `wss://` `backendUrl` in its response) — or,
   if not enrolling yet, fall back to manual configuration (§6.3 Way 2) with a **distinct
   `AgentId` per server**.
3. **There is still no production-grade credential rotation, RBAC, or audit logging** —
   enrollment gives you real per-agent identity and authentication, but a compromised
   credential must currently be revoked manually (`CredentialStore.Revoke` equivalent on
   your real backend) rather than automatically rotated. See
   [docs/FUTURE_BACKEND_INTEGRATION.md](docs/FUTURE_BACKEND_INTEGRATION.md) for the exact
   remaining gap list.
4. **Rolling out to many servers**: repeat step 1 (the one-liner, with a fresh token per
   server) on each one. The installer is idempotent — re-running it on an
   already-installed server upgrades in place without duplicating services or
   re-enrolling unnecessarily (only pass `--token` again if you actually want to
   re-enroll that specific server against a different backend/identity).

---

## 13. GitHub Repository & Release History

**Repository:** `https://github.com/varunpsingh74358/Server-Agent` (public)

| Tag | What it contains | Status |
|---|---|---|
| `v1.0.0` | Self-contained ZIP + `CloudOrcAgentSetup.exe` installer, no enrollment (manual `appsettings.json` config only) | ✅ Released, verified live (§3.9) |
| `v1.1.0` | Adds the full environment-agnostic enrollment architecture (`--token` support) | ✅ Released, verified live (§3.10), after fixing a real CI build failure (§3.8 item 8, §3.11) |

**First-time repo setup commands used** (for reference — already done):

```powershell
git init
git add <files>
git commit -m "..."
git branch -M main
git remote add origin https://github.com/varunpsingh74358/Server-Agent.git
git push -u origin main
```

**Creating a new release** (the pattern used for both `v1.0.0` and `v1.1.0`):

```powershell
git add -A
git commit -m "..."
git push origin main
git tag vX.Y.Z
git push origin vX.Y.Z
```

Pushing the tag triggers both GitHub Actions workflows, which build, run all 136 tests,
publish both agents self-contained, build the installer, and — only for a `v*` tag —
create a GitHub Release with all four assets attached.

**Moving a tag after a fix** (used twice in this project's history — once for the release
race-condition bug, once for the `.gitignore` bug):

```powershell
git tag -f vX.Y.Z
git push origin vX.Y.Z --force
```

Only ever done immediately after confirming, via a fresh clone, that the new commit
actually builds and tests clean — never blindly.
