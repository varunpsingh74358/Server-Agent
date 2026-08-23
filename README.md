# CloudOrc Windows Agents

Two independent .NET Worker Services that will eventually replace WinRM as the way an
existing backend runs PowerShell against a Windows Server:

- **CloudOrc Control Agent** - a generic local PowerShell execution engine.
- **CloudOrc Watchdog Agent** - monitors the Control Agent and performs controlled,
  rate-limited recovery when it becomes unhealthy.

This is still the **local development version** - it does **not** connect to the real
CloudOrc backend and does **not** use WinRM. Commands can come from either (or both, at
the same time): local JSON files dropped in a directory, or a WebSocket connection to a
**local test backend** included in this repo (`tools/CloudOrc.AgentTestServer`) for
exercising the full agent<->backend protocol before wiring up the real thing. See
[docs/BACKEND_WEBSOCKET_TESTING.md](docs/BACKEND_WEBSOCKET_TESTING.md) for hands-on
testing and [docs/FUTURE_BACKEND_INTEGRATION.md](docs/FUTURE_BACKEND_INTEGRATION.md) for
exactly what's still missing before this can talk to the real backend.

## Why two agents

Running the Watchdog as a separate process/service from the Control Agent means it can
keep monitoring - and attempt recovery - even if the Control Agent's process crashes,
hangs, or is stopped outright. The dependency is one-directional: the Watchdog monitors
the Control Agent; the Control Agent has no knowledge the Watchdog exists.

## What each agent does

**CloudOrc Control Agent** (`CloudOrc.ControlAgent`, service name `CloudOrcControlAgent`):
detects a command (currently: a JSON file dropped into a local directory), validates it,
protects against re-running a command whose result already exists, executes it through a
generic PowerShell execution engine (the PowerShell SDK,
`System.Management.Automation` - not `if command == "Get-Service"` style branching),
captures output/errors/timeouts/exceptions, and writes a result JSON file. One bad or
slow command never stops the next one from running.

**CloudOrc Watchdog Agent** (`CloudOrc.WatchdogAgent`, service name
`CloudOrcWatchdogAgent`): on a fixed interval, checks whether the Control Agent's Windows
Service exists and is running, and independently asks it (over a local Named Pipe -
never a network endpoint) whether it's actually healthy. A single failed check is just
recorded; only after a configurable number of *consecutive* failures does it attempt a
restart, and repeated failed recoveries are throttled by a rate limit plus exponential
backoff so it can never hammer the machine with restart attempts. It never executes
PowerShell and never accepts command files - monitoring and recovery only.

## Architecture

### Current (local development)

```
Local command JSON file --> ICommandSource --> Command Queue --> Generic PowerShell
Executor --> ICommandResultSink --> Local result JSON file
```

### Future (backend-connected)

```
Existing Backend <--(secure WebSocket)--> ICommandSource --> Command Queue -->
SAME Generic PowerShell Executor --> ICommandResultSink <--(secure WebSocket)--> Existing Backend
```

The generic PowerShell execution engine is identical in both diagrams - replacing WinRM
happens at the transport layer (`ICommandSource`/`ICommandResultSink`), not in execution
logic. Full details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and
[docs/FUTURE_BACKEND_INTEGRATION.md](docs/FUTURE_BACKEND_INTEGRATION.md).

## Prerequisites

- Windows. Both agents depend on Windows-only APIs (PowerShell SDK's Windows cmdlets,
  `ServiceController`, Named Pipes).
- .NET 10 SDK (the current LTS release). Check what you have installed before assuming:

  ```powershell
  dotnet --version
  dotnet --list-sdks
  ```

  This solution targets `net10.0-windows`. No Docker, Node.js, or SQL Server required.

## Quick start

```powershell
cd CLOUDORCAGENT

# Restore
dotnet restore CloudOrc.WindowsAgents.sln

# Build
dotnet build CloudOrc.WindowsAgents.sln

# Run tests
dotnet test CloudOrc.WindowsAgents.sln
```

### Run the Control Agent (VS Code terminal 1)

```powershell
cd src\CloudOrc.ControlAgent
dotnet run
```

### Run the Watchdog Agent (VS Code terminal 2)

```powershell
cd src\CloudOrc.WatchdogAgent
dotnet run
```

### Create your first test command

Path to create it in:

```
C:\ProgramData\CloudOrc\ControlAgent\commands\test-001.json
```

Content:

```json
{
  "commandId": "test-001",
  "script": "Get-Date",
  "timeoutSeconds": 30
}
```

PowerShell one-liner to create it:

```powershell
@'
{
  "commandId": "test-001",
  "script": "Get-Date",
  "timeoutSeconds": 30
}
'@ | Set-Content -Path "C:\ProgramData\CloudOrc\ControlAgent\commands\test-001.json"
```

### Where to see the result

```
C:\ProgramData\CloudOrc\ControlAgent\results\test-001.result.json
```

Within a couple of seconds it should contain `"status": "Success"` and the current
date/time in `output`. The original command file will have moved to
`C:\ProgramData\CloudOrc\ControlAgent\completed\test-001.json`.

### Test a failure

```powershell
@'
{ "commandId": "test-003", "script": "Get-Service -Name \"DefinitelyDoesNotExist\"", "timeoutSeconds": 30 }
'@ | Set-Content -Path "C:\ProgramData\CloudOrc\ControlAgent\commands\test-003.json"
```

Expect `results\test-003.result.json` with `"status": "Failed"` and a real error message
- and the agent keeps running.

### Test a timeout

```powershell
@'
{ "commandId": "timeout-test", "script": "Start-Sleep -Seconds 60", "timeoutSeconds": 5 }
'@ | Set-Content -Path "C:\ProgramData\CloudOrc\ControlAgent\commands\timeout-test.json"
```

Expect `results\timeout-test.result.json` with `"status": "Timeout"` after ~5 seconds
(not 60) - PowerShell's own `Stop()` mechanism actually halts execution.

### Test Watchdog recovery

With both agents running, stop the Control Agent process. Watch the Watchdog's log:
consecutive failures accumulate, then after the configured threshold it logs an attempted
recovery. In console/dev mode (no Windows Service installed) recovery will fail with a
clearly logged reason - install both as Windows Services (see
[docs/WINDOWS_SERVICE_INSTALLATION.md](docs/WINDOWS_SERVICE_INSTALLATION.md)) to see a
real restart. Full step-by-step scenarios (including invalid JSON, duplicate detection,
and repeated-failure backoff) are in [docs/TESTING.md](docs/TESTING.md).

### Test the backend WebSocket layer locally

A local stand-in for the real backend ships in this repo - it accepts a WebSocket
connection, prints every HELLO/HEARTBEAT/TELEMETRY message the agent sends, and lets you
send it commands to execute:

```powershell
# Terminal 1
cd tools\CloudOrc.AgentTestServer
dotnet run

# Terminal 2
cd src\CloudOrc.ControlAgent
dotnet run --BackendConnection:Enabled=true --BackendConnection:Url=ws://localhost:5299/agent --BackendConnection:DevelopmentAllowInsecureWs=true
```

Then in Terminal 1: `send Get-Date`. Full walkthrough - HELLO/HEARTBEAT/TELEMETRY
verification, failure/timeout over the WebSocket, reconnection after restarting the test
server, and confirming the Watchdog stays calm through a backend outage - is in
[docs/BACKEND_WEBSOCKET_TESTING.md](docs/BACKEND_WEBSOCKET_TESTING.md), with real
confirmed output for every step.

## Running the automated tests

```powershell
dotnet test CloudOrc.WindowsAgents.sln
```

95 tests as of this writing, 0 failures - see [docs/TESTING.md](docs/TESTING.md) for
exactly what each test covers, including which pieces (real Windows Service
query/restart) are intentionally left to manual/integration testing instead.

## Publishing and Windows Service installation

```powershell
dotnet publish src\CloudOrc.ControlAgent\CloudOrc.ControlAgent.csproj -c Release -r win-x64 --self-contained false -o publish\ControlAgent
dotnet publish src\CloudOrc.WatchdogAgent\CloudOrc.WatchdogAgent.csproj -c Release -r win-x64 --self-contained false -o publish\WatchdogAgent
```

Neither `dotnet build` nor `dotnet publish` installs a Windows Service automatically.
Full manual installation steps (service creation, required permissions, start/stop,
status, uninstall) are in
[docs/WINDOWS_SERVICE_INSTALLATION.md](docs/WINDOWS_SERVICE_INSTALLATION.md).

## Deploying to a Windows Server (installer)

For deploying to many servers, a single installer EXE (`CloudOrcAgentSetup.exe`, built
with Inno Setup from `installer/CloudOrcAgentSetup.iss`) packages both agents'
self-contained published output and installs them as Windows Services with zero required
configuration. The target server needs **no .NET, no Git, no source code** - just Windows
and Administrator privileges.

```powershell
# One-line install (elevated PowerShell), once you've set your repo owner/name -
# see docs/INSTALLATION.md section C:
irm https://raw.githubusercontent.com/varunpsingh74358/Server-Agent/main/install-cloudorc.ps1 | iex

# Silent install for scripted/many-server deployment:
.\CloudOrcAgentSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART

# Silent uninstall:
& "C:\Program Files\CloudOrc\Agents\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

`.github/workflows/build-installer.yml` builds and tests the agents, builds the
installer, and - only when a `v*` tag is pushed - publishes it as a GitHub Release asset
alongside its SHA256 checksum. Full instructions (build, release, install, upgrade,
uninstall, health check, troubleshooting, and the private-repository download
limitation) are in [docs/INSTALLATION.md](docs/INSTALLATION.md). The older ZIP + PowerShell
script deployment path (no installer EXE) is still available and documented in
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## Security limitations of this version (by design)

The WebSocket layer connects over plain `ws://` for local testing, and refuses to start
with an insecure URL unless `BackendConnection.DevelopmentAllowInsecureWs` is explicitly
set to `true` (or the URL came from a completed enrollment - see below) - there is no path
to accidentally end up connected insecurely.

**Agent enrollment and WebSocket bearer-credential authentication are now implemented** -
see [docs/ENROLLMENT.md](docs/ENROLLMENT.md). An administrator supplies a one-time
enrollment token (`CloudOrcAgentSetup.exe --token "ENR-..."`); the agent redeems it,
receives its `AgentId`/`ServerId`/backend URL/permanent credential, stores them
DPAPI-encrypted, and presents the credential on every WebSocket connection - with zero
hardcoded backend URL anywhere in the agent or installer, and zero manual
`appsettings.json` editing. This repository ships the full agent-side implementation plus
a reference (dev/test-only, in-memory) enrollment backend in
`tools/CloudOrc.AgentTestServer`; a **real production backend** implementing the same
request/response contract is still a separate integration task - see
[docs/FUTURE_BACKEND_INTEGRATION.md](docs/FUTURE_BACKEND_INTEGRATION.md) for what's left
(a persistent token/credential database, RBAC, audit logs, command expiry, replay
protection, live output streaming) beyond what's already built.

The local file transport also provides **at-least-once, not exactly-once** command
execution - see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#duplicate-protection-and-delivery-semantics-read-this-before-assuming-exactly-once)
for the precise, honest explanation of when a command could theoretically run twice (a
crash between claiming a command file and writing its result).

## Repository layout

```
CloudOrc.WindowsAgents.sln
src/
  CloudOrc.Agent.Contracts/     Shared models + interfaces (no I/O)
  CloudOrc.ControlAgent/        Worker Service - service name CloudOrcControlAgent
  CloudOrc.WatchdogAgent/       Worker Service - service name CloudOrcWatchdogAgent
tests/
  CloudOrc.ControlAgent.Tests/
  CloudOrc.WatchdogAgent.Tests/
tools/
  CloudOrc.AgentTestServer/     Local WebSocket stand-in for the backend - DEV/TEST ONLY
docs/
  ARCHITECTURE.md
  DEVELOPMENT.md
  TESTING.md
  BACKEND_WEBSOCKET_TESTING.md
  WINDOWS_SERVICE_INSTALLATION.md
  FUTURE_BACKEND_INTEGRATION.md
README.md
STATUS_REPORT.md
.gitignore
```
