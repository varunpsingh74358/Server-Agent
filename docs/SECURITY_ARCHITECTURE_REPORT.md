# CloudOrc Agent — Architecture & Security Review

**Repository:** CloudOrc.WindowsAgents
**Reviewed:** 25 Aug 2026
**Basis:** full read of `docs/`, `src/`, `tools/`, `tests/`, `installer/` against the current `main` branch (working tree clean at time of review)
**Status:** pre-production (no live backend yet)

---

## Executive Summary

This project (CloudOrc Agent) is a system that runs on a Windows server, connects remotely to the company's backend to execute PowerShell commands, and reports the server's health. It has two parts — the **Control Agent** (does the actual work) and the **Watchdog Agent** (keeps it alive and restarts it if it fails).

**Security already in place** — the enrollment token is single-use, identity/credential data is secured at rest with Windows DPAPI encryption, every connection carries bearer-token authentication, unencrypted (`ws://`) connections are blocked by default, and an automated test now permanently guards against hardcoded passwords/IPs/URLs ever being committed to the code again. All of this has been verified directly in the source code, not just claimed in documentation.

**The one thing that matters most** — a real production backend does not exist yet; everything so far has only been tested against a local test server, without real TLS encryption. Also, this agent will run whatever PowerShell script it is given, with no content allow-list — meaning whoever controls the backend effectively has full administrative control over that Windows server. This is why making the real backend's authentication/authorization airtight is the single most important priority before going into production.

---

## A. System Overview — Which Flow the Code Runs In

From install to command execution, the full data flow, with code file references at each step:

1. **Install (Admin-only).** An administrator runs `CloudOrcAgentSetup.exe` on the target Windows Server, optionally with a one-time enrollment token. The installer copies both services — Control Agent and Watchdog Agent — into `Program Files\CloudOrc\Agents` and registers each as a Windows Service.
   `installer/CloudOrcAgentSetup.iss`

2. **Enrollment — the agent gets its identity.** The token is decoded locally and a one-time-use secret is sent to the backend's enrollment URL. The backend returns a permanent `agentId` plus a bearer `credential`, which is encrypted with Windows DPAPI and saved to disk.
   `Enrollment/EnrollmentCommandLine.cs`, `Identity/EnrolledStateStore.cs`

3. **Connecting to the backend (WebSocket).** On every startup, a WebSocket connection is opened to the backend using the saved credential (`Authorization: Bearer <credential>`). If the connection drops, it automatically retries.
   `Backend/BackendConnectionService.cs`

4. **Receiving & executing a command.** A `COMMAND` message from the backend carries a PowerShell script. Only structural checks are applied (length, timeout) — there is no content-based allow-list. The script runs in an isolated PowerShell session.
   `Services/PowerShellCommandExecutor.cs`, `Commands/CommandRequestValidator.cs`

5. **Reporting the result.** Output, error text, and exit code are sent back to the backend as a `COMMAND_RESULT` message, and also written to a local file.
   `Services/CommandProcessingService.cs`

6. **Health & auto-recovery.** The Control Agent publishes its health on a local-only Named Pipe (never over the network). The Watchdog Agent polls it, and restarts the Control Agent service if it looks unhealthy — with rate-limiting so it cannot restart-loop endlessly.
   `Health/HealthPipeServer.cs`, `WatchdogAgent/Recovery/RecoveryRateLimiter.cs`

---

## B. Security Measures Already Implemented

All verified directly in source code, not taken from documentation claims.

| # | Control | Detail | File(s) |
|---|---------|--------|---------|
| 1 | One-time, hashed enrollment token | 32-byte random token; only its SHA-256 hash is stored server-side; single-use; expires after 900s. Verified safe under a concurrent-redemption race-condition test. | `Enrollment/EnrollmentTokenStore.cs` |
| 2 | Per-agent unique, revocable credential | Each agent gets its own random credential; only a hash is stored; revocation takes effect immediately. | `Enrollment/CredentialStore.cs` |
| 3 | DPAPI-encrypted identity storage | Credential/identity data is encrypted at rest with Windows DPAPI (machine scope). A corrupted/tampered file never crashes the agent — treated safely as "not enrolled." | `Identity/EnrolledStateStore.cs` |
| 4 | Bearer-token auth on every connection | Every WebSocket handshake attaches an Authorization header. The credential is never logged — explicitly protected by a field-level comment in code. | `Backend/BackendConnectionService.cs`, `Identity/AgentIdentity.cs` |
| 5 | Insecure ws:// blocked by default | Agent refuses to start against an unencrypted `ws://` URL unless a dev-mode flag is explicitly turned on. | `Program.cs`, `appsettings.json` |
| 6 | Health check never exposed to the network | A local Named Pipe is used — only the Watchdog on the same machine can talk to it, no network socket. | `Health/HealthPipeServer.cs` |
| 7 | Automated hardcoded-secret audit (CI gate) | Six automated tests permanently block hardcoded passwords/secrets/API keys, private IPs, WinRM/RDP references, and non-placeholder ws/wss URLs — run on every build. | `tests/StaticSourceAuditTests.cs` |
| 8 | `.gitignore` bug fixed | A blanket `*credential*`/`*secrets*` pattern was silently untracking real source files from git — narrowed to specific filenames and fixed. | `.gitignore` |
| 9 | Installer checksum verification | Downloaded installer's SHA256 is verified before it runs; mismatch or missing checksum stops the install. | `install-cloudorc.ps1` |
| 10 | Watchdog has limited attack surface | Watchdog never runs PowerShell or arbitrary commands — only talks to the Windows Service Control Manager API. | `WatchdogAgent/ControlAgentServiceManager.cs` |
| 11 | Elevation-hang bug fixed | No repeat UAC prompt when the session is already elevated — previously caused an indefinite hang on a real server. | `install-cloudorc.ps1` |
| 12 | Categorized connection diagnostics | DNS failure, connection refused, TLS error, and auth failure (401/403) are each detected separately, giving a clear operator signal. | `Backend/ConnectionFailureClassifier.cs` |

---

## C. Gaps & Risks — Where Security Is Still Weak

Sorted by severity — this is where management should focus attention.

### High
- **No real production backend exists yet.** Everything so far has only been tested against a local test server (`tools/CloudOrc.AgentTestServer`), without a real TLS certificate. The code supports `wss://`, but it has never been verified against a real network or certificate.
  *Refs: `docs/FUTURE_BACKEND_INTEGRATION.md`, `STATUS_REPORT.md §5`*

- **No content allow-list — full admin-level PowerShell execution.** Whatever script the backend sends runs with no restriction (only length/timeout are checked). The service runs as the `LocalSystem` account, meaning backend control equals full admin control of the server. This is an intentional design choice, but the real security boundary is entirely the backend's authentication — no second line of defense inside the agent.
  *Refs: `Commands/CommandRequestValidator.cs`, `Services/PowerShellCommandExecutor.cs`*

### Medium-High
- **No RBAC or audit log.** No role-based control over "who is allowed to run what" — entirely deferred to the future backend.
  *Ref: `docs/FUTURE_BACKEND_INTEGRATION.md`*

### Medium
- **Credential is never rotated automatically.** The permanent bearer credential issued at enrollment never expires on its own — only manual revocation. A leaked credential stays valid until noticed.
- **Enrollment token visible as a command-line argument.** The installer passes the token as a plaintext command-line argument — visible via process-list inspection (`Get-CimInstance Win32_Process`) or Windows Event Log 4688 if enabled. Impact limited since the token is single-use/short-lived, but it's an avoidable exposure.
  *Ref: `installer/CloudOrcAgentSetup.iss:472`*
- **Reference test-server's enrollment API has no authentication.** Documented as "DEV/TEST ONLY" — anyone on loopback can mint a token. Loopback-only binding is enforced by default, but risky if mistaken for a production backend.
  *Ref: `tools/CloudOrc.AgentTestServer`*

### Low-Medium
- **Script output/error content is not sanitized.** If a command script prints a password or secret, it lands in plaintext in logs and result files — no redaction.
  *Ref: `Services/CommandProcessingService.cs`*

### Low
- **Tight retry loop when the health pipe is busy.** Two Control Agent processes contending for the same pipe retry with no delay — produced gigabyte-sized log files within seconds during testing.
  *Ref: `Health/HealthPipeServer.cs`*

### Informational
- **The enrollment token itself isn't encrypted, only opaque.** Deliberate trade-off — the token only contains the backend URL (readable); the actual secret is separately protected. Not a real risk.

---

## D. Action Plan — By Priority

### P0 — Before enrolling any real/production server
1. Stand up a real, database-backed enrollment backend with its own admin authentication (today's test server is reference-only).
2. Configure a real, trusted TLS certificate, move every deployment to `wss://`, and confirm `DevelopmentAllowInsecureWs` stays false everywhere in production.
3. Decide and document the authorization model for "who is allowed to send a command" on the backend — since the agent has no restriction of its own, the backend is the entire security boundary.

### P1 — Shortly after go-live
4. Implement scheduled credential rotation rather than relying solely on manual revocation.
5. Add RBAC and audit logging on the backend side — no agent-side change needed, built from the existing COMMAND_RESULT stream.
6. Reduce the enrollment token's command-line exposure — pass via a short-lived temp file or stdin, delete right after use.
7. Add a short delay to HealthPipeServer's busy-pipe retry, to avoid a disk-full incident.

### P2 — Not urgent, but worth doing
8. Add command expiry and backend-side replay protection (reject commands that are too old).
9. Perform a genuine OS reboot test on a real server (so far only proxied via service stop/start).
10. Decide whether script output/error should ever be redacted or scrubbed — a policy decision, not necessarily a code change.

---

*This report reflects the state of the codebase as of the commit at time of review (working tree clean, most recent commit: "Align COMMAND protocol with commandType/correlationId/parameters, add exit code, and version-command support"). File paths are relative to the repository root unless noted.*
