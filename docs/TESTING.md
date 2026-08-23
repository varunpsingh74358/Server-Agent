# Testing Guide

## Automated tests

```powershell
dotnet test CloudOrc.WindowsAgents.sln
```

This runs both test projects. As of this writing: **95 tests, 0 failures** (15 in
`CloudOrc.WatchdogAgent.Tests`, 80 in `CloudOrc.ControlAgent.Tests`).

For the backend WebSocket layer specifically - HELLO/HEARTBEAT/TELEMETRY, sending
commands, reconnection, and confirming the Watchdog stays calm through a backend outage -
see [docs/BACKEND_WEBSOCKET_TESTING.md](BACKEND_WEBSOCKET_TESTING.md), which has
step-by-step instructions and real confirmed output for every scenario.

### What's covered by automated tests

`CloudOrc.ControlAgent.Tests`:

- `CommandRequestValidatorTests` - empty `CommandId`, empty `Script`, invalid
  filename characters, script-length limit, timeout range validation, default-timeout
  fallback, valid-request success.
- `CommandResultSerializationTests` - JSON shape, round-tripping, all six `CommandStatus`
  values.
- `InMemoryCommandQueueTests` - enqueue/dequeue, FIFO order, cancellation.
- `LocalFileCommandSourceTests` - **real temp-directory file-system tests** (not mocked)
  covering: valid command claimed and moved to `processing\`; success/failure
  acknowledgment moving files to `completed\`/`failed\`; invalid JSON moved to `failed\`
  and never yielded; invalid `CommandId` rejected; duplicate `CommandId` skipped both when
  a prior result already exists and when a second file with the same id arrives in the
  same session; orphaned `processing\` files recovered and re-yielded on startup.
- `PowerShellCommandExecutorTests` - **real PowerShell SDK execution** (not mocked):
  successful command with captured output; non-terminating error captured as `Failed`;
  timeout enforcement (`Start-Sleep -Seconds 30` cut short by a 2s timeout); a command
  that completes comfortably inside its timeout; external cancellation; and confirmation
  that a failed command does not prevent the next command from succeeding.
- `ControlAgentHealthStateTests` - heartbeat-derived alive/degraded status, current
  command tracking, processed/failed counters.
- `ControlAgentHealthStateBackendConnectionTests` - the key Watchdog-safety guarantee:
  every `BackendConnectionState` value leaves an otherwise-healthy snapshot `HEALTHY`,
  and genuinely stale workers are `DEGRADED` regardless of backend connectivity.
- `ReconnectBackoffCalculatorTests` - initial delay, doubling, cap, reset-on-success.
- `ProtocolMessageSerializationTests` / `ProtocolJsonTests` - every protocol message type
  round-trips correctly, and malformed/typeless JSON is handled without throwing.
- `WssCommandSourceTests` - valid command accepted; invalid rejected; duplicate within
  the same connection session rejected; duplicate against an existing on-disk result
  rejected (the cross-source protection also exercised live - see
  docs/BACKEND_WEBSOCKET_TESTING.md); `AcknowledgeAsync` is a safe no-op.
- `OutgoingMessageChannelTests` / `WssResultSinkTests` - messages queue correctly even
  with no connection/reader active, confirming a disconnected backend can never throw.

`CloudOrc.WatchdogAgent.Tests`:

- `ConsecutiveFailureTrackerTests` - counter increments, threshold comparison, reset on
  success, and explicitly that a single transient failure does not reach the default
  threshold.
- `RecoveryRateLimiterTests` - allow with no prior attempts; deny once the per-window
  attempt cap is reached; allow again once the window rolls forward; deny immediately
  after a failed attempt until backoff elapses; backoff doubling up to the configured
  cap; backoff reset to zero after a success. Uses a `ManualTimeProvider` test double so
  these are deterministic and instant - no real `Task.Delay`/sleeping.
- `ControlAgentHealthClientTests` - real Named Pipe round trip: no listener present
  correctly returns null within the configured timeout; a real pipe server response is
  parsed correctly end-to-end.

### What's intentionally *not* unit tested

`ControlAgentServiceManager` (Windows Service query/restart via `ServiceController`) has
no unit tests. Querying/restarting a real Windows Service is an integration concern that
requires the service to actually be installed on the machine running the test - see the
manual scenarios below, and `docs/WINDOWS_SERVICE_INSTALLATION.md` for installing the
service to exercise this for real.

## Manual local testing scenarios

These mirror the automated tests above but exercise the full running process, matching
what an operator would actually do. All paths below assume the defaults in
`appsettings.json`.

### Control Agent Test 1 - basic success

```powershell
cd src\CloudOrc.ControlAgent
dotnet run
```

In a second terminal:

```powershell
@'
{
  "commandId": "test-001",
  "script": "Get-Date",
  "timeoutSeconds": 30
}
'@ | Set-Content -Path "C:\ProgramData\CloudOrc\ControlAgent\commands\test-001.json"
```

Expected within a couple of seconds:
`C:\ProgramData\CloudOrc\ControlAgent\results\test-001.result.json` exists with
`"status": "Success"` and the current date/time in `output`. The command file moves from
`commands\` to `completed\test-001.json`.

### Control Agent Test 2 - output capture

```powershell
@'
{
  "commandId": "test-002",
  "script": "Get-Service | Select-Object -First 5",
  "timeoutSeconds": 60
}
'@ | Set-Content -Path "C:\ProgramData\CloudOrc\ControlAgent\commands\test-002.json"
```

Expected: `results\test-002.result.json` with `"status": "Success"` and 5 lines in
`output`.

### Control Agent Test 3 - error handling

```powershell
@'
{
  "commandId": "test-003",
  "script": "Get-Service -Name \"DefinitelyDoesNotExist\"",
  "timeoutSeconds": 30
}
'@ | Set-Content -Path "C:\ProgramData\CloudOrc\ControlAgent\commands\test-003.json"
```

Expected: `results\test-003.result.json` with `"status": "Failed"` and a useful message
in `error` (e.g. `Cannot find any service with service name 'DefinitelyDoesNotExist'.`).
The Control Agent keeps running and keeps logging.

### Control Agent Test 4 - timeout

```powershell
@'
{
  "commandId": "timeout-test",
  "script": "Start-Sleep -Seconds 60",
  "timeoutSeconds": 5
}
'@ | Set-Content -Path "C:\ProgramData\CloudOrc\ControlAgent\commands\timeout-test.json"
```

Expected: after ~5 seconds (not 60), `results\timeout-test.result.json` shows
`"status": "Timeout"` with `durationMilliseconds` around 5000, and the log line
`"The pipeline has been stopped."` as the error. The Control Agent keeps running.

### Control Agent Test 5 - invalid JSON

```powershell
Set-Content -Path "C:\ProgramData\CloudOrc\ControlAgent\commands\invalid.json" -Value "{ this is not valid json"
```

Expected: the Control Agent logs a warning/error and does not crash. The file is moved to
`failed\invalid.json` unchanged (its raw content is preserved so you can inspect what was
wrong with it). No result file is written for it since no `CommandId` could be determined.

### Control Agent Test 6 - recovery after failure

Repeat Control Agent Test 1 (a fresh `CommandId`, e.g. `test-006`) after Tests 3/4/5.
Expected: it succeeds normally, proving one bad/slow command does not degrade the agent.

### Watchdog Test 1 - healthy, no restart

```powershell
# Terminal 1
cd src\CloudOrc.ControlAgent
dotnet run

# Terminal 2
cd src\CloudOrc.WatchdogAgent
dotnet run
```

Expected: every ~10s the Watchdog logs the Control Agent's service status (`NotInstalled`
in console/dev mode - expected, see below) and a `HEALTHY` response from the health pipe.
No restart is attempted.

### Watchdog Test 2 - Control Agent stopped

With both agents running from Test 1, stop the Control Agent (Ctrl+C in its terminal, or
`Stop-Process -Name CloudOrc.ControlAgent -Force`).

Expected: the Watchdog's health pipe check starts timing out. After
`ConsecutiveFailureThreshold` (default 3) consecutive failed checks, it logs "attempting
recovery." **In console/dev mode** (no Windows Service installed) recovery will fail with
a clearly logged reason ("service ... is not installed") - this is expected; see
`docs/WINDOWS_SERVICE_INSTALLATION.md` to test real restart-based recovery.

### Watchdog Test 3 - process alive but unresponsive

Hardest to simulate exactly without code changes, but the same mechanism as Test 2
covers it: the Watchdog never treats "service status = Running" alone as healthy - it
always requires a successful health pipe response. If you want to simulate this
specifically, you can temporarily block the pipe by holding a connection open with a
throwaway script, or simply reduce `Watchdog:HealthCheckTimeoutSeconds` in
`appsettings.json` to something very small (e.g. `1`) while the Control Agent is under
heavy load - transient timeouts are logged individually and only accumulate toward
recovery after crossing the threshold.

### Watchdog Test 4 - restart loop protection

Continue from Watchdog Test 2 with the Control Agent still stopped. Expected: after the
first failed recovery attempt, subsequent cycles log
`"Recovery is needed but was suppressed: Backing off after N consecutive failed recovery
attempt(s): Ns remaining..."` - the backoff grows (30s, 60s, 120s, ... by default) rather
than retrying every cycle. If you let it run long enough to exceed
`MaxRestartAttemptsPerWindow` within `RestartRateLimitWindowMinutes`, you'll additionally
see `"Rate limit reached"` as the suppression reason.

This exact sequence (threshold reached -> recovery attempted -> failed -> backoff
doubling 30s -> 60s -> suppressed with an accurate remaining-time countdown) was verified
against a real running instance during development, not just in the unit tests.
