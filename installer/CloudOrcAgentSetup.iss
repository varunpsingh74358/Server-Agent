; CloudOrc Agent Setup - Inno Setup script
;
; Builds CloudOrcAgentSetup.exe: a single Windows installer EXE that deploys the
; ALREADY-PUBLISHED self-contained win-x64 Control Agent + Watchdog Agent (produced by
; scripts\package-agent.ps1 into dist\CloudOrcAgents-win-x64\...) as two Windows Services.
;
; This script does not build, publish, or modify the agents themselves - it only
; packages their existing published output. Run scripts\package-agent.ps1 first.
;
; Build locally:
;   "<path-to-ISCC.exe>" installer\CloudOrcAgentSetup.iss
;
; Build with an explicit version (matches a release tag, e.g. v1.0.0 -> 1.0.0):
;   "<path-to-ISCC.exe>" /DMyAppVersion=1.0.0 installer\CloudOrcAgentSetup.iss
;
; Output: installer\Output\CloudOrcAgentSetup.exe (see OutputDir below)

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#ifndef ControlAgentSourceDir
  #define ControlAgentSourceDir "..\dist\CloudOrcAgents-win-x64\ControlAgent"
#endif
#ifndef WatchdogAgentSourceDir
  #define WatchdogAgentSourceDir "..\dist\CloudOrcAgents-win-x64\WatchdogAgent"
#endif

#define MyAppName "CloudOrc Agent"
#define MyAppPublisher "CloudOrc"
#define ControlAgentServiceName "CloudOrcControlAgent"
#define WatchdogServiceName "CloudOrcWatchdogAgent"

[Setup]
; Fixed product GUID - a literal "{{" is required to escape Inno's own "{" constant
; delimiter; do not change this value across releases, or upgrades/uninstall identity
; will break.
AppId={{944FC679-C8D0-45F9-8B9B-5F3A1E9259AC}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Program Files (true 64-bit location, never the x86 folder) - required install location.
DefaultDirName={autopf}\CloudOrc\Agents
DefaultGroupName=CloudOrc Agent
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableReadyPage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=CloudOrcAgentSetup
UninstallDisplayIcon={app}\ControlAgent\CloudOrc.ControlAgent.exe
UninstallDisplayName={#MyAppName}
WizardStyle=modern
; No end-user configuration is asked for - both components are selected and installed
; with working defaults with zero prompts beyond install directory / component choice.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Components]
Name: "controlagent"; Description: "CloudOrc Control Agent"; Types: full custom
Name: "watchdogagent"; Description: "CloudOrc Watchdog Agent"; Types: full custom

[Types]
Name: "full"; Description: "Full installation (Control Agent + Watchdog Agent)"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Files]
Source: "{#ControlAgentSourceDir}\*"; DestDir: "{app}\ControlAgent"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: controlagent
Source: "{#WatchdogAgentSourceDir}\*"; DestDir: "{app}\WatchdogAgent"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: watchdogagent

[Code]
var
  PreservedControlAgentConfig: String;
  PreservedControlAgentDevConfig: String;
  PreservedWatchdogConfig: String;
  PreservedWatchdogDevConfig: String;
  HadPreservedControlAgentConfig: Boolean;
  HadPreservedControlAgentDevConfig: Boolean;
  HadPreservedWatchdogConfig: Boolean;
  HadPreservedWatchdogDevConfig: Boolean;

// RaiseException does NOT reliably propagate to the process exit code when called from
// CurStepChanged(ssPostInstall) under /VERYSILENT - verified empirically (it returned
// exit code 0 even after raising). Calling the Win32 ExitProcess API directly is the
// technique that actually terminates Setup.exe with a chosen exit code, confirmed by a
// standalone test (ExitProcess(17) -> process exit code 17, checked from the caller).
procedure ExitProcess(uExitCode: UINT);
external 'ExitProcess@kernel32.dll stdcall';

procedure FailInstall(Message: String; ExitCode: Integer);
begin
  if not WizardSilent() then
    MsgBox(Message, mbCriticalError, MB_OK);
  Log(Message);
  ExitProcess(ExitCode);
end;

function RunSc(Args: String): Integer;
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;
  Result := ResultCode;
end;

function ServiceExists(ServiceName: String): Boolean;
begin
  Result := (RunSc('query "' + ServiceName + '"') = 0);
end;

procedure StopServiceIfPresent(ServiceName: String);
begin
  if ServiceExists(ServiceName) then
    RunSc('stop "' + ServiceName + '"');
end;

procedure DeleteServiceIfPresent(ServiceName: String);
begin
  if ServiceExists(ServiceName) then
    RunSc('delete "' + ServiceName + '"');
end;

// Creates the service if missing, or reconfigures it in place if it already exists -
// this is what makes re-running the installer (upgrade) idempotent instead of failing
// or creating a duplicate service.
procedure InstallOrUpdateService(ServiceName, DisplayName, Description, ExePath: String);
begin
  if not ServiceExists(ServiceName) then
    RunSc('create "' + ServiceName + '" binPath= "' + ExePath + '" DisplayName= "' + DisplayName + '" start= auto')
  else
    RunSc('config "' + ServiceName + '" binPath= "' + ExePath + '" start= auto');

  RunSc('description "' + ServiceName + '" "' + Description + '"');

  // OS-level restart-on-crash safety net. Complementary to, not a replacement for, the
  // Watchdog Agent's own health-check-based recovery (which also catches an
  // unresponsive-but-still-running Control Agent - a case this cannot see).
  RunSc('failure "' + ServiceName + '" reset= 86400 actions= restart/60000/restart/60000/restart/60000');
end;

// Exec does not capture a process's stdout directly, so `sc query` is redirected to a
// temp file via cmd.exe and the STATE line is inspected - a plain "did sc.exe exit 0"
// check would only prove the service EXISTS, not that it reached RUNNING.
function IsServiceRunning(ServiceName: String): Boolean;
var
  ResultCode: Integer;
  outFile: String;
  lines: TArrayOfString;
  i: Integer;
begin
  Result := False;
  outFile := ExpandConstant('{tmp}\sc_query_out.txt');
  if Exec(ExpandConstant('{cmd}'), '/c sc.exe query "' + ServiceName + '" > "' + outFile + '"', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringsFromFile(outFile, lines) then
    begin
      for i := 0 to GetArrayLength(lines) - 1 do
      begin
        if Pos('RUNNING', lines[i]) > 0 then
        begin
          Result := True;
          Break;
        end;
      end;
    end;
  end;
  DeleteFile(outFile);
end;

function StartServiceAndVerify(ServiceName: String): Boolean;
var
  ResultCode: Integer;
  i: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'start "' + ServiceName + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Give the service a few seconds to actually reach Running before verifying, since
  // sc start returns as soon as the start request is accepted, not once it's live.
  Result := False;
  for i := 1 to 10 do
  begin
    if IsServiceRunning(ServiceName) then
    begin
      Result := True;
      Break;
    end;
    Sleep(1000);
  end;
end;

function ReadFileIfExists(FileName: String; var Content: String): Boolean;
var
  Lines: TArrayOfString;
  i: Integer;
begin
  Result := False;
  Content := '';
  if FileExists(FileName) then
  begin
    if LoadStringsFromFile(FileName, Lines) then
    begin
      for i := 0 to GetArrayLength(Lines) - 1 do
      begin
        if i > 0 then
          Content := Content + #13#10;
        Content := Content + Lines[i];
      end;
      Result := True;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  controlAgentExe, watchdogExe: String;
  controlAgentDir, watchdogDir: String;
begin
  controlAgentDir := ExpandConstant('{app}\ControlAgent');
  watchdogDir := ExpandConstant('{app}\WatchdogAgent');

  if CurStep = ssInstall then
  begin
    // Stop services BEFORE files are copied so their executables/DLLs are not locked,
    // and back up existing configuration so an upgrade doesn't silently reset it -
    // literal preserve, matching scripts\install-agent.ps1's behavior exactly.
    StopServiceIfPresent('{#WatchdogServiceName}');
    StopServiceIfPresent('{#ControlAgentServiceName}');

    HadPreservedControlAgentConfig := ReadFileIfExists(controlAgentDir + '\appsettings.json', PreservedControlAgentConfig);
    HadPreservedControlAgentDevConfig := ReadFileIfExists(controlAgentDir + '\appsettings.Development.json', PreservedControlAgentDevConfig);
    HadPreservedWatchdogConfig := ReadFileIfExists(watchdogDir + '\appsettings.json', PreservedWatchdogConfig);
    HadPreservedWatchdogDevConfig := ReadFileIfExists(watchdogDir + '\appsettings.Development.json', PreservedWatchdogDevConfig);
  end;

  if CurStep = ssPostInstall then
  begin
    // Restore preserved configuration over the freshly-deployed default files.
    if HadPreservedControlAgentConfig then
      SaveStringToFile(controlAgentDir + '\appsettings.json', PreservedControlAgentConfig, False);
    if HadPreservedControlAgentDevConfig then
      SaveStringToFile(controlAgentDir + '\appsettings.Development.json', PreservedControlAgentDevConfig, False);
    if HadPreservedWatchdogConfig then
      SaveStringToFile(watchdogDir + '\appsettings.json', PreservedWatchdogConfig, False);
    if HadPreservedWatchdogDevConfig then
      SaveStringToFile(watchdogDir + '\appsettings.Development.json', PreservedWatchdogDevConfig, False);

    controlAgentExe := controlAgentDir + '\CloudOrc.ControlAgent.exe';
    watchdogExe := watchdogDir + '\CloudOrc.WatchdogAgent.exe';

    if WizardIsComponentSelected('controlagent') then
    begin
      InstallOrUpdateService('{#ControlAgentServiceName}', 'CloudOrc Control Agent',
        'Generic local PowerShell execution engine for CloudOrc.', controlAgentExe);
      if not StartServiceAndVerify('{#ControlAgentServiceName}') then
        FailInstall('CloudOrc Control Agent service did not reach the Running state. Check C:\ProgramData\CloudOrc\ControlAgent\logs\ for details.', 10);
    end;

    if WizardIsComponentSelected('watchdogagent') then
    begin
      InstallOrUpdateService('{#WatchdogServiceName}', 'CloudOrc Watchdog Agent',
        'Monitors and recovers the CloudOrc Control Agent.', watchdogExe);
      if not StartServiceAndVerify('{#WatchdogServiceName}') then
        FailInstall('CloudOrc Watchdog Agent service did not reach the Running state. Check C:\ProgramData\CloudOrc\WatchdogAgent\logs\ for details.', 11);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    // Stop and remove both services before Inno removes the application files.
    // C:\ProgramData\CloudOrc\ (logs, command/result history, config) is deliberately
    // NEVER touched here - it was not installed by this installer and is preserved by
    // design so operators can inspect history after removal.
    StopServiceIfPresent('{#WatchdogServiceName}');
    StopServiceIfPresent('{#ControlAgentServiceName}');
    DeleteServiceIfPresent('{#WatchdogServiceName}');
    DeleteServiceIfPresent('{#ControlAgentServiceName}');
  end;
end;
