# Environment-Agnostic Enrollment Architecture

This document describes the enrollment/bootstrap system that makes the Control Agent
**environment-agnostic**: one build, one installer, no hardcoded backend URL, no manual
`appsettings.json` editing, and no developer/customer IP anywhere in source, the compiled
binary, or the installer. The only server-specific input an administrator ever supplies is
a one-time enrollment token.

This is a permanent architecture change, not a demo - it fully replaces manually setting
`BackendConnection.Url`/`AgentIdentity.AgentId` in config for any enrolled agent, while
remaining 100% backward compatible with the pre-enrollment local-testing flow (running
without a token still works exactly as before - see [Backward compatibility](#backward-compatibility-un-enrolled-mode)).

## Why a token can't be *fully* zero-information

Redeeming a token requires knowing where to redeem it. There is no way around this - it
is not a workaround being avoided here, it is a hard requirement of bootstrapping. Two
honest designs exist:

1. **Encode the redemption endpoint inside the token itself** (chosen here) - the token is
   a self-contained, opaque-to-a-human string; the administrator only ever copies and
   pastes it, never reads or types a URL.
2. **A fixed, universal bootstrap host baked into the agent** (the Tailscale/GitHub Actions
   runner pattern) - the token then carries zero embedded information, but a constant
   hostname must exist somewhere in the binary.

Given this repository's explicit requirement of **zero** hardcoded hostnames anywhere in
the agent/installer, option 1 was chosen. The trade-off, stated plainly: the token is
**opaque, not encrypted** - anyone holding the token string can trivially base64-decode it
and see which URL it redeems at. The token's actual security comes entirely from the
**secret** it carries being single-use, short-lived, and validated/consumed by the backend
- never from the encoding being secret. See `EnrollmentToken.cs` for the exact format.

## Token format

```
ENR-<base64url(JSON)>
JSON = { "u": "<enrollment endpoint URL>", "s": "<single-use secret>" }
```

Example (decoded): `{"u":"https://your-backend/api/enroll","s":"<random-secret>"}`

## End-to-end flow

```
Administrator runs:
  CloudOrcAgentSetup.exe --token "ENR-..."
        |
        v
Installer deploys Control Agent + Watchdog files (unchanged from before)
        |
        v
Installer shells out to:
  CloudOrc.ControlAgent.exe enroll --token "ENR-..."
        |
        v
EnrollmentClient decodes the token locally (no network call needed to find
the endpoint) -> POSTs { secret, machineId, machineName, agentVersion }
to the decoded URL
        |
        v
Backend validates + CONSUMES the secret (single-use), issues:
  { agentId, serverId, backendUrl, credential }
        |
        v
EnrolledStateStore encrypts this (Windows DPAPI, machine scope) and
writes it to C:\ProgramData\CloudOrc\ControlAgent\enrollment.dat
        |
        v
Installer creates/starts the CloudOrcControlAgent / CloudOrcWatchdogAgent
services (only after enrollment succeeds - a failed enrollment never
leaves a running, misconfigured service behind)
        |
        v
On every startup (service start, restart, reboot), Program.cs loads
enrollment.dat BEFORE building the host, and overrides
BackendConnection.Enabled/Url and AgentIdentity.AgentId/ServerId with the
enrolled values - appsettings.json is never consulted for these once
enrolled
        |
        v
BackendConnectionService connects to the enrolled BackendUrl and presents
"Authorization: Bearer <credential>" on the WebSocket handshake - the
existing HELLO/HEARTBEAT/TELEMETRY/COMMAND/COMMAND_STATUS/COMMAND_RESULT
protocol is completely unchanged
```

## What's static vs. runtime configuration

| Static (appsettings.json, ships with the build) | Runtime (from enrollment, never in appsettings.json) |
|---|---|
| `PollIntervalSeconds`, `FileStabilityMilliseconds` | `BackendConnection.Url` |
| `WorkerHeartbeatTimeoutSeconds` | `BackendConnection.Enabled` (forced `true` once enrolled) |
| `HeartbeatIntervalSeconds`/`TelemetryIntervalSeconds` (timing only) | `AgentIdentity.AgentId` |
| `Validation.*` (script length/timeout limits) | `AgentIdentity.ServerId` |
| `LocalFileModeEnabled` | The permanent bearer credential |
| Logging/Serilog settings | |

## Components

### Agent side (`src/CloudOrc.ControlAgent/Enrollment/`)

- **`EnrollmentClient`** - decodes the token, POSTs the secret, parses the response. Never
  throws for an expected failure (bad token, unreachable endpoint, rejected secret) -
  always returns a clean `EnrollmentOutcome` so the CLI can print a message and exit
  non-zero.
- **`EnrolledStateStore`** - encrypts (`ProtectedData.Protect`, `DataProtectionScope.LocalMachine`)
  and persists `EnrolledAgentState` to `enrollment.dat` under the agent's data directory.
  Machine scope, not user scope, is deliberate: the installer may run as an interactively
  elevated administrator while the service later runs as `LocalSystem` - only
  machine-scoped DPAPI keys decrypt across that account boundary. A tampered/corrupted
  file is treated as "not enrolled" (logged, never thrown) rather than crashing the agent.
- **`EnrollmentCommandLine`** - implements `CloudOrc.ControlAgent.exe enroll --token "..."`.
  Exit code `0` = success, `2` = bad arguments, `20` = enrollment failed (bad token,
  unreachable endpoint, rejected by backend). Never writes partial state on failure.
- **`AgentIdentityProvider`** - now checks `EnrolledStateStore` first; falls back to
  `AgentIdentityOptions` (config) only when not enrolled. `AgentId`/`ServerId` are read
  from the SAME file on every call - never regenerated.
- **`Program.cs`** - the `enroll` subcommand runs before the host is built. Normal startup
  loads `EnrolledStateStore` early and overrides `BackendConnectionOptions.Enabled`/`Url`
  before any service is registered - so `BackendConnectionService`,
  `HeartbeatPublisherService`, `TelemetryPublisherService`, etc. all see the enrolled
  value with no code changes of their own.
- **`BackendConnectionService`** - sets `Authorization: Bearer <credential>` on the
  `ClientWebSocket` handshake when `identity.Credential` is present. No other protocol
  change.

### Installer (`installer/CloudOrcAgentSetup.iss`)

Parses `--token "<value>"` (also accepts `--token=<value>` / `/TOKEN=<value>`) from its own
command line, and - if present - shells out to the just-deployed
`CloudOrc.ControlAgent.exe enroll --token "..."` **before** creating/starting the Control
Agent service. A non-zero exit from that step fails the whole install (`FailInstall`, exit
code 20) - the installer never reports success on a failed enrollment, and never starts a
service with no valid backend configuration. Running the installer **without** `--token`
still performs a plain local-only install exactly as before enrollment existed - useful
for local development/testing with no backend at all.

### Reference backend (`tools/CloudOrc.AgentTestServer/Enrollment/`) - DEV/TEST ONLY

- **`EnrollmentTokenStore`** - issues tokens, validates+atomically-consumes secrets
  (thread-safe single-use, confirmed under concurrent load in
  `EnrollmentTokenStoreTests.ValidateAndConsume_ConcurrentAttemptsOnTheSameToken_ExactlyOneSucceeds`),
  tracks expiry, supports revocation before use. Secrets are stored **hashed** (SHA-256),
  never in plaintext, exactly as a real backend's database should.
- **`CredentialStore`** - issues/validates/revokes the permanent per-agent bearer
  credential, same hashed-storage discipline.
- **Endpoints**: `POST /api/enroll` (redeem), `POST /api/enrollment-tokens` (issue a test
  token), `POST /api/enrollment-tokens/revoke`, `POST /api/credentials/revoke`.
- **This is a reference shape, not a production backend.** It is in-memory (resets on
  restart - a real backend uses a database so a restart doesn't invalidate every issued
  credential), has no admin authentication of its own on the issuing endpoints, and is
  explicitly labeled DEVELOPMENT/TESTING ONLY everywhere, exactly like the rest of this
  tool. A real backend implementing the same three request/response shapes
  (`EnrollmentRequest`/`EnrollmentResponse`, plus WebSocket bearer-credential validation)
  is a drop-in replacement - nothing on the agent or installer side needs to change.

## Generating a test enrollment token

```powershell
# Terminal 1
cd tools\CloudOrc.AgentTestServer
dotnet run

# Terminal 2
Invoke-RestMethod -Uri "http://localhost:5299/api/enrollment-tokens" -Method Post -ContentType "application/json" -Body "{}"
# -> { "token": "ENR-..." }
```

To bind the token to the specific server it's meant for (recommended - see
[Security properties](#security-properties) below), pass that server's IP address as
`ExpectedIpAddress` when issuing it:

```powershell
Invoke-RestMethod -Uri "http://localhost:5299/api/enrollment-tokens" -Method Post -ContentType "application/json" `
    -Body '{ "ExpectedIpAddress": "10.20.30.40" }'
```

`POST /api/enroll` then rejects redemption from any IP other than `10.20.30.40`, even if
the correct token string is presented - the check runs against the actual TCP connection
making the redemption request (`HttpContext.Connection.RemoteIpAddress`), not anything the
agent claims about itself. Omit `ExpectedIpAddress` to keep the previous, unbound
behavior (any machine holding the token can redeem it).

Use that token with the installer or directly with the CLI:

```powershell
CloudOrcAgentSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART --token "ENR-..."
# or, on an already-installed agent, to re-enroll:
& "C:\Program Files\CloudOrc\Agents\ControlAgent\CloudOrc.ControlAgent.exe" enroll --token "ENR-..."
```

## Security properties

| Property | How it's satisfied |
|---|---|
| Enrollment token is cryptographically random | 32 bytes from `RandomNumberGenerator.GetBytes`, base64url-encoded |
| Can be bound to the target server's IP (optional) | `IssueToken(url, validFor, expectedIpAddress)` stores it; `ValidateAndConsume` rejects redemption from any other IP, checked against the redemption request's actual remote address (`HttpContext.Connection.RemoteIpAddress`), not anything the agent claims - a mismatched attempt is rejected without consuming the token. Not set by default (opt-in via `ExpectedIpAddress` on `POST /api/enrollment-tokens`) - without it, the token is unbound exactly as before this feature, and anyone holding the token string can redeem it from anywhere. |
| Short-lived | `EnrollmentTokenStore.IssueToken(url, validFor)` - default 900s in the reference server |
| Single-use | `ValidateAndConsume` marks used under a lock; a second attempt on the same secret fails, verified under concurrency |
| Revocable before use | `RevokeByToken` - fails validation afterward, verified live |
| Invalid after successful enrollment | Same "used" flag - success and revocation both permanently close a token |
| Stored hashed, never plaintext | `EnrollmentTokenStore`/`CredentialStore` key everything by `SHA256` hash |
| Never logged | Neither the agent nor the reference server logs the secret or the permanent credential anywhere - verified by grepping real log output during testing |
| Not the permanent credential | The one-time secret and the issued `Credential` are two different values; the secret cannot be used again after enrollment even if captured |
| Permanent credential: unique per agent | `CredentialStore.IssueCredential(agentId)` generates a fresh random value per call |
| Permanent credential: revocable | `CredentialStore.Revoke` - a revoked credential fails `IsValid` immediately (unit-tested: `CredentialStoreTests.Revoke_ThenIsValid_ReturnsFalse_RevokedAgentCannotAuthenticate`) |
| Permanent credential: securely stored on the agent | DPAPI-encrypted at rest (`EnrolledStateStore`), never a plaintext file |
| No plaintext secrets in source/binary/installer | Verified by repository-wide grep - see the final validation report for exact commands and results |

## Backward compatibility (un-enrolled mode)

Every pre-enrollment capability is fully preserved:

- Running the installer with **no** `--token` performs a plain local install - both
  services are created and started, `BackendConnection` stays disabled, and the agent
  behaves exactly as it did before enrollment existed (local file command source only).
- `dotnet run --BackendConnection:Enabled=true --BackendConnection:Url=ws://localhost:5299/agent --BackendConnection:DevelopmentAllowInsecureWs=true`
  (the original manual dev-testing flow from `docs/BACKEND_WEBSOCKET_TESTING.md`) still
  works unchanged - `AgentIdentityProvider` only prefers enrolled state when
  `enrollment.dat` actually exists.
- The WebSocket protocol, PowerShell execution engine, timeout handling, duplicate
  protection, Watchdog behavior, and telemetry collection are all completely untouched by
  this work - only how the agent learns *where* to connect and *how it authenticates*
  changed.

## What a real production backend still needs to add

This repository provides the full agent-side implementation and a working reference
backend shape - not a production-grade enrollment service. A real backend needs:

- A persistent database for tokens/credentials (the reference store is in-memory).
- Its own admin-facing authentication on the token-issuing endpoints (`/api/enrollment-tokens`
  has none here deliberately, since this is a local dev tool).
- `wss://` with a real, trusted TLS certificate (the agent already refuses `ws://` unless
  the URL came from enrollment or `DevelopmentAllowInsecureWs` is explicitly set - see
  `Program.cs`).
- Credential rotation on a schedule, and an audit log of enrollments/revocations.
- Rate limiting on the enrollment endpoint itself (to slow down token-guessing attempts,
  though a 32-byte random secret already makes guessing computationally infeasible).
- If deployed behind a load balancer/reverse proxy, the IP-binding check above must read
  the real client IP from a trusted forwarding header (e.g. `X-Forwarded-For`, validated
  against a known-proxy allowlist) instead of `HttpContext.Connection.RemoteIpAddress` -
  otherwise every request appears to come from the proxy's IP and the check becomes
  meaningless. This reference server has no proxy in front of it, so it doesn't need this;
  a production deployment behind one does.

None of this requires any change to the Control Agent, the Watchdog, or the installer -
the contract (`EnrollmentRequest`/`EnrollmentResponse` shapes, bearer-credential WebSocket
auth) is already the complete integration surface.
