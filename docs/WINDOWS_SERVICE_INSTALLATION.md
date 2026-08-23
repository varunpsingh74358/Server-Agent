# Windows Service Installation

This describes installing the **published executables** as Windows Services. It is a
manual, deliberate step - nothing in the build (`dotnet build`) or test
(`dotnet test`) process installs a service automatically, and none of this has any
effect on `dotnet run` console/dev mode.

All commands below use `sc.exe` (built into Windows) and must be run from an elevated
(Administrator) PowerShell or Command Prompt. Replace paths if you publish somewhere
other than the examples shown.

## 1. Publish

```powershell
cd CLOUDORCAGENT

dotnet publish src\CloudOrc.ControlAgent\CloudOrc.ControlAgent.csproj `
    -c Release -r win-x64 --self-contained false `
    -o publish\ControlAgent

dotnet publish src\CloudOrc.WatchdogAgent\CloudOrc.WatchdogAgent.csproj `
    -c Release -r win-x64 --self-contained false `
    -o publish\WatchdogAgent
```

`--self-contained false` produces a framework-dependent deployment (requires the .NET 10
runtime on the target machine); drop the runtime identifier and self-contained flags for
a portable build, or set `--self-contained true` for a fully self-contained one if the
target machine won't have .NET installed.

Verify the executables exist:

```powershell
Get-ChildItem publish\ControlAgent\CloudOrc.ControlAgent.exe
Get-ChildItem publish\WatchdogAgent\CloudOrc.WatchdogAgent.exe
```

## 2. Install as Windows Services

```powershell
sc.exe create CloudOrcControlAgent `
    binPath= "E:\CloudOrcAgent\publish\ControlAgent\CloudOrc.ControlAgent.exe" `
    DisplayName= "CloudOrc Control Agent" `
    start= auto

sc.exe create CloudOrcWatchdogAgent `
    binPath= "E:\CloudOrcAgent\publish\WatchdogAgent\CloudOrc.WatchdogAgent.exe" `
    DisplayName= "CloudOrc Watchdog Agent" `
    start= auto
```

> `sc.exe` requires a literal space after each `=` (e.g. `binPath= "..."`, not
> `binPath="..."`) - this is `sc.exe` syntax, not a typo.

Optionally add a description:

```powershell
sc.exe description CloudOrcControlAgent "Generic local PowerShell execution engine for CloudOrc."
sc.exe description CloudOrcWatchdogAgent "Monitors and recovers the CloudOrc Control Agent."
```

## 3. Required permissions

By default, `sc.exe create` without a `obj=`/`password=` runs the service as
`LocalSystem`, which has enough privilege to run arbitrary PowerShell locally and to
start/stop other services - sufficient for both agents to function. This is broad
privilege and should be narrowed for anything beyond local development:

- The Control Agent needs: read/write access to its `DataDirectory`
  (`C:\ProgramData\CloudOrc\ControlAgent` by default) and whatever permissions the
  PowerShell scripts it will actually be asked to run require (e.g. querying services,
  reading event logs). It does **not** need to be `LocalSystem` if run as a dedicated
  service account with those specific rights instead.
- The Watchdog needs: permission to query and start/stop the `CloudOrcControlAgent`
  service (`SC_MANAGER_CONNECT` + `SERVICE_QUERY_STATUS`/`SERVICE_START`/`SERVICE_STOP`
  on that service), and read access to its own `DataDirectory`
  (`C:\ProgramData\CloudOrc\WatchdogAgent`). It does not need any PowerShell execution
  rights at all, since it never runs PowerShell.

To run as a specific account instead of `LocalSystem`:

```powershell
sc.exe config CloudOrcControlAgent obj= ".\ServiceAccountName" password= "..."
```

## 4. Start / stop / status

```powershell
sc.exe start CloudOrcControlAgent
sc.exe start CloudOrcWatchdogAgent

sc.exe query CloudOrcControlAgent
sc.exe query CloudOrcWatchdogAgent

sc.exe stop CloudOrcWatchdogAgent
sc.exe stop CloudOrcControlAgent
```

(PowerShell's own `Start-Service` / `Stop-Service` / `Get-Service` cmdlets work equally
well once the services are registered, e.g. `Start-Service CloudOrcControlAgent`.)

When running as a service, both agents write logs only to
`C:\ProgramData\CloudOrc\<Agent>\logs\` - not to a console, since there isn't one.
Directories are created automatically on first start if they don't already exist, same as
in console mode.

## 5. Remove / uninstall

Stop first, then delete:

```powershell
sc.exe stop CloudOrcWatchdogAgent
sc.exe stop CloudOrcControlAgent

sc.exe delete CloudOrcWatchdogAgent
sc.exe delete CloudOrcControlAgent
```

`sc.exe delete` does not remove `C:\ProgramData\CloudOrc\...` - remove those directories
manually if you want a clean slate.

## Order of operations for testing Watchdog recovery for real

1. Publish and install both services as above.
2. `sc.exe start CloudOrcControlAgent` then `sc.exe start CloudOrcWatchdogAgent`.
3. Confirm via the Watchdog's logs that it reports the Control Agent's service status as
   `Running` and the health check as `HEALTHY`.
4. `sc.exe stop CloudOrcControlAgent` (simulating a crash/failure).
5. Watch the Watchdog's logs: consecutive failures accumulate, then at the configured
   threshold it calls `sc.exe`-equivalent start logic on `CloudOrcControlAgent` via
   `ServiceController.Start()`. Confirm with `sc.exe query CloudOrcControlAgent` that it
   is `RUNNING` again shortly after.

This full end-to-end restart path requires an elevated session and the services actually
installed - it was not exercised as part of this local-development build (which instead
verified the console/dev-mode behavior and the documented "service not installed"
fallback path); do this once on a real Windows Server/desktop before relying on it.

## Notes

- Neither service is configured with automatic Windows Service-level restart-on-failure
  (`sc.exe failure ...`) in these instructions. That is a reasonable additional layer for
  a production deployment (it's what would restart the Watchdog itself if it crashed) but
  is left out here to keep this version's specific, application-level recovery logic
  (rate limiting, backoff) the one thing under test.
- Do not add `sc.exe failure` restart policies to the Control Agent as a substitute for
  the Watchdog - the desired behavior is a **third party** (the Watchdog) verifying
  actual functional health before restarting, not just "the OS noticed the process
  exited."
