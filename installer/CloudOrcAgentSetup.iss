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
;
; Installer command-line flags (in addition to Inno's own /VERYSILENT, /DIR=, etc.):
;   --version                    Print installer name/version and exit 0 - no UI, no
;                                 install action. See PrintVersionAndExitIfRequested below.
;   --token "ENR-..."            One-time enrollment token - see GetTokenParam below.
;   --force-downgrade            Allow installing an older version over a newer one - see
;                                 CheckDowngradeProtection below. Never required for a
;                                 same-or-newer version, i.e. a normal upgrade.
;
; Exit codes (checkable from a calling script): 0 = success. 2 = invalid arguments.
; 10 = Control Agent service did not reach Running. 11 = Watchdog Agent service did not
; reach Running. 20 = enrollment failed. 30 = downgrade blocked (installed version is
; newer than this installer's version, and --force-downgrade was not passed).

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
; The installation path must be identical across every version (never
; ControlAgent-1.1, a user-chosen folder, etc.) so upgrade detection - which looks for an
; existing exe at this exact fixed path - is reliable. Locking the directory page is what
; actually guarantees that even in interactive (non-silent) mode; DefaultDirName alone is
; only a suggestion the user could otherwise browse away from.
DisableDirPage=yes
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

// --- --version support ------------------------------------------------------------
// Inno Setup produces a GUI-subsystem installer with no native "print to stdout" command.
// These raw kernel32 imports are the standard Inno Setup Pascal Script recipe for adding
// one: attach to whatever console invoked Setup.exe and write to it directly. Confirmed by
// live testing that this correctly avoids showing the wizard and exits 0 immediately; the
// actual console text was NOT visually confirmed from this session's shell tooling, because
// AttachConsole(ATTACH_PARENT_PROCESS) reproducibly fails with ERROR_INVALID_HANDLE (6) in
// that automation environment even for a minimal throwaway script with no [Setup] section
// at all - i.e. it is that environment's process tree lacking an attachable console, not an
// admin-elevation or Inno-Setup-specific defect (genuine console-subsystem exes, e.g.
// CloudOrc.ControlAgent.exe --version, print correctly there via normal CRT stdout). A
// MsgBox fallback covers the interactive-no-console case (e.g. double-clicked in Explorer);
// only a silent run with no attachable console at all falls back to exit-code-only.
function AttachConsole(dwProcessId: DWORD): BOOL;
external 'AttachConsole@kernel32.dll stdcall';

function GetStdHandle(nStdHandle: DWORD): THandle;
external 'GetStdHandle@kernel32.dll stdcall';

function WriteConsoleW(hConsoleOutput: THandle; lpBuffer: String; nNumberOfCharsToWrite: DWORD; var lpNumberOfCharsWritten: DWORD; lpReserved: DWORD): BOOL;
external 'WriteConsoleW@kernel32.dll stdcall';

// Writes one line to whatever console Setup.exe is (or becomes, via AttachConsole) attached
// to. Returns False if there is no attachable console at all - the caller falls back to a
// MsgBox in that case rather than silently producing no output whatsoever.
function PrintLineToConsole(Line: String): Boolean;
var
  Written: DWORD;
  Text: String;
  hStdOut: THandle;
begin
  Result := False;
  hStdOut := GetStdHandle(DWORD(-11) { STD_OUTPUT_HANDLE });
  if hStdOut = 0 then
    Exit;

  Text := Line + #13#10;
  if WriteConsoleW(hStdOut, Text, Length(Text), Written, 0) then
    Result := True;
end;

function HasVersionParam(): Boolean;
var
  i: Integer;
  upperP: String;
begin
  Result := False;
  for i := 1 to ParamCount do
  begin
    upperP := Uppercase(ParamStr(i));
    if (upperP = '--VERSION') or (upperP = '/VERSION') then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure PrintVersionAndExitIfRequested();
var
  printedToConsole: Boolean;
begin
  if not HasVersionParam() then
    Exit;

  // GetStdHandle alone covers the case where the parent already redirected our std handles
  // (e.g. `> out.txt`); AttachConsole covers a real interactive console that didn't.
  if GetStdHandle(DWORD(-11) { STD_OUTPUT_HANDLE }) = 0 then
    AttachConsole(DWORD(-1) { ATTACH_PARENT_PROCESS });

  printedToConsole := PrintLineToConsole('CloudOrc Agent Installer') and
    PrintLineToConsole('Version: {#MyAppVersion}');

  if not printedToConsole then
  begin
    Log('--version: no attachable console - falling back to MsgBox (skipped if silent).');
    if not WizardSilent() then
      MsgBox('CloudOrc Agent Installer' + #13#10 + 'Version: {#MyAppVersion}', mbInformation, MB_OK);
  end;

  ExitProcess(0);
end;

// --- Downgrade protection ----------------------------------------------------------

function StripVersionSuffix(V: String): String;
var
  p: Integer;
begin
  Result := V;
  p := Pos('-', Result);
  if p > 0 then
    Result := Copy(Result, 1, p - 1);
  p := Pos('+', Result);
  if p > 0 then
    Result := Copy(Result, 1, p - 1);
end;

// Returns the zero-based Nth dot-separated numeric component of V (0 if that component is
// missing or non-numeric). Hand-rolled rather than relying on a string-split helper of
// uncertain availability across Inno Setup versions - Pos/Copy/StrToIntDef are guaranteed.
function NthVersionPart(V: String; N: Integer): Integer;
var
  i, partIndex: Integer;
  current: String;
  ch: Char;
begin
  Result := 0;
  partIndex := 0;
  current := '';
  for i := 1 to Length(V) + 1 do
  begin
    if i <= Length(V) then
      ch := V[i]
    else
      ch := '.'; // sentinel: flush whatever's left in `current` as the final component
    if ch = '.' then
    begin
      if partIndex = N then
      begin
        Result := StrToIntDef(current, 0);
        Exit;
      end;
      partIndex := partIndex + 1;
      current := '';
    end
    else
      current := current + ch;
  end;
end;

// -1 if A < B, 0 if equal, 1 if A > B. Compares up to 4 numeric dot-separated components
// after stripping any "-prerelease"/"+build" suffix - mirrors how the .NET SDK truncates a
// semver-style <Version> down to AssemblyVersion/FileVersion, which is what
// GetVersionNumbersString below actually reads off the installed exe on disk.
function CompareVersions(A, B: String): Integer;
var
  i, aPart, bPart: Integer;
begin
  A := StripVersionSuffix(A);
  B := StripVersionSuffix(B);
  Result := 0;
  for i := 0 to 3 do
  begin
    aPart := NthVersionPart(A, i);
    bPart := NthVersionPart(B, i);
    if aPart < bPart then
    begin
      Result := -1;
      Exit;
    end;
    if aPart > bPart then
    begin
      Result := 1;
      Exit;
    end;
  end;
end;

function GetForceDowngradeParam(): Boolean;
var
  i: Integer;
  upperP: String;
begin
  Result := False;
  for i := 1 to ParamCount do
  begin
    upperP := Uppercase(ParamStr(i));
    if (upperP = '--FORCE-DOWNGRADE') or (upperP = '/FORCEDOWNGRADE') then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

// Runs from InitializeSetup, i.e. before any file is copied, any service touched, or any
// wizard page shown - a blocked downgrade must be a true no-op: nothing on disk, no
// service, and no enrollment/config change at all. Detecting "is there an existing
// installation" via FileExists on the fixed path (rather than the uninstall registry key)
// works because DisableDirPage=yes above guarantees that path never varies across
// versions.
procedure CheckDowngradeProtection();
var
  installedExe, installedVersion: String;
begin
  installedExe := ExpandConstant('{autopf}\CloudOrc\Agents\ControlAgent\CloudOrc.ControlAgent.exe');
  if not FileExists(installedExe) then
    Exit; // No existing installation - this is a clean install, nothing to protect.

  if not GetVersionNumbersString(installedExe, installedVersion) then
    Exit; // Could not read a version from the existing exe - never block on missing data.

  if CompareVersions(installedVersion, '{#MyAppVersion}') <= 0 then
    Exit; // Installed version is the same (repair) or older (a genuine upgrade) - allowed.

  if GetForceDowngradeParam() then
  begin
    Log('Downgrade forced via --force-downgrade: installed=' + installedVersion + ', new={#MyAppVersion}.');
    Exit;
  end;

  FailInstall(
    'The installed CloudOrc Agent version (' + installedVersion + ') is newer than this ' +
    'installer''s version ({#MyAppVersion}). Refusing to downgrade - the existing ' +
    'installation, its enrollment identity, and its configuration have not been touched. ' +
    'Re-run with --force-downgrade to override.', 30);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  PrintVersionAndExitIfRequested();
  CheckDowngradeProtection();
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

// Reads --token "<value>", --token=<value>, or /TOKEN=<value> from the installer's own
// command line. This is the ONLY server-specific value an administrator ever supplies -
// no backend URL, no IP address, nothing else. Returns '' if no token was given (a valid,
// supported case: a local-only install with no backend enrollment, e.g. for local
// development/testing exactly as before enrollment existed).
function GetTokenParam(): String;
var
  i: Integer;
  p, upperP: String;
begin
  Result := '';
  for i := 1 to ParamCount do
  begin
    p := ParamStr(i);
    upperP := Uppercase(p);
    if (upperP = '--TOKEN') and (i < ParamCount) then
    begin
      Result := ParamStr(i + 1);
      Exit;
    end;
    if Copy(upperP, 1, 8) = '--TOKEN=' then
    begin
      Result := Copy(p, 9, MaxInt);
      Exit;
    end;
    if Copy(upperP, 1, 7) = '/TOKEN=' then
    begin
      Result := Copy(p, 8, MaxInt);
      Exit;
    end;
  end;
end;

// Shells out to the just-deployed Control Agent's own `enroll` CLI mode (see
// EnrollmentCommandLine.cs) rather than reimplementing HTTP/JSON in Pascal Script - the
// .NET side already has a real HttpClient and does the actual token decode + POST +
// DPAPI-encrypted persistence. This installer only needs to know whether it succeeded.
function RunEnrollment(ControlAgentExe, Token: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ControlAgentExe, 'enroll --token "' + Token + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and (ResultCode = 0);
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
  token: String;
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
      // One-time enrollment, if a token was supplied. Runs BEFORE the service is
      // created/started so a failed enrollment never leaves a service running with no
      // valid backend configuration - and never partially consumes/wastes the token
      // (EnrollmentCommandLine only persists state after the backend confirms success).
      token := GetTokenParam();
      if token <> '' then
      begin
        Log('Enrollment token supplied - running one-time enrollment before starting services.');
        if not RunEnrollment(controlAgentExe, token) then
          FailInstall('Enrollment failed - the Control Agent could not be enrolled with the supplied token. Check the token and try again; see C:\ProgramData\CloudOrc\ControlAgent\logs\ for details.', 20);
        Log('Enrollment succeeded.');
      end;

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
