# Architecture

## Overview

The solution is two independent .NET Worker Services:

| Project | Service name | Role |
|---|---|---|
| `CloudOrc.ControlAgent` | `CloudOrcControlAgent` | Generic local PowerShell execution engine |
| `CloudOrc.WatchdogAgent` | `CloudOrcWatchdogAgent` | Monitors the Control Agent and performs controlled recovery |

A third project, `CloudOrc.Agent.Contracts`, holds the models and interfaces shared by
both agents (and, in the future, by whatever replaces the local file transport). Both
agent projects, and both test projects, reference it; neither agent project references
the other.

```
CloudOrc.WindowsAgents.sln
|
|-- src/CloudOrc.Agent.Contracts     (models + interfaces, no I/O)
|-- src/CloudOrc.ControlAgent        (Worker Service, references Contracts)
|-- src/CloudOrc.WatchdogAgent       (Worker Service, references Contracts)
|
|-- tests/CloudOrc.ControlAgent.Tests
|-- tests/CloudOrc.WatchdogAgent.Tests
|
|-- tools/CloudOrc.AgentTestServer   (dev/test-only local WebSocket stand-in for the
                                       real backend - never shipped with the agent)
```

Target framework: **net10.0-windows** everywhere. .NET 10 is the current LTS release and
the only SDK installed in this environment (`dotnet --list-sdks`). The `-windows` suffix
is used because every project in this solution genuinely depends on Windows-only
capabilities (Named Pipes used the Windows-only way expected here, `System.ServiceProcess.ServiceController`,
and the PowerShell SDK's Windows-specific cmdlets) - it is not a cross-platform codebase
pretending otherwise.

## Control Agent

### Responsibility boundary

The Control Agent's only job is: detect a command, validate it, run it through
PowerShell, capture the outcome, persist the result. It has zero awareness of the
Watchdog Agent.

### Components

```
  commands\*.json         WSS COMMAND message
       |                          |
       v                          v
  +--------------------+   +--------------------+
  | LocalFileCommandSource |   | WssCommandSource    |   both implement ICommandSource
  | (detect/validate/     |   | (validate, dedupe,  |
  |  dedupe, claim files) |   |  fed by the receive |
  +-----------+------------+   |  loop below)        |
              |                +----------+----------+
              | CommandJob                | CommandJob
              +-------------+-------------+
                            v
                +----------------------+
                |  InMemoryCommandQueue   |  ICommandQueue
                |  (System.Threading.Channels) |
                +-----------+--------------+
                            | CommandJob
                            v
                +----------------------+
                | CommandProcessingService |  (BackgroundService,
                | (sequential consumer)    |   single reader)
                +-----+--------------+----+
                      |              |
                      v              v
          +----------------+   +---------------------------+
          | PowerShellCommand |  | every ICommandResultSink   |  today: LocalFileResultSink
          | Executor          |  | (fan-out, one call each)   |  and, if enabled, WssResultSink
          | (System.Management.|  +---------------------------+
          |  Automation)       |
          +--------------------+

  ControlAgentHealthState  <--- touched by both CommandDetectionService and
       ^                        CommandProcessingService on every loop tick,
       |                        plus BackendConnectionService (connection state)
       |
  HealthPipeServer  --(Named Pipe "CloudOrc.ControlAgent.Health")-->  Watchdog Agent
```

Hosted services (all registered via `AddHostedService`, all `BackgroundService`):

- **CommandDetectionService** - runs `GetCommandsAsync` on *every* registered
  `ICommandSource` concurrently (today: local file always, plus WSS when
  `BackendConnection.Enabled`) and forwards every yielded `CommandJob` into the single
  shared `ICommandQueue`, publishing a QUEUED status update along the way. Does no
  PowerShell execution itself, so a slow or stuck command execution can never block
  detection of new work from any source.
- **CommandProcessingService** - the single consumer of the queue, regardless of which
  source a job came from. Executes one command at a time (`await foreach` over the
  queue, no `Task.Run`/parallel dispatch), publishes a RUNNING status update, builds the
  `CommandResult`, writes it to *every* registered `ICommandResultSink`, and acknowledges
  the job back to `job.OriginSource` - the specific source instance that produced it, now
  that more than one can be active at once.
- **HealthPipeServer** - serves the current `ControlAgentHealthSnapshot` as JSON over a
  local Named Pipe for the Watchdog to poll.
- **BackendConnectionService, HeartbeatPublisherService, TelemetryPublisherService** -
  only run when `BackendConnection.Enabled`; see
  [Backend connectivity (WSS)](#backend-connectivity-wss) below.

### Multiple command sources, one pipeline

`CommandJob` carries an `OriginSource` reference (the `ICommandSource` instance that
produced it) precisely so `CommandProcessingService` can acknowledge the right source
without needing to know how many sources exist or what kind they are. This is what makes
"local file + WSS running simultaneously" work without either source needing any
awareness of the other - confirmed live: a locally-file-sourced command and a
WSS-sourced command were processed correctly in the same run, including while the WSS
connection was mid-reconnect.

### Why detection and execution are separate workers

If a single worker both scanned the directory and ran PowerShell inline, a command that
takes 60 seconds (or hangs) would also stop new commands from being *detected* for that
same 60 seconds. Splitting them means detection keeps running (and keeps reporting itself
healthy) independently of how long the current command execution takes.

### Command lifecycle and file state machine

A command file physically moves between directories as it progresses; the directory it's
in *is* its state:

```
commands\{name}.json
     |  (atomic File.Move - "claims" the file)
     v
processing\{name}.json
     |  (read + parse + validate)
     |
     +-- invalid JSON / invalid fields --> failed\{name-or-commandId}.json
     |                                     (+ results\{commandId}.result.json if a
     |                                      CommandId could be determined)
     |
     +-- duplicate CommandId --> failed\{commandId}.duplicate-{ticks}.json
     |
     +-- valid & unique --> renamed to processing\{commandId}.json, enqueued
                                  |
                                  v
                          PowerShell execution
                                  |
                    results\{commandId}.result.json written
                                  |
                    +-------------+--------------+
                    |                             |
              Status == Success            Status != Success
                    |                             |
                    v                             v
        completed\{commandId}.json        failed\{commandId}.json
```

### Duplicate protection and delivery semantics (read this before assuming "exactly-once")

This is a local-file transport, and it is **at-least-once, not exactly-once**:

1. While the process is running, a command file can only be claimed once: `File.Move`
   from `commands\` to `processing\` is atomic on the same volume, so two detection
   passes can never both pick up the same file.
2. Before a claimed job is queued, its `CommandId` is checked against
   `results\{id}.result.json`, `completed\{id}.json`, `failed\{id}.json`, and an
   in-memory "claimed this session" set. A `CommandId` that already has a result is
   never re-executed - the new file is diverted to `failed\` with a `.duplicate-*`
   suffix and logged, not silently dropped and not overwriting the earlier result.
3. **The gap**: if the process crashes *after* claiming a file (moved into `processing\`)
   but *before* a result is written, there is no record yet that the command ran. On the
   next startup, `LocalFileCommandSource` recovers any file left in `processing\` by
   moving it back to `commands\`, where it will be picked up and **executed again**. If
   the original execution had already produced a real side effect (e.g. it wasn't a
   read-only command like `Get-Date`), that side effect can happen twice.

This is called out explicitly rather than glossed over because it is a real limitation of
a file-based transport with sequential local execution, and it is exactly the kind of
thing that must be designed for correctly (e.g. idempotent commands, or a durable queue)
before this is used for anything that isn't local development/testing.

### Generic PowerShell execution engine

`PowerShellCommandExecutor` (`IPowerShellExecutor`) is the one piece of this system that
must never contain command-specific logic. It:

- Accepts any script string and runs it in a fresh `Runspace` via the PowerShell SDK
  (`Microsoft.PowerShell.SDK`, `System.Management.Automation`). A new Runspace per
  invocation costs a small amount of overhead but guarantees no state (variables,
  functions, imported modules) leaks between commands - acceptable because commands run
  strictly sequentially in this version.
- Captures the normal output stream (`PSDataCollection<PSObject>`, each item
  `.ToString()`'d) and the error stream (`PowerShell.Streams.Error`) separately.
- Applies the command's timeout via a `CancellationTokenSource(timeout)`, linked with the
  caller's own `CancellationToken` (used for host shutdown). Either cause calls
  `PowerShell.Stop()` - the SDK's own cooperative cancellation - never `Process.Kill()`
  or thread abandonment. The registration is deliberately wired up **after**
  `BeginInvoke` returns (with a check for the residual race immediately after), because
  registering it earlier can call `Stop()` before the pipeline has actually started,
  which does nothing and is never retried.
- Distinguishes the final status precisely: caller cancellation -> `Cancelled`;
  timeout elapsed -> `Timeout`; anything written to the error stream, or a terminating
  exception -> `Failed`; otherwise -> `Success`. This is why
  `Get-Service -Name "DefinitelyDoesNotExist"` (a *non-terminating* error) correctly comes
  back as `Failed` with the error message captured, without needing any special-casing of
  that command.
- Never throws out of `ExecuteAsync` for a bad script - `CommandProcessingService` also
  wraps the call in a try/catch as defense in depth, so an unexpected SDK exception still
  produces a `Failed` result rather than crashing the worker.

### Health model

`ControlAgentHealthState` is a small, deliberately-scoped piece of shared mutable state.
Both `CommandDetectionService` and `CommandProcessingService` run a lightweight heartbeat
loop (`Task.WhenAll`'d alongside their real work loop) that touches a last-activity
timestamp every few seconds. `HealthPipeServer` reads a snapshot of this state and reports:

- `detectionWorkerAlive` / `processingWorkerAlive` - true if that worker's heartbeat is
  younger than `WorkerHeartbeatTimeoutSeconds`.
- `lastDetectionActivityAt` / `lastProcessingActivityAt`.
- `currentCommandId` / `currentCommandStatus` - what's executing right now, if anything.
- `processedCount` / `failedCount` - running totals since process start.

**Honest limitation**: the heartbeat proves each `BackgroundService`'s scheduling loop is
being scheduled and is not deadlocked - it is a liveness check, not a proof that the file
scan or PowerShell execution is making meaningful forward progress at that exact instant.
Combined with `currentCommandId`/`currentCommandStatus`, a Watchdog that also tracks how
long a command has been "Running" could detect a hang; this version does not add that
extra layer, and it is called out here as a reasonable next step rather than implemented
speculatively.

Transport: a local Named Pipe (`CloudOrc.ControlAgent.Health` by default), not any kind of
network listener. One connection is served at a time - one health snapshot per connect,
then the pipe instance is recreated to accept the next connection.

## Backend connectivity (WSS)

Disabled by default (`BackendConnection.Enabled = false`). When enabled, the Control
Agent maintains a single outbound WebSocket connection to a backend, used both to receive
commands and to publish telemetry/status/results - see
[docs/BACKEND_WEBSOCKET_TESTING.md](BACKEND_WEBSOCKET_TESTING.md) for hands-on testing
instructions and confirmed live output for every message type.

### Components

- **BackendConnectionService** - the only piece of code that ever calls
  `ClientWebSocket.SendAsync`/`ReceiveAsync`. Owns connect, HELLO, the receive loop
  (dispatching COMMAND/PING), the send loop (draining `OutgoingMessageChannel`), and
  reconnect-with-backoff on any failure. Never throws out of its `ExecuteAsync` - a
  connection failure is logged and retried, never propagated to crash the process.
- **OutgoingMessageChannel** - a `Channel<string>` of already-serialized JSON messages.
  Every publisher (heartbeat, telemetry, status, result) writes here instead of touching
  the socket directly; this - not locking - is what makes concurrent sends from multiple
  background services safe. Unbounded and not cleared on reconnect: a message written
  while disconnected simply waits until the next successful connection's send loop
  starts draining it again (confirmed live: a result generated entirely during an outage
  was delivered immediately upon reconnect).
- **ReconnectBackoffCalculator** - pure exponential backoff
  (`ReconnectInitialDelaySeconds * 2^failures`, capped at `ReconnectMaximumDelaySeconds`,
  reset on a successful connect). Confirmed live: 4s -> 8s -> 16s -> 32s across a real
  outage.
- **WssCommandSource** / **WssResultSink** - the WSS-specific `ICommandSource` /
  `ICommandResultSink` implementations. Structurally identical in role to
  `LocalFileCommandSource`/`LocalFileResultSink` - see
  [Multiple command sources, one pipeline](#multiple-command-sources-one-pipeline) above.
- **HeartbeatPublisherService** / **TelemetryPublisherService** - independent
  interval-driven `BackgroundService`s. Both check the connection state before doing any
  work and simply skip a tick while disconnected, rather than queuing up stale data.
  Neither ever runs PowerShell - telemetry is collected via `TelemetryCollector`
  (`DriveInfo`, a `PerformanceCounter` for CPU%, and a `GlobalMemoryStatusEx` P/Invoke for
  memory), which is a completely separate code path from command execution.
- **AgentIdentityProvider** - builds the `AgentIdentity` sent in HELLO/HEARTBEAT/TELEMETRY.
  `MachineId` prefers the real Windows `MachineGuid` registry value (confirmed live to
  resolve to a real GUID), falling back to a locally-generated-and-persisted one if that
  registry key can't be read.

### Delivery guarantees (read this before assuming "exactly-once")

Same honesty standard as the local file transport: this is **at-least-once while the
process stays up**, not durable/exactly-once.

- A `COMMAND_RESULT` generated while disconnected sits in `OutgoingMessageChannel` until
  the next successful connection - confirmed live. If the Control Agent **process**
  restarts while disconnected, that queued message is lost from the backend's
  perspective (though the same result is still on disk in `results\` if local file mode
  is also enabled, which fanning results out to every sink makes possible for free).
- `HEARTBEAT`/`TELEMETRY` are intentionally *not* durable - `HeartbeatPublisherService`/
  `TelemetryPublisherService` skip publishing entirely while disconnected rather than
  queuing stale snapshots, since a missed heartbeat/telemetry tick is immediately
  superseded by the next one.
- `COMMAND_STATUS` (QUEUED/RUNNING) is best-effort for the same reason - it's always
  superseded by the terminal `COMMAND_RESULT`.

### Security posture for this phase (deliberately minimal, not accidentally minimal)

- `BackendConnection.DevelopmentAllowInsecureWs` must be explicitly `true` for a `ws://`
  URL to be accepted at all - `Program.cs` throws a clear startup exception otherwise.
  There is no code path that silently accepts an insecure connection.
- `AgentId`/`ServerId` are plain configuration values - there is no enrollment step, no
  credential, no proof of identity. This is intentional for this phase (see
  [docs/FUTURE_BACKEND_INTEGRATION.md](FUTURE_BACKEND_INTEGRATION.md)) and is not
  disguised as anything more than it is.

### Why this can never affect Watchdog behavior

`ControlAgentHealthSnapshot.BackendConnectionState` is populated for visibility only -
`ControlAgentHealthState.Snapshot()`'s `Status` (`HEALTHY`/`DEGRADED`) computation only
ever looks at the detection/processing worker heartbeats, never at backend connectivity.
Confirmed live: across a real, sustained backend outage (with reconnect attempts backing
off up to 32s), the Watchdog logged `status=HEALTHY` on every single cycle and never
attempted recovery - only `backendConnectionState` in its log line changed.

## Watchdog Agent

### Responsibility boundary

The Watchdog does exactly one thing: watch the Control Agent, and recover it in a
controlled way when it's unhealthy. It never executes PowerShell, never reads command
files, and has no knowledge of `ICommandSource`/`ICommandQueue`/etc. The dependency is
one-directional - Watchdog -> monitors -> Control Agent - and the Control Agent has zero
code referencing the Watchdog.

### Components

- **WatchdogMonitorService** (`BackgroundService`) - runs one monitoring cycle every
  `HealthCheckIntervalSeconds`, wrapped in its own try/catch so a single bad cycle (e.g. a
  transient service-query error) never stops the loop.
- **ControlAgentServiceManager** - thin wrapper over `System.ServiceProcess.ServiceController`
  for the `CloudOrcControlAgent` Windows Service: query status, stop+start to restart.
  Never shells out to PowerShell for this.
- **ControlAgentHealthClient** - Named Pipe client that asks the Control Agent for its
  health snapshot, with its own timeout. A connection failure or timeout is treated the
  same as an explicit "unhealthy" response. The snapshot's `backendConnectionState` field
  (added for the WSS layer, see [Backend connectivity (WSS)](#backend-connectivity-wss))
  is logged by `WatchdogMonitorService` purely for operator visibility - it is never read
  by any decision-making logic here.
- **ConsecutiveFailureTracker** - pure in-memory counter, unit tested directly.
- **RecoveryRateLimiter** - pure logic combining a rolling-window attempt cap with
  exponential backoff between consecutive failed recoveries, driven by an injected
  `TimeProvider` so it is deterministically unit tested without real waiting.

### Monitoring cycle

```
service status query (best-effort; "NotInstalled" is a valid, non-fatal result)
        |
        v
health pipe check (with its own timeout)
        |
   healthy? ----yes----> reset consecutive-failure counter, done for this cycle
        |
        no
        v
increment consecutive-failure counter
        |
   threshold reached? ----no----> done for this cycle
        |
       yes
        v
ask RecoveryRateLimiter: allowed right now?
        |
   no (rate-limited/backing off) --> log and skip; do NOT restart
        |
       yes
        v
record the attempt, restart the Windows Service
        |
   restart actually issued? --no--> record a failed outcome, log why, done
        |
       yes
        v
wait RecoveryWaitSeconds, health-check again
        |
   healthy now? --yes--> record success, reset failure counter
        |
        no
        v
record a failed outcome (feeds the next backoff), log it, continue monitoring
```

### Restart loop protection

Two independent, composable controls, both configurable (see `docs/DEVELOPMENT.md` /
appsettings):

1. **Rate limit**: at most `MaxRestartAttemptsPerWindow` restart attempts within a
   rolling `RestartRateLimitWindowMinutes` window. A queue of attempt timestamps is
   pruned on every evaluation; once the cap is hit, no further restarts happen until an
   old attempt ages out of the window.
2. **Exponential backoff**: after each recovery attempt that does *not* result in a
   healthy Control Agent, the required delay before the next attempt doubles
   (`InitialBackoffSeconds * 2^(consecutiveFailures-1)`, capped at `MaxBackoffSeconds`).
   A single success resets the backoff to zero.

Both are evaluated together in `RecoveryRateLimiter.Evaluate()`; either one denying is
sufficient to block a recovery attempt this cycle.

### Known limitation: recovery in console/dev mode

`ControlAgentServiceManager.TryRestart()` requires the `CloudOrcControlAgent` Windows
Service to actually be installed. In console/dev mode (`dotnet run`, no service
installed), the Watchdog can still fully exercise health *detection* against a
console-mode Control Agent, but a restart attempt will fail with a clearly logged reason
("service not installed") rather than silently doing nothing or crashing. Full recovery
testing requires both agents installed as Windows Services - see
`docs/WINDOWS_SERVICE_INSTALLATION.md` and `docs/TESTING.md`.

## Why the two agents don't share a process

Running two separate Worker Services (rather than one process with two loops) means the
Watchdog keeps running - and can still attempt recovery - even if the Control Agent
process crashes outright, hangs completely, or is stopped by an operator. A Watchdog that
lived inside the Control Agent's own process could never observe or recover from the
Control Agent's process disappearing.
