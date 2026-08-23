# Deployment Guide (Self-Contained Windows Package)

This document is the step-by-step guide for copying the **published, self-contained**
CloudOrc Windows Agents package to another Windows Server and running it there,
independently of this development machine. It complements, and does not replace,
[docs/WINDOWS_SERVICE_INSTALLATION.md](WINDOWS_SERVICE_INSTALLATION.md) (which covers a
framework-dependent publish from the dev machine) — this guide assumes the
**self-contained** publish described below, which does not require .NET to be installed
on the target server at all.

This is still the **local-development/test version**: it does not connect to the real
CloudOrc backend, has no production authentication, and is not a polished installer. See
[docs/FUTURE_BACKEND_INTEGRATION.md](FUTURE_BACKEND_INTEGRATION.md) for what's still
missing before production use.

## 1. What files to copy

From the build machine, after publishing (see [Producing the package](#producing-the-package-on-the-build-machine) below), copy the two folders:

```
publish\win-x64\ControlAgent\
publish\win-x64\WatchdogAgent\
```

Copy them **as complete folders** — every file inside each one is required (the .NET
runtime files, `Microsoft.PowerShell.SDK` and its dependencies, `appsettings.json`, etc.),
not just the `.exe`. Use `robocopy`, a zip, or a file share — whatever gets the whole
folder tree across intact.

## 2. Where to copy them on the target server

Any local path works; a directory not under `Program Files` avoids extra ACL issues
without granting anything unnecessary. This guide assumes:

```
E:\CloudOrc\ControlAgent\
E:\CloudOrc\WatchdogAgent\
```

Adjust every `binPath=` command below if you use a different path.

## 3. Is .NET required on the target server?

**No.** These are self-contained publishes (`--self-contained true -r win-x64`) — the
.NET 10 runtime is included inside each folder (`hostfxr.dll`, `coreclr.dll`,
`System.*.dll`, etc.). The target server needs **no .NET SDK, no .NET runtime, no Visual
Studio, no VS Code, no Node.js, no Docker** — only:

- Windows (both agents use Windows-only APIs).
- The [Visual C++ Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)
  is normally already present on Windows Server; it underlies the self-contained .NET
  runtime. If a service fails to start with a native-library load error, install it.

## 4. Required Windows permissions

- An **elevated (Administrator)** PowerShell/Command Prompt session is required to
  install/start/stop/remove the services (`sc.exe` requires it).
- By default `sc.exe create` without `obj=`/`password=` runs the service as
  `LocalSystem`, which has enough privilege for both agents. See
  [docs/WINDOWS_SERVICE_INSTALLATION.md §3](WINDOWS_SERVICE_INSTALLATION.md#3-required-permissions)
  for running as a narrower, dedicated service account instead.
- The account running the Control Agent needs read/write access to its `DataDirectory`
  (`C:\ProgramData\CloudOrc\ControlAgent` by default) and whatever rights the PowerShell
  scripts it will be asked to run actually require.
- The account running the Watchdog needs permission to query/start/stop the
  `CloudOrcControlAgent` service and read/write access to
  `C:\ProgramData\CloudOrc\WatchdogAgent`. It never executes PowerShell.

## 5. Configuring Agent ID and Server ID

Edit `AgentIdentity` in `<ControlAgent folder>\appsettings.json`:

```json
"AgentIdentity": {
  "AgentId": "your-agent-id-here",
  "ServerId": "your-server-id-here"
}
```

These are plain configuration values in this phase — there is no enrollment step yet
(see [docs/FUTURE_BACKEND_INTEGRATION.md](FUTURE_BACKEND_INTEGRATION.md)). `MachineId`
(the real Windows `MachineGuid`) and `MachineName` are derived automatically at runtime
and are not configured here.

You can also override this per-run without editing the file, e.g. by passing
`--AgentIdentity:AgentId=...` as a service argument, but editing `appsettings.json` is
simpler for a permanent deployment.

## 6. Configuring the backend URL

Edit `BackendConnection` in `<ControlAgent folder>\appsettings.json`:

```json
"BackendConnection": {
  "Enabled": true,
  "Url": "wss://your-real-backend/agent",
  "DevelopmentAllowInsecureWs": false,
  "ConnectTimeoutSeconds": 10,
  "ReconnectInitialDelaySeconds": 2,
  "ReconnectMaximumDelaySeconds": 60,
  "HeartbeatIntervalSeconds": 15,
  "TelemetryIntervalSeconds": 10
}
```

- Leave `Enabled: false` (the default) to run in local-file-only mode.
- For **local/test backend testing on this server** (pointing at
  `tools/CloudOrc.AgentTestServer` running somewhere reachable), use a `ws://` URL and set
  `DevelopmentAllowInsecureWs: true`. The agent refuses to start with `ws://` unless this
  is explicitly set.
- For a real backend, use `wss://` and leave `DevelopmentAllowInsecureWs: false` — this is
  enforced at startup (`Program.cs` throws otherwise), not just documented.
- **There is still no production authentication/enrollment** — see
  [docs/FUTURE_BACKEND_INTEGRATION.md](FUTURE_BACKEND_INTEGRATION.md). Do not point this
  at a real backend expecting any credential exchange to happen; none exists yet.

## 7. Configuring the telemetry interval

`BackendConnection.TelemetryIntervalSeconds` in `appsettings.json` (default `10`). Only
takes effect when `BackendConnection.Enabled` is `true` — telemetry is not published in
local-file-only mode. Telemetry is **periodic on this interval, not instantaneous** — a
snapshot is a point-in-time read of CPU/memory/disk/uptime taken every N seconds, not a
continuous stream.

## 8. Configuring the heartbeat interval

`BackendConnection.HeartbeatIntervalSeconds` in `appsettings.json` (default `15`). Same
conditions as telemetry — only sent while `BackendConnection.Enabled` and connected.

## 9. Installing the Control Agent as a Windows Service

From an elevated prompt on the target server:

```powershell
sc.exe create CloudOrcControlAgent `
    binPath= "E:\CloudOrc\ControlAgent\CloudOrc.ControlAgent.exe" `
    DisplayName= "CloudOrc Control Agent" `
    start= auto

sc.exe description CloudOrcControlAgent "Generic local PowerShell execution engine for CloudOrc."
```

> `sc.exe` requires a literal space after each `=` (`binPath= "..."`, not `binPath="..."`)
> — this is `sc.exe` syntax, not a typo.

## 10. Installing the Watchdog as a Windows Service

```powershell
sc.exe create CloudOrcWatchdogAgent `
    binPath= "E:\CloudOrc\WatchdogAgent\CloudOrc.WatchdogAgent.exe" `
    DisplayName= "CloudOrc Watchdog Agent" `
    start= auto

sc.exe description CloudOrcWatchdogAgent "Monitors and recovers the CloudOrc Control Agent."
```

## 11. Starting the services

Start the Control Agent first, then the Watchdog:

```powershell
sc.exe start CloudOrcControlAgent
sc.exe start CloudOrcWatchdogAgent
```

(`Start-Service CloudOrcControlAgent` / `Start-Service CloudOrcWatchdogAgent` work
identically once registered.)

## 12. Stopping the services

Stop the Watchdog first so it doesn't observe (and try to "recover" from) the Control
Agent stopping intentionally, then stop the Control Agent:

```powershell
sc.exe stop CloudOrcWatchdogAgent
sc.exe stop CloudOrcControlAgent
```

## 13. Checking service status

```powershell
sc.exe query CloudOrcControlAgent
sc.exe query CloudOrcWatchdogAgent
```

or `Get-Service CloudOrcControlAgent`, `Get-Service CloudOrcWatchdogAgent`.

## 14. Viewing logs

Both agents log only to files when running as a service (there is no console to write
to):

```
C:\ProgramData\CloudOrc\ControlAgent\logs\controlagent-YYYYMMDD.log
C:\ProgramData\CloudOrc\WatchdogAgent\logs\watchdogagent-YYYYMMDD.log
```

One file per day, 14 days retained, rolled automatically by Serilog. Directories are
created automatically on first start if missing.

## 15. Uninstalling the services

```powershell
sc.exe stop CloudOrcWatchdogAgent
sc.exe stop CloudOrcControlAgent
sc.exe delete CloudOrcWatchdogAgent
sc.exe delete CloudOrcControlAgent
```

`sc.exe delete` removes only the service registration — it does **not** touch
`C:\ProgramData\CloudOrc\...` or the published files. See `scripts\uninstall-agent.ps1`
for a scripted version of this with clear prompts.

## 16. Completely removing the agent

After uninstalling the services (step 15):

```powershell
Remove-Item -Recurse -Force "E:\CloudOrc\ControlAgent"
Remove-Item -Recurse -Force "E:\CloudOrc\WatchdogAgent"
Remove-Item -Recurse -Force "C:\ProgramData\CloudOrc"
```

The last command deletes all commands/results/logs history for both agents — only run it
if you genuinely want a clean slate; nothing does this automatically.

## 17. Troubleshooting startup failures

| Symptom | Likely cause | What to check |
|---|---|---|
| `sc.exe start` returns immediately with an error, service never reaches RUNNING | Bad `binPath`, missing files, or a startup exception | `sc.exe query <name>` for the exit code; then the log file under `C:\ProgramData\CloudOrc\<Agent>\logs\` — an exception during startup is logged before the process exits. If no log file exists yet, the process crashed before Serilog initialized; check Windows Event Viewer → Application log for the raw exception. |
| Control Agent throws `BackendConnection.Url ('ws://...') uses insecure ws://, but DevelopmentAllowInsecureWs is false` | Intentional safety check — see [§6](#6-configuring-the-backend-url) | Set `DevelopmentAllowInsecureWs: true` only if this really is a local/test `ws://` endpoint, otherwise switch the URL to `wss://` |
| Control Agent starts but no commands are ever picked up | `LocalFileModeEnabled` and `BackendConnection.Enabled` are both `false` | Check `appsettings.json` — the agent logs a warning ("this agent will not receive any commands from any source") at startup in this case |
| Watchdog logs `Control Agent service 'CloudOrcControlAgent' status: NotInstalled` | The Control Agent isn't registered as a Windows Service (yet), or the service name doesn't match `Watchdog.ControlAgentServiceName` | Confirm step 9 above was run and the name matches exactly (`CloudOrcControlAgent`) |
| Watchdog never recovers a genuinely-dead Control Agent | Consecutive-failure threshold not yet reached, or rate-limited/backing off | Check `Watchdog.ConsecutiveFailureThreshold` and the Watchdog's own log for `"Recovery is needed but was suppressed"` — this is by design, not a bug |
| A PowerShell command always returns `Failed` with a module-loading / execution-policy error (e.g. `NetTCPIP` module) | The target server's **PowerShell execution policy** blocks loading binary/script modules, independent of this agent | This is environment configuration, not an agent defect — the executor is working exactly as designed (captured the real error, stayed alive, ran the next command). Adjust the execution policy on the target server if the scripts you intend to run need those modules. |
| Service runs as `LocalSystem` and a script needs different rights (e.g. a UNC path, a domain resource) | `LocalSystem` has strong local privilege but no network identity | Configure a dedicated domain/service account per [docs/WINDOWS_SERVICE_INSTALLATION.md §3](WINDOWS_SERVICE_INSTALLATION.md#3-required-permissions) |

## Producing the package (on the build machine)

Run from the repository root, with the .NET 10 SDK installed (only needed on the *build*
machine, never the target server):

```powershell
dotnet publish src\CloudOrc.ControlAgent\CloudOrc.ControlAgent.csproj `
    -c Release -r win-x64 --self-contained true `
    -o publish\win-x64\ControlAgent

dotnet publish src\CloudOrc.WatchdogAgent\CloudOrc.WatchdogAgent.csproj `
    -c Release -r win-x64 --self-contained true `
    -o publish\win-x64\WatchdogAgent
```

`win-x64` is used because that is the architecture of every machine this has been built
and tested on; use a different RID (e.g. `win-arm64`) only if the target server's CPU
architecture actually requires it.

Verify before copying anywhere:

```powershell
Get-Item publish\win-x64\ControlAgent\CloudOrc.ControlAgent.exe
Get-Item publish\win-x64\WatchdogAgent\CloudOrc.WatchdogAgent.exe
Get-Item publish\win-x64\ControlAgent\appsettings.json
Get-Item publish\win-x64\WatchdogAgent\appsettings.json
Get-Item publish\win-x64\ControlAgent\hostfxr.dll   # confirms the runtime is bundled
```

## Security posture of this package (read before deploying anywhere reachable over a network)

- No hardcoded secrets, production URLs, or credentials exist anywhere in this
  repository or in the published output — verified by inspection as part of this
  deployment prep.
- The local Agent Test Server (`tools/CloudOrc.AgentTestServer`) binds to
  `http://localhost:<port>` only (`UseUrls("http://localhost:{port}")` in its
  `Program.cs`) — it never listens on `0.0.0.0` or any externally reachable interface,
  and is not part of this published package in the first place (it lives under `tools/`,
  not `src/`, and nothing in either agent's publish output references it).
- `BackendConnection.DevelopmentAllowInsecureWs` defaults to `false`; a `ws://` URL is
  refused at startup unless explicitly overridden — there is no accidental-insecure-mode
  code path.
- There is **no agent authentication/enrollment/credential mechanism yet** — this is a
  known, documented gap for Phase 3, not an oversight. Do not expose a Control Agent with
  `BackendConnection.Enabled=true` pointed at an untrusted or public endpoint.
- See [22. SECURITY CHECK](#) in the final validation report (delivered alongside this
  document) for the full current-vs-future security breakdown.
