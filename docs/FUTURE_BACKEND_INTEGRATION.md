# Future Backend Integration

This document describes how the Control Agent's WebSocket layer - implemented and
verified for **local testing only** - is intended to eventually connect to the real
CloudOrc backend. It also lists everything production-grade that is deliberately **not**
built yet.

## Where things actually stand

| Stage | Status |
|---|---|
| Local JSON file command source | Implemented, unchanged since the first version |
| Generic PowerShell execution engine | Implemented, unchanged - identical code path regardless of transport |
| WSS protocol (HELLO/HEARTBEAT/TELEMETRY/COMMAND/COMMAND_STATUS/COMMAND_RESULT/PING/ERROR) | **Implemented and verified locally** (`WssCommandSource`, `WssResultSink`, `BackendConnectionService`) |
| Local test backend (`tools/CloudOrc.AgentTestServer`) | **Implemented** - a dev-only stand-in, not the real backend |
| Connection to the real CloudOrc backend | **Not implemented** - nobody has pointed this at anything but `tools/CloudOrc.AgentTestServer` |
| Production security (`wss://`, enrollment, credentials, RBAC, audit) | **Not implemented** - see [What's still missing](#whats-still-missing-for-production) |

In other words: the transport-layer work described as "future" in earlier revisions of
this document is now built and working - see
[docs/BACKEND_WEBSOCKET_TESTING.md](BACKEND_WEBSOCKET_TESTING.md) for confirmed live
output of every message type. What's left is pointing it at the *real* backend instead of
the local test stand-in, and adding the security layer that a real backend requires.

## Current data flow (both modes can run simultaneously)

```
Local command JSON file (commands\*.json)          COMMAND message over ws://localhost
        |                                                        |
        v                                                        v
ICommandSource  <-- LocalFileCommandSource      ICommandSource  <-- WssCommandSource
        |                                                        |
        +-----------------------+  --------------  +-------------+
                                 v
                    ICommandQueue (System.Threading.Channels, in-process only)
                                 |
                                 v
                    IPowerShellExecutor  <-- PowerShellCommandExecutor (generic, transport-blind)
                                 |
                                 v
                    every registered ICommandResultSink (fan-out)
                                 |
                    +------------+------------+
                    v                         v
        LocalFileResultSink          WssResultSink
        (results\*.result.json)      (COMMAND_RESULT over ws://localhost)
```

`tools/CloudOrc.AgentTestServer` plays the role of "backend" above during local testing -
it is a small ASP.NET Core app with no authentication, meant to run only on the same
machine.

## What changes to reach the real backend

Perhaps surprisingly little, and it confirms the original design goal: nothing in
`ICommandQueue`, `IPowerShellExecutor`, `CommandDetectionService`, or
`CommandProcessingService` needs to change. What actually needs to change to point at a
real backend instead of the local test server:

1. **`BackendConnection.Url`** changes from `ws://localhost:5299/agent` to the real
   backend's `wss://` endpoint.
2. **`BackendConnection.DevelopmentAllowInsecureWs`** stays `false` (the safe default) -
   a `wss://` URL doesn't need it, and the agent will refuse to start with `ws://` in a
   config meant for production, which is the point.
3. **Identity/authentication** (see below) replaces the current plain
   `AgentIdentity.AgentId`/`ServerId` configuration values with something the real
   backend actually trusts.
4. **The real backend must speak the same protocol** - `HelloMessage`, `CommandMessage`,
   `CommandResultMessage`, etc. in `CloudOrc.Agent.Contracts.Protocol` - or a translation
   layer needs to sit in front of it. This is the one piece of actual coordination work
   with the backend team/codebase.

Nothing about `WssCommandSource`/`WssResultSink`/`BackendConnectionService` themselves
needs to change for this - they already implement the full protocol against a real
WebSocket server; the local test server just happens to be the WebSocket server they've
been pointed at so far.

## What's still missing for production

Unique agent identity, one-time enrollment, and agent-specific credentials are **now
implemented** - see [ENROLLMENT.md](ENROLLMENT.md) for the full design
(`CloudOrc.ControlAgent.exe enroll --token "ENR-..."`, DPAPI-encrypted persisted identity,
WebSocket bearer-credential authentication). What remains below is what a **real
production backend** still needs to add on top of the reference (dev/test-only, in-memory)
enrollment backend shipped in `tools/CloudOrc.AgentTestServer` - none of it requires
further agent-side changes.

| Concept | Where it plugs in | Notes |
|---|---|---|
| ~~Unique Agent Identity~~ | `EnrolledStateStore`/`AgentIdentityProvider` | **Implemented** - `AgentId`/`ServerId` come from the enrollment response, persisted encrypted, never regenerated |
| ~~One-time Enrollment~~ | `EnrollmentCommandLine`, run once by the installer before any service starts | **Implemented** - see [ENROLLMENT.md](ENROLLMENT.md) |
| ~~Agent-specific Credentials~~ | `Authorization: Bearer <credential>` on the WebSocket handshake | **Implemented** - additive; `HelloMessage`'s own shape is unchanged |
| A real, persistent enrollment backend | Replaces `tools/CloudOrc.AgentTestServer`'s in-memory `EnrollmentTokenStore`/`CredentialStore` | Same request/response contract (`EnrollmentRequest`/`EnrollmentResponse`) - a database-backed implementation is a drop-in replacement, no agent/installer change needed |
| TLS (`wss://`) | The connection URL itself | Already supported by `ClientWebSocket`/`BackendConnectionOptions`, and already what an enrollment response would hand back - just needs a trusted certificate on the backend side |
| Credential Rotation | A new endpoint/message the agent would call on a timer to exchange its current credential for a new one | Not implemented; today's credential is long-lived once issued (revocable, but not automatically rotated) |
| RBAC | Enforced entirely on the backend before it ever sends a `COMMAND` | The agent already validates and executes whatever a `COMMAND` message contains - it has no concept of permissions and isn't expected to |
| Audit Logs | Backend-side, built from the `COMMAND_RESULT`/`COMMAND_STATUS` stream already being sent | No agent-side change needed |
| Command Expiry | An extra field on `CommandRequest`/`CommandMessage` + a check alongside `CommandRequestValidator.Validate` | Would reject a `COMMAND` whose `createdAt` is too old, the same validation layer that already rejects malformed commands |
| Replay Protection | Backend-side (unique command ids, once-only issuance) plus the agent's existing duplicate protection (`results\`/`completed\`/`failed\` + in-memory claim, see docs/ARCHITECTURE.md) | The agent's half of this already works and was verified live |
| Live Output Streaming | `IPowerShellExecutor` would need to expose incremental output (e.g. an `IAsyncEnumerable<string>` callback) alongside the final `PowerShellExecutionOutcome` | The one place a future change would touch the execution engine itself, and only additively - today's output is captured but only delivered at completion |

## Honest scope of what "verified" means today

Every message type, reconnect behavior, and failure mode described in
[docs/BACKEND_WEBSOCKET_TESTING.md](BACKEND_WEBSOCKET_TESTING.md) was confirmed against
`tools/CloudOrc.AgentTestServer` running on the same machine, over an unauthenticated
`ws://localhost` connection. None of it has been run against a real backend, over a real
network, over `wss://`, or under any authentication scheme - because none of those things
exist for this project yet. Treat "the WebSocket layer works" and "this is ready to
connect to production" as two separate, currently-not-equal claims.
