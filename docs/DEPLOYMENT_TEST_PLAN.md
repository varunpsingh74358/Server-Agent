# Deployment Test Plan

A fixed test sequence for validating the self-contained published package
(`publish\win-x64\ControlAgent\`, `publish\win-x64\WatchdogAgent\`) on a clean Windows
Server, separate from the development machine. Some steps were already executed once,
for real, on the development machine as part of the final validation pass documented
alongside this plan — those are marked **(already verified on dev machine)** below, with
what was actually observed. Steps that inherently require a *second, separate* machine
(a genuinely clean server, a reboot) are marked **MANUAL TEST REQUIRED** with exact
commands to run yourself.

Run the numbered tests in order — several depend on state left behind by earlier ones.

---

**TEST 1 — Copy published package to clean Windows Server**

Copy `publish\win-x64\ControlAgent\` and `publish\win-x64\WatchdogAgent\` (entire folders)
to the target server, e.g. `E:\CloudOrc\ControlAgent\` and `E:\CloudOrc\WatchdogAgent\`.

**MANUAL TEST REQUIRED** — requires an actual second machine or clean VM. How to perform:
copy both folders via `robocopy`, a zip transfer, or a file share; confirm both `.exe`
files and their `appsettings.json` arrived intact (`Get-ChildItem -Recurse | Measure-Object`
on source vs. destination should match in file count).

---

**TEST 2 — Verify .NET SDK is NOT required**

On the target server, confirm `dotnet` is either absent or irrelevant, then run the
Control Agent executable directly.

**(Already verified on dev machine)**: the published executable was launched directly
(`.\CloudOrc.ControlAgent.exe`, no `dotnet run`, no SDK invocation) and started
successfully, confirming the self-contained runtime (`hostfxr.dll`, `coreclr.dll`, and all
`Microsoft.PowerShell.SDK` dependencies) is fully bundled. On a genuinely clean server
(no .NET installed at all), additionally confirm:

```powershell
dotnet --version   # expect "not recognized" or similar — the target server needs no SDK
```

**MANUAL TEST REQUIRED** only for confirming absence of `dotnet` on a truly clean machine.

---

**TEST 3 — Install Control Agent service**

```powershell
sc.exe create CloudOrcControlAgent binPath= "E:\CloudOrc\ControlAgent\CloudOrc.ControlAgent.exe" DisplayName= "CloudOrc Control Agent" start= auto
```

**(Already verified on dev machine)**: `sc.exe create` returned `[SC] CreateService SUCCESS`.

---

**TEST 4 — Install Watchdog service**

```powershell
sc.exe create CloudOrcWatchdogAgent binPath= "E:\CloudOrc\WatchdogAgent\CloudOrc.WatchdogAgent.exe" DisplayName= "CloudOrc Watchdog Agent" start= auto
```

**(Already verified on dev machine)**: `sc.exe create` returned `[SC] CreateService SUCCESS`.

---

**TEST 5 — Start both services**

```powershell
sc.exe start CloudOrcControlAgent
sc.exe start CloudOrcWatchdogAgent
```

**(Already verified on dev machine)**: both transitioned `START_PENDING` → `RUNNING`.

---

**TEST 6 — Verify both services are RUNNING**

```powershell
sc.exe query CloudOrcControlAgent
sc.exe query CloudOrcWatchdogAgent
```

**(Already verified on dev machine)**: both reported `STATE: 4 RUNNING`.

---

**TEST 7 — Verify Control Agent connects to local development/test backend**

Start `tools\CloudOrc.AgentTestServer` (reachable from the target server), reconfigure the
installed Control Agent's `appsettings.json` (`BackendConnection.Enabled=true`,
`Url=ws://<test-server-host>:5299/agent`, `DevelopmentAllowInsecureWs=true`), restart the
service, and confirm the test server prints `[test-server] Agent connected.`

**(Already verified on dev machine, console mode)**: connection established
(`Connected to backend at ws://localhost:5299/agent.`) — same code path runs identically
under a Windows Service. Re-run against the installed service on the target server to
confirm end to end.

**MANUAL TEST REQUIRED** for the cross-machine variant (test server and agent on separate
hosts) — the connectivity logic itself is unchanged, but firewall/network reachability
between two real machines has not been exercised here.

---

**TEST 8 — Verify HELLO**

Check the test server console/log for a `[HELLO]` line with real `machineId`/`machineName`.

**(Already verified on dev machine)**:
`{"type":"HELLO","agentId":"local-test-agent",...,"machineId":"52e64628-1d06-4486-9cd0-82c6d21f91cd","machineName":"WIN-7FD5JOCI12B",...}` — real Windows `MachineGuid`.

---

**TEST 9 — Verify HEARTBEAT**

**(Already verified on dev machine)**: `[HEARTBEAT]` messages observed at the configured
15-second interval (`status=HEALTHY, workerAlive=true`), including one correctly showing
`currentCommandId`/`currentCommandStatus` populated while a command was running.

---

**TEST 10 — Verify TELEMETRY**

**(Already verified on dev machine)**: `[TELEMETRY]` messages observed at the configured
10-second interval with real CPU%, memory (total/used/available bytes), all 10 mounted
drives on the test machine, and `uptimeSeconds`.

---

**TEST 11 — Send Get-Date**

**(Already verified on dev machine)**: `COMMAND_RESULT` with `status=Success`,
`output=["23-08-2026 10:21:50"]` (real timestamp).

---

**TEST 12 — Send Get-Service**

**(Already verified on dev machine)**: `Get-Service | Select-Object -First 5` returned
`status=Success` with 5 real service names.

---

**TEST 13 — Send invalid PowerShell command**

**(Already verified on dev machine)**: `Get-Service -Name "DefinitelyDoesNotExist"`
returned `status=Failed`, `error="Cannot find any service with service name
'DefinitelyDoesNotExist'."` — agent stayed alive, next command still processed.

---

**TEST 14 — Send timeout command**

**(Already verified on dev machine)**: `Start-Sleep -Seconds 30` with a 4s configured
timeout returned `status=Timeout`, `durationMilliseconds=4017` — the pipeline was
genuinely stopped early, not left to complete.

---

**TEST 15 — Stop backend/test server**

**(Already verified on dev machine)**: test server process stopped.

---

**TEST 16 — Verify Agent stays alive**

**(Already verified on dev machine)**: Control Agent kept running, logged
`Backend connection attempt failed` and growing reconnect delays; local file command
processing continued uninterrupted during the outage (a command dropped mid-outage
completed with `status=Success` in 17ms).

---

**TEST 17 — Start backend/test server**

**(Already verified on dev machine)**: test server restarted.

---

**TEST 18 — Verify Agent reconnects**

**(Already verified on dev machine)**: reconnect backoff observed as `2s → 4s → 8s → 16s
→ 32s`, then `Connected to backend at ws://localhost:5299/agent.` with a fresh `Sent
HELLO`. The `COMMAND_RESULT` generated during the outage (Test 16) was delivered to the
test server immediately upon reconnect, confirming the queued-message guarantee.

---

**TEST 19 — Stop Control Agent**

```powershell
sc.exe stop CloudOrcControlAgent
```

**(Already verified on dev machine)**: service transitioned to `STOPPED`.

---

**TEST 20 — Verify Watchdog detects failure**

**(Already verified on dev machine, real installed service)**: Watchdog logged
`Control Agent service 'CloudOrcControlAgent' status: Stopped.` and health-pipe timeouts,
counting `(1/3)` → `(2/3)` → `(3/3)` at the configured 10s check interval.

---

**TEST 21 — Verify Watchdog attempts recovery**

**(Already verified on dev machine, real installed service)**: at the threshold, Watchdog
logged `Consecutive failure threshold reached; attempting recovery of service
'CloudOrcControlAgent'.` and `Starting service CloudOrcControlAgent.` — a real
`ServiceController.Start()` call, not a simulation.

---

**TEST 22 — Verify Control Agent becomes healthy**

**(Already verified on dev machine, real installed service)**: 15 seconds after the
restart was issued, Watchdog logged `Recovery succeeded; Control Agent is healthy again.`
`sc.exe query CloudOrcControlAgent` independently confirmed `STATE: 4 RUNNING`.

---

**TEST 23 — Reboot Windows Server**

**MANUAL TEST REQUIRED** — this validation pass did not reboot the machine (both services
were installed with `start= demand` for this exact reason, and removed afterward — see
below). To perform: with both services installed via `start= auto` (per
[docs/DEPLOYMENT.md](DEPLOYMENT.md)), reboot the target server.

---

**TEST 24 — Verify both services automatically start**

**MANUAL TEST REQUIRED** (depends on Test 23). After reboot:

```powershell
sc.exe query CloudOrcControlAgent
sc.exe query CloudOrcWatchdogAgent
```

Both should show `STATE: 4 RUNNING` without any manual `sc.exe start`, since `start= auto`
registers them to start with Windows.

---

**TEST 25 — Verify Agent reconnects after reboot**

**MANUAL TEST REQUIRED** (depends on Test 23/24). With `BackendConnection.Enabled=true`
configured, confirm the post-reboot Control Agent log
(`C:\ProgramData\CloudOrc\ControlAgent\logs\controlagent-*.log`) shows a fresh
`Connected to backend` / `Sent HELLO` sequence without operator intervention.

---

**TEST 26 — Verify heartbeat and telemetry resume**

**MANUAL TEST REQUIRED** (depends on Test 23/24/25). Confirm HEARTBEAT/TELEMETRY messages
resume arriving at the backend/test server on their configured intervals after the reboot,
with no manual restart of either agent needed.

---

## Summary of what this pass actually covered vs. left manual

| # | Test | Result |
|---|---|---|
| 1 | Copy to clean server | MANUAL TEST REQUIRED |
| 2 | .NET SDK not required | Verified (exe launched standalone); full clean-machine confirmation MANUAL TEST REQUIRED |
| 3 | Install Control Agent service | Verified |
| 4 | Install Watchdog service | Verified |
| 5 | Start both services | Verified |
| 6 | Both RUNNING | Verified |
| 7 | Connect to local test backend | Verified (console mode); cross-machine MANUAL TEST REQUIRED |
| 8 | HELLO | Verified |
| 9 | HEARTBEAT | Verified |
| 10 | TELEMETRY | Verified |
| 11 | Get-Date | Verified |
| 12 | Get-Service | Verified |
| 13 | Invalid command | Verified |
| 14 | Timeout | Verified |
| 15 | Stop backend | Verified |
| 16 | Agent stays alive | Verified |
| 17 | Start backend | Verified |
| 18 | Agent reconnects | Verified |
| 19 | Stop Control Agent | Verified |
| 20 | Watchdog detects failure | Verified (real installed service) |
| 21 | Watchdog attempts recovery | Verified (real installed service, real `ServiceController.Start()`) |
| 22 | Control Agent becomes healthy | Verified (real installed service) |
| 23 | Reboot server | MANUAL TEST REQUIRED |
| 24 | Both services auto-start | MANUAL TEST REQUIRED |
| 25 | Agent reconnects after reboot | MANUAL TEST REQUIRED |
| 26 | Heartbeat/telemetry resume after reboot | MANUAL TEST REQUIRED |

Tests 1, 2 (clean-machine variant), 7 (cross-machine variant), and 23–26 require either a
second physical/virtual machine or an actual reboot of the host running this validation,
and were intentionally not performed automatically — rebooting or wiping the current
machine was out of scope for this pass. Everything else in this plan was executed for
real (not assumed) during this validation session, including a genuine Windows-Service
Watchdog restart of the Control Agent — both services were removed again afterward
(`sc.exe delete`) so this development machine is left in a clean state.
