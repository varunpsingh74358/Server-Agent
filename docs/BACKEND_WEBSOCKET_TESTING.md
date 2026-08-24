# Backend WebSocket Testing Guide

This covers the new local-testing WebSocket layer: the Control Agent's outbound
connection to a backend, and the local `CloudOrc.AgentTestServer` tool that stands in for
the real CloudOrc backend so this can all be exercised before touching it.

**Everything in this document is DEVELOPMENT/TESTING ONLY.** `ws://localhost` has no
encryption and no authentication - see [Security notes](#security-notes-read-this) at the
bottom before assuming any of this is production-ready.

## 1. The two command modes, and how they relate

| Mode | Config | Source of commands | Status |
|---|---|---|---|
| Local file | `ControlAgent.LocalFileModeEnabled` (default `true`) | JSON files in `commands\` | Original, unchanged |
| Backend WebSocket | `BackendConnection.Enabled` (default `false`) | COMMAND messages over WSS | New |

Both can run **at the same time** - they feed the same internal command queue and the
same generic PowerShell executor, and every result goes to every enabled sink (local file
and/or backend). This was verified directly: a command dropped as a local file was
processed and produced a result **while a WSS-sourced command was also executing**, with
no interference between them.

## 2. Starting everything for local testing

Three processes, three terminals, in this order:

### Terminal 1 - Local Test Server (stands in for the real backend)

```powershell
cd tools\CloudOrc.AgentTestServer
dotnet run
```

You should see:

```
======================================================================
 CloudOrc Agent Test Server - DEVELOPMENT ONLY
 This is NOT the real CloudOrc backend and has no authentication.
 Listening on ws://localhost:5299/agent (loopback only)
======================================================================
Commands: send <script> | send --timeout <seconds> <script> | ping | help | exit
```

Port is configurable via `tools/CloudOrc.AgentTestServer/appsettings.json`
(`TestServer:Port`, default `5299`). The bind address is also configurable — see
[§10. Cross-machine (LAN) development testing](#10-cross-machine-lan-development-testing)
below if you need the Control Agent running on a *different* physical/virtual machine to
reach this test server; the default shown above (`localhost`, loopback-only) is what you
want for everything else in this document.

### Terminal 2 - Control Agent, with backend connectivity turned on

The safe default in `appsettings.json` is `BackendConnection.Enabled: false`. For a
one-off local test, override it on the command line instead of editing the file:

```powershell
cd src\CloudOrc.ControlAgent
dotnet run --BackendConnection:Enabled=true --BackendConnection:Url=ws://localhost:5299/agent --BackendConnection:DevelopmentAllowInsecureWs=true
```

`DevelopmentAllowInsecureWs=true` is required because the URL uses `ws://` - see
[Security notes](#security-notes-read-this). Leaving it `false` (the default) with a
`ws://` URL makes the agent refuse to start with a clear error, on purpose.

Expected log lines:

```
[INF] Backend connection worker starting. Target: ws://localhost:5299/agent (agentId=local-test-agent, serverId=local-test-server).
[INF] Connected to backend at ws://localhost:5299/agent.
[INF] Sent HELLO (agentId=local-test-agent, machineId=<a real Windows MachineGuid>).
```

### Terminal 3 - Watchdog Agent (optional, for the Watchdog-specific checks below)

```powershell
cd src\CloudOrc.WatchdogAgent
dotnet run
```

## 3. Verifying HELLO

Immediately after Terminal 2 connects, Terminal 1 prints:

```
[test-server] Agent connected.
[HELLO] {"type":"HELLO","agentId":"local-test-agent","serverId":"local-test-server","machineId":"...","machineName":"...","agentVersion":"...","timestamp":"..."}
```

`machineId` is the real Windows `MachineGuid` (read from
`HKLM\SOFTWARE\Microsoft\Cryptography`) - confirmed against a real value during
development, not a placeholder.

## 4. Verifying HEARTBEAT

Every `BackendConnection.HeartbeatIntervalSeconds` (default 15s) while connected:

```
[HEARTBEAT] {"type":"HEARTBEAT","agentId":"local-test-agent",...,"status":"HEALTHY","workerAlive":true,"currentCommandId":null,"currentCommandStatus":null,"lastActivityAt":"..."}
```

`currentCommandId`/`currentCommandStatus` populate with the real in-flight command when
one is running - this is read directly from the same health state the Watchdog also
reads, not a separate fabricated value.

## 5. Verifying TELEMETRY

Every `BackendConnection.TelemetryIntervalSeconds` (default 10s) while connected:

```
[TELEMETRY] {"type":"TELEMETRY",...,"machine":{"machineName":"...","os":"Microsoft Windows ..."},"cpu":{"usagePercent":6.1},"memory":{"totalBytes":25768787968,"usedBytes":9396817920,"availableBytes":16371970048},"disks":[{"name":"C:\\","totalBytes":...,"usedBytes":...,"freeBytes":...}, ...],"uptimeSeconds":1074724}
```

Confirmed live with real values for CPU%, memory, and **every mounted drive** on the test
machine (not just `C:`). The very first CPU reading after startup can be inaccurate
(`PerformanceCounter` needs two samples to compute a rate) - subsequent readings are
accurate.

## 6. Sending commands: Get-Date, Get-Service, failures, timeouts

Two ways to send a COMMAND to the connected agent:

**A. Console input in Terminal 1** (the documented, primary way):

```
send Get-Date
send Get-Service | Select-Object -First 5
send --timeout 5 Start-Sleep -Seconds 60
```

**B. `POST /send`** (scriptable alternative - useful for automated local testing, or when
the test server isn't attached to an interactive terminal):

```powershell
Invoke-RestMethod -Uri "http://localhost:5299/send" -Method Post -ContentType "application/json" `
    -Body (@{ script = "Get-Date"; timeoutSeconds = 30 } | ConvertTo-Json)
```

Both produce the same message flow in Terminal 1:

```
[COMMAND_STATUS] {"type":"COMMAND_STATUS","commandId":"test-...","correlationId":"corr-...","status":"Queued",...}
[COMMAND_STATUS] {"type":"COMMAND_STATUS","commandId":"test-...","correlationId":"corr-...","status":"Running",...}
[COMMAND_RESULT] {"type":"COMMAND_RESULT","commandId":"test-...","correlationId":"corr-...","status":"Success","output":["<date>"],"error":null,"exitCode":null,...}
```

The `send` console command generates and sends its own `correlationId` alongside `commandId`, mirroring what a real backend is expected to do. The COMMAND envelope it sends looks like:

```json
{
  "type": "COMMAND",
  "commandId": "test-...",
  "correlationId": "corr-...",
  "commandType": "powershell-exec",
  "createdAt": "...",
  "parameters": { "script": "Get-Date", "timeoutSeconds": 30 }
}
```

`exitCode` is populated only when the script explicitly calls `exit <n>` - PowerShell has no implicit process exit code otherwise, so it stays `null`.

**Failed command** - send `Get-Service -Name "DefinitelyDoesNotExist"`:

```
[COMMAND_RESULT] {..."status":"Failed",...,"error":"Cannot find any service with service name 'DefinitelyDoesNotExist'.","exitCode":null}
```

**Timeout** - `send --timeout 3 Start-Sleep -Seconds 30`:

```
[COMMAND_RESULT] {..."status":"Timeout","durationMilliseconds":3025,"error":"The pipeline has been stopped."}
```

All three were confirmed with real runs - the timeout genuinely stopped at ~3 seconds,
not 30.

## 7. Testing reconnection

1. With the Control Agent connected (Terminal 2 shows `Connected to backend`), stop
   Terminal 1 (Ctrl+C, or close it).
2. Terminal 2 logs a connection failure and a growing backoff delay:
   ```
   [WRN] Backend connection attempt failed.
   [INF] Reconnecting to backend in 4s.
   ...
   [INF] Reconnecting to backend in 32s.
   ```
   (Confirmed live: the delay doubles - 4s, 8s, 16s, 32s - capped at
   `ReconnectMaximumDelaySeconds`.)
3. **While disconnected, drop a local command file** (see
   [docs/TESTING.md](TESTING.md)) - it still completes normally. This is the key
   guarantee: local processing is entirely independent of backend connectivity.
4. Restart Terminal 1 (`dotnet run` again in `tools\CloudOrc.AgentTestServer`).
5. Terminal 2 reconnects on its next scheduled attempt and sends a fresh HELLO:
   ```
   [INF] Connected to backend at ws://localhost:5299/agent.
   [INF] Sent HELLO (...)
   ```
6. Any COMMAND_RESULT/COMMAND_STATUS messages generated while disconnected are delivered
   as soon as the connection is back (they were queued in memory, not dropped) - confirmed
   live: a result generated entirely during the outage appeared in Terminal 1 immediately
   after reconnection.

## 8. Verifying the Control Agent stays healthy while disconnected

Query the local health pipe indirectly via the Watchdog's logs (Terminal 3), or directly:
while Terminal 1 is stopped, Terminal 2's own health never changes because of it - only
`backendConnectionState` in the health snapshot changes (`Connected` ->
`Reconnecting`/`Disconnected`). `status` stays `HEALTHY` throughout, confirmed live.

## 9. Verifying Watchdog behavior with backend connectivity in the picture

With Terminal 3 (Watchdog) running against a Control Agent that has
`BackendConnection.Enabled=true`:

```
[INF] Health check response: status=HEALTHY, detectionWorkerAlive=True, processingWorkerAlive=True, currentCommandId=(none), backendConnectionState=Connected.
```

Stop Terminal 1 (the test server) and watch the Watchdog's log across the outage:

```
[INF] Health check response: status=HEALTHY, ..., backendConnectionState=Connecting.
[INF] Health check response: status=HEALTHY, ..., backendConnectionState=Reconnecting.
[INF] Health check response: status=HEALTHY, ..., backendConnectionState=Reconnecting.
```

`status` stays `HEALTHY` the entire time - confirmed live across a real outage. The
Watchdog **never** attempts recovery of the Control Agent for this reason alone.
`backendConnectionState` is logged purely for operator visibility; it is not read
anywhere in the Watchdog's healthy/unhealthy decision logic (see
[docs/ARCHITECTURE.md](ARCHITECTURE.md)).

To actually trigger Watchdog recovery, the Control Agent's *local* health (not backend
connectivity) must fail - see the existing scenarios in
[docs/TESTING.md](TESTING.md#watchdog-test-2---control-agent-stopped).

## 10. Cross-machine (LAN) development testing

Everything above runs the test server and Control Agent on the same machine
(`ws://localhost:5299/agent`), which cannot be reached from a genuinely separate Windows
Server. To test the Control Agent running on one machine against the test server running
on another (still development/testing only, never production), the test server's bind
address is configurable.

### Config options (`tools/CloudOrc.AgentTestServer/appsettings.json`)

| Setting | Default | Meaning |
|---|---|---|
| `TestServer.Port` | `5299` | Unchanged from local-only testing |
| `TestServer.BindAddress` | `localhost` | The address Kestrel binds to. Leave as `localhost` for everything in this document above. Set to `0.0.0.0` (all interfaces) or a specific NIC IP for cross-machine testing. |
| `TestServer.AllowNonLoopbackBinding` | `false` | Safety gate. Must be explicitly `true` for any non-loopback `BindAddress` to be accepted — otherwise the server refuses to start with a clear error, the same pattern as `BackendConnection.DevelopmentAllowInsecureWs` on the Control Agent. |

### Enabling LAN testing

On the machine that will run the test server, find its LAN IP (e.g.
`ipconfig` → the `IPv4 Address` under your active network adapter — for example
`10.47.145.175`), then start the server with the bind address opened up:

```powershell
cd tools\CloudOrc.AgentTestServer
dotnet run --TestServer:BindAddress=0.0.0.0 --TestServer:AllowNonLoopbackBinding=true
```

You should see:

```
======================================================================
 CloudOrc Agent Test Server - DEVELOPMENT ONLY
 This is NOT the real CloudOrc backend and has no authentication.
 Listening on ws://0.0.0.0:5299/agent (LAN-reachable - DEVELOPMENT ONLY, no authentication)
 WARNING: bound beyond loopback for cross-machine development testing.
 Do not expose this address beyond a trusted development network.
======================================================================
```

Confirmed live: with the server bound this way, `netstat -ano` on that machine shows
`TCP 0.0.0.0:5299 ... LISTENING` (versus `127.0.0.1:5299` in the default/loopback mode),
and both `http://<that machine's LAN IP>:5299/` and `POST http://<LAN IP>:5299/send`
respond correctly from outside `localhost`.

On the **other** machine (or the same machine, addressing itself by LAN IP instead of
`localhost`, as a stand-in for a genuinely separate server), point the Control Agent at
that IP instead of `localhost`:

```powershell
cd src\CloudOrc.ControlAgent
dotnet run --BackendConnection:Enabled=true --BackendConnection:Url=ws://10.47.145.175:5299/agent --BackendConnection:DevelopmentAllowInsecureWs=true
```

(Replace `10.47.145.175` with the test server machine's actual LAN IP.)

Confirmed live: the Control Agent logs `Connected to backend at
ws://10.47.145.175:5299/agent.` and sends HELLO, and the test server logs `[test-server]
Agent connected.` followed by the same `[HELLO]` line as the loopback case — every
scenario in §3–§9 above (HEARTBEAT, TELEMETRY, sending commands, reconnection, Watchdog
behavior) works identically once connected this way; only the URL changed.

### Going back to loopback-only

Simply omit both overrides (or set `TestServer.BindAddress` back to `localhost` /
`TestServer.AllowNonLoopbackBinding` back to `false`, or edit `appsettings.json` back to
its defaults) — `dotnet run` with no arguments always uses the safe loopback-only default.

### Firewall note

Windows Firewall may prompt to allow `dotnet.exe`/the published `.exe` through on first
LAN-bound run — allow it only for the network profile you're actually testing on (e.g.
"Private"), and only for as long as you're actively doing cross-machine testing.

## Security notes (read this)

- `ws://localhost` has **no encryption and no authentication**. Anything sent over it on
  this machine is visible to anything else on this machine that cares to look.
- `BackendConnection.DevelopmentAllowInsecureWs` exists specifically to make this
  unsafe-by-default: a `ws://` URL is refused at startup unless this is explicitly set to
  `true`. There is no way to "accidentally" end up connected over an insecure URL in a
  default configuration.
- The same pattern applies to the test server's own bind address (§10): it listens on
  `localhost` only unless `TestServer.AllowNonLoopbackBinding` is explicitly set to `true`.
  When it *is* opened up to `0.0.0.0`/a LAN IP for cross-machine testing, it is reachable
  by **anything on that network** with no authentication at all — treat that mode as
  "open microphone on the LAN," only run it on a trusted development network, only for as
  long as you're actively testing, and never on a machine reachable from the public
  internet.
- `AgentId`/`ServerId` are plain locally-configured strings for this phase - there is no
  enrollment, no credential, no proof that the agent connecting is who it claims to be.
- **Production requirement (not built yet)**: a real deployment must use `wss://` (TLS)
  against the real backend, with the agent authenticated via whatever
  enrollment/credential mechanism the backend defines. See
  [docs/FUTURE_BACKEND_INTEGRATION.md](FUTURE_BACKEND_INTEGRATION.md) for exactly what
  that involves and where it plugs into this same protocol.
