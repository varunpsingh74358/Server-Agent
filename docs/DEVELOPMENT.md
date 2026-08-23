# Development Guide

## Prerequisites

- Windows (both agents use Windows-only APIs: the PowerShell SDK's Windows cmdlets,
  `System.ServiceProcess.ServiceController`, Named Pipes for local IPC).
- .NET 10 SDK (LTS). Check what's installed:

  ```powershell
  dotnet --version
  dotnet --list-sdks
  ```

  This solution targets `net10.0-windows`. If a different SDK major version is installed,
  update the `<TargetFramework>` in every `.csproj` accordingly before building - do not
  assume net10.0 is available without checking first.

No Docker, no Node.js, no SQL Server, and no external services are required for local
development or testing.

## Restore

```powershell
cd CLOUDORCAGENT
dotnet restore CloudOrc.WindowsAgents.sln
```

## Build

```powershell
dotnet build CloudOrc.WindowsAgents.sln
```

## Run the Control Agent (console/dev mode)

```powershell
cd src\CloudOrc.ControlAgent
dotnet run
```

On first run it creates, if missing:

```
C:\ProgramData\CloudOrc\ControlAgent\
    commands\
    processing\
    completed\
    failed\
    results\
    logs\
```

Logs go to the console and to `C:\ProgramData\CloudOrc\ControlAgent\logs\controlagent-*.log`
(one file per day, 14 days retained).

## Run the Watchdog Agent (console/dev mode)

```powershell
cd src\CloudOrc.WatchdogAgent
dotnet run
```

Logs go to the console and to `C:\ProgramData\CloudOrc\WatchdogAgent\logs\watchdogagent-*.log`.

Running the Watchdog without the Control Agent installed as a Windows Service is fully
supported for observing health-check behavior; it will report the service as
`NotInstalled` and, if recovery is ever attempted, log clearly that it cannot restart a
service that isn't installed. See `docs/TESTING.md` for what to expect from each scenario
and `docs/WINDOWS_SERVICE_INSTALLATION.md` for enabling real restart-based recovery.

Run both agents from two separate terminals (e.g. two VS Code integrated terminals) to
see them interact.

## Run the local backend test server (optional, for WSS testing)

```powershell
cd tools\CloudOrc.AgentTestServer
dotnet run
```

A dev/test-only local stand-in for the real backend - accepts a WebSocket connection from
the Control Agent, prints HELLO/HEARTBEAT/TELEMETRY, and lets you send it commands. See
[docs/BACKEND_WEBSOCKET_TESTING.md](BACKEND_WEBSOCKET_TESTING.md) for the full walkthrough.
Not part of, and never shipped alongside, the production agent.

## Configuration

Each agent reads `appsettings.json` (and `appsettings.Development.json` when
`DOTNET_ENVIRONMENT=Development`, which `dotnet run` sets by default) via the standard
.NET configuration system. Key settings:

**`src/CloudOrc.ControlAgent/appsettings.json`** (`ControlAgent` section):

| Setting | Default | Meaning |
|---|---|---|
| `DataDirectory` | `C:\ProgramData\CloudOrc\ControlAgent` | Root for commands/processing/completed/failed/results/logs |
| `PollIntervalSeconds` | 2 | How often the commands\ directory is scanned |
| `FileStabilityMilliseconds` | 750 | How long a file's last-write time must be unchanged before it's considered fully written |
| `HealthPipeName` | `CloudOrc.ControlAgent.Health` | Named pipe the health server listens on |
| `WorkerHeartbeatTimeoutSeconds` | 30 | How stale a worker heartbeat can be before it's reported unhealthy |
| `Validation.MinTimeoutSeconds` / `MaxTimeoutSeconds` / `DefaultTimeoutSeconds` | 1 / 3600 / 30 | Allowed range for a command's `timeoutSeconds` |
| `Validation.MaxScriptLength` | 32000 | Maximum characters allowed in `script` |
| `LocalFileModeEnabled` | `true` | Whether the local file command source/result sink are active; can run alongside `BackendConnection` |

**`src/CloudOrc.ControlAgent/appsettings.json`** (`BackendConnection` section - see
[docs/BACKEND_WEBSOCKET_TESTING.md](BACKEND_WEBSOCKET_TESTING.md) for a full walkthrough):

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master switch for the WebSocket connection to a backend |
| `Url` | *(empty)* | e.g. `ws://localhost:5299/agent` for local testing, `wss://...` for production |
| `DevelopmentAllowInsecureWs` | `false` | Must be `true` to allow a `ws://` URL - otherwise the agent refuses to start |
| `ConnectTimeoutSeconds` | 10 | Timeout for establishing the WebSocket connection |
| `ReconnectInitialDelaySeconds` / `ReconnectMaximumDelaySeconds` | 2 / 60 | Exponential backoff bounds for reconnect attempts |
| `HeartbeatIntervalSeconds` | 15 | How often a HEARTBEAT is sent while connected |
| `TelemetryIntervalSeconds` | 10 | How often a TELEMETRY snapshot is sent while connected |

**`src/CloudOrc.ControlAgent/appsettings.json`** (`AgentIdentity` section):

| Setting | Default | Meaning |
|---|---|---|
| `AgentId` | `local-test-agent` | Sent in HELLO/HEARTBEAT/TELEMETRY - locally configured for this phase, no enrollment yet |
| `ServerId` | `local-test-server` | Same |

`MachineId`, `MachineName`, and `AgentVersion` are derived at runtime (the real Windows
`MachineGuid`, `Environment.MachineName`, and the assembly version) - not configured.

All of the above can be overridden per-run without editing the file, e.g.
`dotnet run --BackendConnection:Enabled=true --BackendConnection:Url=ws://localhost:5299/agent`.

**`src/CloudOrc.WatchdogAgent/appsettings.json`** (`Watchdog` section):

| Setting | Default | Meaning |
|---|---|---|
| `ControlAgentServiceName` | `CloudOrcControlAgent` | Windows Service name to monitor/restart |
| `HealthPipeName` | `CloudOrc.ControlAgent.Health` | Must match the Control Agent's setting |
| `HealthCheckIntervalSeconds` | 10 | How often a full monitoring cycle runs |
| `HealthCheckTimeoutSeconds` | 5 | How long to wait for a health pipe response |
| `ConsecutiveFailureThreshold` | 3 | Failures required before recovery is attempted |
| `RecoveryWaitSeconds` | 15 | Wait after a restart before re-checking health |
| `MaxRestartAttemptsPerWindow` | 3 | Rate limit cap |
| `RestartRateLimitWindowMinutes` | 15 | Rate limit window |
| `InitialBackoffSeconds` / `MaxBackoffSeconds` | 30 / 600 | Exponential backoff bounds between failed recoveries |

Both agents also have a `Serilog.MinimumLevel` section for adjusting log verbosity.

## Solution layout

```
CloudOrc.WindowsAgents.sln
src/
  CloudOrc.Agent.Contracts/    Commands, Abstractions, Execution, Health namespaces
  CloudOrc.ControlAgent/       Configuration, Health, Services, Startup namespaces
  CloudOrc.WatchdogAgent/      Configuration, ControlAgentManagement, Recovery, Services
tests/
  CloudOrc.ControlAgent.Tests/
  CloudOrc.WatchdogAgent.Tests/
docs/
README.md
.gitignore
```

## Code style notes

- Dependency injection throughout via `Microsoft.Extensions.Hosting`; no static
  singletons or service-locator patterns.
- All background work is `BackgroundService` + `CancellationToken`; nothing blocks a
  thread pool thread waiting on I/O.
- Interfaces (`ICommandSource`, `ICommandResultSink`, `IPowerShellExecutor`,
  `ICommandQueue`) exist specifically at the points where a future backend integration
  will need to swap an implementation - not speculatively elsewhere.
