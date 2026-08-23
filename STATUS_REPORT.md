# CloudOrc Windows Agents — Project Status Report

**Last updated:** 2026-08-23
**Phase 1 (local file execution):** **COMPLETE and VERIFIED**
**Phase 2 (local WebSocket testing layer):** **COMPLETE and VERIFIED**
**Phase 2.5 (final validation + self-contained deployment packaging):** **COMPLETE and VERIFIED**
**Phase 3 (real backend connection + production security):** **NOT STARTED**

--- 

## 1. What This Project Is

Two .NET 10 Worker Services that will eventually replace WinRM for running PowerShell
commands on Windows Servers from an existing backend:

1. **CloudOrc Control Agent** — executes PowerShell commands generically (no command is
   hardcoded — any valid script can be sent), and can optionally receive those commands
   over a WebSocket connection instead of (or alongside) local JSON files.
2. **CloudOrc Watchdog Agent** — monitors the Control Agent and restarts it in a
   controlled, rate-limited way if it becomes unhealthy. Confirmed to never confuse a
   disconnected backend with a dead Control Agent, and now **confirmed to perform a real
   Windows-Service restart**, not just a documented/theoretical one (see §3.5).

A third piece, `tools/CloudOrc.AgentTestServer`, is a **local, dev-only stand-in for the
real CloudOrc backend** — it lets the whole agent↔backend protocol be exercised before
touching production. It is not part of, and never ships with, the production agent.

Nothing in this project connects to the real CloudOrc backend yet, and no production
security (TLS in production, enrollment, credentials) exists yet — see section 5.

---

## 2. What Has Been Built

| Item | Status |
|---|---|
| Solution `CloudOrc.WindowsAgents.sln` — 6 projects | ✅ Done |
| `CloudOrc.Agent.Contracts` — shared models, interfaces, wire protocol | ✅ Done |
| `CloudOrc.ControlAgent` — full Worker Service | ✅ Done |
| `CloudOrc.WatchdogAgent` — full Worker Service | ✅ Done |
| `tools/CloudOrc.AgentTestServer` — local backend stand-in (dev/test only) | ✅ Done |
| `CloudOrc.ControlAgent.Tests` — 80 automated tests | ✅ Done |
| `CloudOrc.WatchdogAgent.Tests` — 15 automated tests | ✅ Done |
| Full documentation set (`docs/`, 8 files) | ✅ Done |
| Self-contained `win-x64` publish for both agents | ✅ **New — produced and verified this phase** |
| `scripts/install-agent.ps1` / `scripts/uninstall-agent.ps1` | ✅ **New — written and run for real this phase** |
| Real Windows Service install + real Watchdog-triggered restart | ✅ **New — performed for real this phase, not just documented** |

### Control Agent — feature checklist

| Feature | Status |
|---|---|
| Generic PowerShell execution (no hardcoded commands) | ✅ Working |
| Local JSON file command source (`commands\`) | ✅ Working |
| WebSocket command source (receives COMMAND messages) | ✅ Working, verified live |
| Both sources active simultaneously, sharing one queue/executor | ✅ Verified live — no interference |
| Duplicate protection (per-source + cross-source via shared on-disk signal) | ✅ Working (at-least-once, not exactly-once — see docs/ARCHITECTURE.md) |
| Sequential execution (one command at a time, regardless of source) | ✅ Working |
| Timeout handling (actually stops the pipeline) | ✅ Working — reconfirmed at 5005ms for a 5s timeout in this phase |
| HELLO / HEARTBEAT / TELEMETRY over WebSocket | ✅ Working, verified live again this phase |
| Automatic reconnect with exponential backoff | ✅ Working — 2s→4s→8s→16s→32s reconfirmed live this phase |
| Stays alive & keeps processing local commands during a backend outage | ✅ Working — reconfirmed live this phase |
| Refuses to start with an insecure `ws://` URL unless explicitly allowed | ✅ Working |
| Local health reporting via Named Pipe | ✅ Working |
| **Runs as a real installed Windows Service (`CloudOrcControlAgent`)** | ✅ **New — actually installed, started, and exercised as a real service this phase (previously only console-mode had been run)** |

### Watchdog Agent — feature checklist

| Feature | Status |
|---|---|
| Checks Control Agent Windows Service status | ✅ Working |
| Checks Control Agent health over Named Pipe | ✅ Working |
| Logs backend connection state for visibility only (never used in decision logic) | ✅ Reconfirmed: stayed `HEALTHY` through a full backend outage this phase |
| Consecutive-failure counter (ignores single blips) | ✅ Working |
| Attempts recovery only after threshold (default 3) | ✅ Working |
| **Restart via `ServiceController` against a real installed service** | ✅ **New — a real `CloudOrcControlAgent` Windows Service was stopped, the Watchdog detected it (1/3 → 2/3 → 3/3), issued a real restart, and logged "Recovery succeeded; Control Agent is healthy again." Confirmed independently via `sc.exe query`.** |
| Restart rate limiting + exponential backoff | ✅ Working (unit tested; live backoff suppression also reconfirmed in console mode) |
| **Runs as a real installed Windows Service (`CloudOrcWatchdogAgent`)** | ✅ **New — actually installed, started, and used to perform a real recovery this phase** |

---

## 3. Verification Performed (Not Just Claimed — Actually Tested)

This phase re-verified the entire system end-to-end **again**, from a clean state, and
additionally performed the one thing every earlier pass had explicitly flagged as
outstanding: **installing both agents as real Windows Services and watching the Watchdog
actually restart the Control Agent through the Service Control Manager**, not just the
console-mode "service not installed" fallback path.

### 3.1 Build + automated tests

```
dotnet restore CloudOrc.WindowsAgents.sln
dotnet build CloudOrc.WindowsAgents.sln
dotnet test CloudOrc.WindowsAgents.sln
```

**Result: 0 Warnings / 0 Errors, 95 / 95 tests passed** (15 Watchdog + 80 Control Agent).

### 3.2 Local file mode (console mode, fresh pass)

| Test | Command | Result |
|---|---|---|
| Basic execution | `Get-Date` | `Success` |
| Multi-line output | `Get-Service \| Select-Object -First 5` | `Success`, 5 services |
| Process listing | `Get-Process \| Select-Object -First 5` | `Success` |
| Drive listing | `Get-PSDrive` | `Success` |
| Network cmdlets | `Get-NetIPAddress`, `Test-NetConnection localhost` | `Failed` — **this specific machine's PowerShell execution policy blocks the `NetTCPIP` module**; the executor correctly captured the real error and stayed alive. This is an environment condition of the validation machine, not an agent defect. |
| Error handling | `Get-Service -Name "DefinitelyDoesNotExist"` | `Failed`, real error message, agent kept running |
| Timeout handling | `Start-Sleep -Seconds 30`, 5s timeout | `Timeout` at **5005ms** |
| Duplicate protection | Same `commandId` resubmitted after completion | Diverted to `failed\{id}.duplicate-*.json`; original result untouched |
| Recovery after failure | Fresh `Get-Date` after the above | `Success` |

### 3.3 WebSocket mode (fresh pass, against `tools/CloudOrc.AgentTestServer`)

| Test | Result |
|---|---|
| HELLO handshake | Real `machineId` (Windows `MachineGuid`), real `machineName` |
| HEARTBEAT (15s interval) | `status=HEALTHY, workerAlive=true` every tick |
| TELEMETRY (10s interval) | Real CPU%, memory, **all 10 mounted drives**, uptime |
| `Get-Date`, `Get-Service` over WebSocket | `COMMAND_STATUS: Queued→Running` then `COMMAND_RESULT: Success` |
| Failing command over WebSocket | `COMMAND_RESULT: Failed`, real error |
| `Start-Sleep -Seconds 30`, 4s timeout, over WebSocket | `COMMAND_RESULT: Timeout` at 4017ms |
| Backend outage (test server killed) | Backoff confirmed: 2s → 4s → 8s → 16s → 32s |
| Local file command during the outage | `Success` in 17ms — proves local processing is independent of backend connectivity |
| Watchdog throughout the outage | Logged `status=HEALTHY` every cycle; `backendConnectionState` moved through `Connecting`/`Reconnecting` while `status` never changed |
| Test server restarted | Control Agent reconnected automatically, sent a fresh HELLO, and the `COMMAND_RESULT` generated during the outage was delivered immediately |

### 3.4 Console-mode Watchdog recovery (Control Agent process killed, no service installed)

| Step | Result |
|---|---|
| Control Agent process killed | Watchdog health checks began failing: 1/3 → 2/3 → 3/3 |
| Recovery attempt at threshold | Triggered correctly; failed gracefully — `"service 'CloudOrcControlAgent' is not installed"` (expected in console mode) |
| Backoff after the failed attempt | Correctly suppressed on the next cycle |
| Watchdog process itself | Never crashed |

### 3.5 Real Windows Service installation and real Watchdog restart — NEW THIS PHASE

This is the one scenario every previous verification pass explicitly deferred
("Windows Service actually installed & restart-tested — documented, but not performed").
It was performed for real in this phase, with the user's explicit approval to modify the
local Service Control Manager (and to remove the services again afterward):

1. Published self-contained `win-x64` builds of both agents.
2. `sc.exe create CloudOrcControlAgent ...` / `sc.exe create CloudOrcWatchdogAgent ...` —
   both `[SC] CreateService SUCCESS`.
3. Started both — `sc.exe query` confirmed `STATE: 4 RUNNING` for both.
4. Confirmed the Watchdog, now pointed at a **real installed service**, logged
   `Control Agent service 'CloudOrcControlAgent' status: Running.` (not `NotInstalled`
   as in console mode) and `status=HEALTHY`.
5. Submitted a command to the **service-hosted** Control Agent (not console mode) — got a
   real `Success` result, and confirmed logs were written to
   `C:\ProgramData\CloudOrc\ControlAgent\logs\` (no console attached).
6. `sc.exe stop CloudOrcControlAgent` — simulated a real failure.
7. Watchdog logged consecutive failures 1/3 → 2/3 → 3/3, then:
   ```
   Consecutive failure threshold reached; attempting recovery of service 'CloudOrcControlAgent'.
   Starting service CloudOrcControlAgent.
   Restart issued; waiting 15s before re-checking health.
   Recovery succeeded; Control Agent is healthy again.
   ```
   This is a **real `ServiceController.Start()` call against a real Windows Service**,
   independently confirmed via `sc.exe query CloudOrcControlAgent` showing `RUNNING`
   again.
8. Both services were stopped (`sc.exe stop`) and removed (`sc.exe delete`) afterward, per
   the agreed plan, leaving the development machine in a clean state (no persistent
   service registration left behind).

### 3.6 Self-contained publish verification

```
dotnet publish src\CloudOrc.ControlAgent\CloudOrc.ControlAgent.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64\ControlAgent
dotnet publish src\CloudOrc.WatchdogAgent\CloudOrc.WatchdogAgent.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64\WatchdogAgent
```

Verified, not assumed: both output folders contain `hostfxr.dll`/`coreclr.dll` and all
`Microsoft.PowerShell.SDK` dependencies (132MB and 80MB respectively), and each `.exe` was
**launched directly from its publish folder with no `dotnet run` and no SDK involved**,
then used to process a real command successfully.

### 3.7 Deployment scripts verified live

`scripts/install-agent.ps1` and `scripts/uninstall-agent.ps1` were run for real (not just
syntax-checked) against a scratch install root. A real bug was found and fixed during this
(`$PSScriptRoot` is not populated inside a parameter *default-value* expression on this
PowerShell host — fixed by resolving it in the script body instead). After the fix, a full
install → verify-running → uninstall → verify-removed cycle succeeded.

### 3.8 Real bugs found and fixed (across all sessions)

1. **(Phase 1)** `PowerShell.Stop()` was sometimes wired up before the pipeline had
   actually started, so external cancellation had no effect.
2. **(Phase 2)** The local test server's console-input loop crashed on end-of-input in a
   non-interactive context — fixed to only shut down on an explicit `exit`/`quit`.
3. **(Phase 2.5, this phase)** `scripts/install-agent.ps1` used `$PSScriptRoot` inside a
   parameter default value, which is empty on this PowerShell host at parameter-binding
   time — fixed by resolving the script root inside the script body instead.

---

## 4. Environment Issues Hit and Resolved (informational, not code defects)

1. A build-cache folder got locked by VS Code's C# language server — fixed by restarting
   that process.
2. Leftover `obj_verify` folders from an earlier verification build caused duplicate
   compilation — fixed by deleting them.
3. **(This phase)** This validation machine's PowerShell execution policy blocks loading
   the `NetTCPIP` module, so `Get-NetIPAddress`/`Test-NetConnection` return `Failed` here
   — this is a property of this specific machine's execution policy, not the agent. On a
   target server with a different execution policy, the exact same generic executor will
   run those commands successfully with no code change needed.

Both fully resolved / understood; unaffected by everything built since.

---

## 5. What Is NOT Ready Yet (By Design — Not a Gap)

| Item | Status |
|---|---|
| Connection to the **real** CloudOrc backend | ❌ Not built — only tested against the local `tools/CloudOrc.AgentTestServer` stand-in |
| Production TLS (`wss://` against a real certificate) | ❌ Not tested — the code supports it, but only `ws://localhost` has actually been run |
| Agent enrollment / issued credentials / RBAC / audit logs | ❌ Not built (explicitly out of scope for this phase) |
| Windows Service actually installed & restart-tested | ✅ **Now performed for real** (see §3.5) — this item is no longer outstanding |
| Database / persistent command history | ❌ Not built (explicitly out of scope) |
| Live incremental output streaming (only final result today) | ❌ Not built |
| Reboot-survival (services auto-starting after a server reboot) | ⚠️ Not tested in this phase — requires an actual reboot of a target machine, deliberately not done to this development machine. See `docs/DEPLOYMENT_TEST_PLAN.md` TESTs 23–26. |

**In short:** the agent now has a full, working WebSocket protocol AND a confirmed real
Windows-Service deployment/recovery story — but only against the local test stand-in and
this development machine respectively. Pointing it at the real backend and a genuinely
separate target server are the two remaining "prove it for real" steps; see
[docs/FUTURE_BACKEND_INTEGRATION.md](docs/FUTURE_BACKEND_INTEGRATION.md) and
[docs/DEPLOYMENT_TEST_PLAN.md](docs/DEPLOYMENT_TEST_PLAN.md) for exactly what's left.

---

## 6. Deploying To Another Server (what to copy, where, and how to run it)

This section is the practical answer to "I want to copy this to another Windows Server
and test it independently before wiring up the real backend." Full detail, including
troubleshooting and required permissions, is in
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) — this is the condensed version.

### 6.1 Which folders to copy

Copy these two folders **in full** (not just the `.exe` — every file inside is required,
including the bundled .NET runtime and PowerShell SDK):

```
E:\CloudOrcAgent\publish\win-x64\ControlAgent\
E:\CloudOrcAgent\publish\win-x64\WatchdogAgent\
```

These are **self-contained** publishes — the target server needs **no .NET SDK, no .NET
runtime, no Visual Studio, no VS Code, no Node.js, no Docker**. Only Windows itself is
required (both agents use Windows-only APIs).

If these folders don't exist yet (e.g. after further code changes), regenerate them from
the repo root:

```powershell
dotnet publish src\CloudOrc.ControlAgent\CloudOrc.ControlAgent.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64\ControlAgent
dotnet publish src\CloudOrc.WatchdogAgent\CloudOrc.WatchdogAgent.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64\WatchdogAgent
```

### 6.2 Where to place them on the target server

Any local path works; avoid `Program Files` to sidestep extra ACL friction. Recommended:

```
E:\CloudOrc\ControlAgent\
E:\CloudOrc\WatchdogAgent\
```

Copy with `robocopy`, a zip transfer, or a file share — whatever preserves the full folder
tree intact.

### 6.3 How to run it — quick manual check first

Before installing as a service, it's worth running each `.exe` directly once to confirm it
launches on the new machine (exactly as was verified on this development machine in §3.6):

```powershell
cd E:\CloudOrc\ControlAgent
.\CloudOrc.ControlAgent.exe
```

Drop a test command in a second window/terminal:

```powershell
'{ "commandId": "test-1", "script": "Get-Date", "timeoutSeconds": 30 }' | Set-Content "C:\ProgramData\CloudOrc\ControlAgent\commands\test-1.json"
Get-Content "C:\ProgramData\CloudOrc\ControlAgent\results\test-1.result.json"
```

`Ctrl+C` to stop the console run once confirmed.

### 6.4 How to run it — as a Windows Service (recommended for anything beyond a quick check)

**Scripted** (uses `scripts\install-agent.ps1`, copied alongside the publish folders or
from the repo):

```powershell
.\install-agent.ps1 -InstallRoot "E:\CloudOrc"
```

This copies the files (if not already at `-InstallRoot`), creates both services, and
starts them, printing their status at the end.

**Manual equivalent** (elevated PowerShell/cmd required):

```powershell
sc.exe create CloudOrcControlAgent  binPath= "E:\CloudOrc\ControlAgent\CloudOrc.ControlAgent.exe"   DisplayName= "CloudOrc Control Agent"  start= auto
sc.exe create CloudOrcWatchdogAgent binPath= "E:\CloudOrc\WatchdogAgent\CloudOrc.WatchdogAgent.exe" DisplayName= "CloudOrc Watchdog Agent" start= auto

sc.exe start CloudOrcControlAgent
sc.exe start CloudOrcWatchdogAgent

sc.exe query CloudOrcControlAgent
sc.exe query CloudOrcWatchdogAgent
```

To stop/remove later:

```powershell
sc.exe stop CloudOrcWatchdogAgent
sc.exe stop CloudOrcControlAgent
sc.exe delete CloudOrcWatchdogAgent
sc.exe delete CloudOrcControlAgent
```

or `scripts\uninstall-agent.ps1` (add `-RemoveFiles` and/or `-RemoveData` if you also want
the installed files and/or `C:\ProgramData\CloudOrc\` history deleted — both are retained
by default).

Logs land at `C:\ProgramData\CloudOrc\ControlAgent\logs\` and
`C:\ProgramData\CloudOrc\WatchdogAgent\logs\` whether run as a console app or a service.

### 6.5 How to connect it to your own backend/code

Edit `appsettings.json` **inside the Control Agent's publish/install folder**
(`E:\CloudOrc\ControlAgent\appsettings.json`):

```json
{
  "BackendConnection": {
    "Enabled": true,
    "Url": "wss://your-backend-domain/agent",
    "DevelopmentAllowInsecureWs": false,
    "ConnectTimeoutSeconds": 10,
    "ReconnectInitialDelaySeconds": 2,
    "ReconnectMaximumDelaySeconds": 60,
    "HeartbeatIntervalSeconds": 15,
    "TelemetryIntervalSeconds": 10
  },
  "AgentIdentity": {
    "AgentId": "your-agent-id",
    "ServerId": "your-server-id"
  }
}
```

Restart the `CloudOrcControlAgent` service after editing (`sc.exe stop` then
`sc.exe start`, or `Restart-Service CloudOrcControlAgent`).

**What "connect to your backend" actually requires, honestly:**

1. **Your backend must speak the same protocol** the agent already implements —
   `HelloMessage`, `CommandMessage`, `CommandStatusMessage`, `CommandResultMessage`,
   `HeartbeatMessage`, `TelemetryMessage`, `PingMessage` (all defined in
   `CloudOrc.Agent.Contracts.Protocol`). This is a plain JSON-over-WebSocket protocol —
   your backend needs a WebSocket endpoint that accepts an inbound connection, receives
   the agent's `HELLO`, and can send it `COMMAND` messages, and reads back
   `COMMAND_STATUS`/`COMMAND_RESULT`/`HEARTBEAT`/`TELEMETRY`. `tools/CloudOrc.AgentTestServer`
   in this repo is a small, complete reference implementation of exactly this — read its
   `AgentConnectionHandler.cs` if you want a working example to model your backend
   endpoint on.
2. **For local/test connection to your own backend before it's fully ready**, you can
   point `Url` at a `ws://` endpoint and set `DevelopmentAllowInsecureWs: true` — the
   agent will connect the same way it did against `tools/CloudOrc.AgentTestServer` in
   every test in §3.3.
3. **For a real production connection**, use `wss://` and leave
   `DevelopmentAllowInsecureWs: false` (the safe default) — a `ws://` URL is refused at
   startup otherwise, on purpose.
4. **There is still no authentication/enrollment/credential exchange** —
   `AgentId`/`ServerId` are plain configuration values with no proof of identity behind
   them yet. This is intentional for this phase, not an oversight; see
   [docs/FUTURE_BACKEND_INTEGRATION.md](docs/FUTURE_BACKEND_INTEGRATION.md) for exactly
   what a real credential/enrollment mechanism would need to add, and where it plugs in
   (additively, at the transport layer — none of `ICommandQueue`, `IPowerShellExecutor`,
   `CommandDetectionService`, or `CommandProcessingService` would need to change).
5. Local file mode (`ControlAgent.LocalFileModeEnabled`) can stay `true` at the same time
   as `BackendConnection.Enabled` — both sources feed the same queue/executor with no
   interference, confirmed live in §3.3. Turn it `false` only if you want the agent to
   receive commands exclusively from your backend.

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

**WebSocket mode:**

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

**Deploying the self-contained package to another server:** see §6 above, or the full
version in [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) and
[docs/DEPLOYMENT_TEST_PLAN.md](docs/DEPLOYMENT_TEST_PLAN.md).

---

## 8. Key File Locations

| Purpose | Location |
|---|---|
| Control Agent data root | `C:\ProgramData\CloudOrc\ControlAgent\` |
| → drop new commands here | `...\ControlAgent\commands\` |
| → see results here | `...\ControlAgent\results\` |
| → Control Agent logs | `...\ControlAgent\logs\` |
| Watchdog logs | `C:\ProgramData\CloudOrc\WatchdogAgent\logs\` |
| Control Agent config (source) | `src\CloudOrc.ControlAgent\appsettings.json` |
| Watchdog config (source) | `src\CloudOrc.WatchdogAgent\appsettings.json` |
| Local test server config | `tools\CloudOrc.AgentTestServer\appsettings.json` |
| **Control Agent self-contained publish** | `publish\win-x64\ControlAgent\` (config lives at `...\ControlAgent\appsettings.json` here too) |
| **Watchdog self-contained publish** | `publish\win-x64\WatchdogAgent\` |
| **Deployment install script** | `scripts\install-agent.ps1` |
| **Deployment uninstall script** | `scripts\uninstall-agent.ps1` |
| **Full deployment guide** | `docs\DEPLOYMENT.md` |
| **Deployment test checklist** | `docs\DEPLOYMENT_TEST_PLAN.md` |

---

## 9. NuGet Packages

| Package | Reason |
|---|---|
| `Microsoft.PowerShell.SDK` | Generic PowerShell execution engine |
| `Microsoft.Extensions.Hosting.WindowsServices` | Windows Service hosting for both agents |
| `System.ServiceProcess.ServiceController` | Watchdog's service query/restart |
| `System.Diagnostics.PerformanceCounter` | CPU usage percentage in TELEMETRY |
| `Serilog.*` | Structured console + rolling file logging |

No database, no message broker, no cloud SDK, no Docker — everything runs from a plain
Windows Server with only these packages bundled in (bundled fully in the self-contained
publish — no separate install step needed on the target machine).

---

## 10. Recommended Next Steps

1. **Deploy to an actual second Windows Server** using §6 above and confirm the two
   remaining manual scenarios in `docs/DEPLOYMENT_TEST_PLAN.md` (reboot-survival and
   cross-machine backend connectivity) — these were the only two things this validation
   phase could not exercise on a single development machine.
2. **Stand up a minimal real (or real-shaped) backend endpoint** that speaks the protocol
   in `CloudOrc.Agent.Contracts.Protocol` — even a rough version lets you test
   `BackendConnection` against something closer to production than
   `tools/CloudOrc.AgentTestServer`.
3. When ready, start **Phase 3**: switch `BackendConnection.Url` to a real `wss://`
   endpoint and design the actual enrollment/credential mechanism — see
   [docs/FUTURE_BACKEND_INTEGRATION.md](docs/FUTURE_BACKEND_INTEGRATION.md) for the exact
   gap list (identity, enrollment, credentials, TLS-with-real-cert, token rotation, RBAC,
   audit, command expiry, replay protection, live output streaming).

---

## 11. Full Documentation Index

- [README.md](README.md) — overview, quick start
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — full component design, honest limitations
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) — build/run/configure
- [docs/TESTING.md](docs/TESTING.md) — every automated + manual test scenario
- [docs/BACKEND_WEBSOCKET_TESTING.md](docs/BACKEND_WEBSOCKET_TESTING.md) — the WebSocket layer, step by step, with confirmed live output
- [docs/WINDOWS_SERVICE_INSTALLATION.md](docs/WINDOWS_SERVICE_INSTALLATION.md) — publish & install as a service (framework-dependent variant)
- [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) — **self-contained package deployment to another server, end to end**
- [docs/DEPLOYMENT_TEST_PLAN.md](docs/DEPLOYMENT_TEST_PLAN.md) — **the fixed 26-test deployment validation checklist and what's already verified vs. manual**
- [docs/FUTURE_BACKEND_INTEGRATION.md](docs/FUTURE_BACKEND_INTEGRATION.md) — what's left before the real backend
