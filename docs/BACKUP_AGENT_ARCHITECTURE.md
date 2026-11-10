# Backup capability in the CloudOrc Windows Agent

This document describes the agent-side implementation of **CO-DEV-BKP-001 (Backup Phase 1, v1.0 PROPOSED)**. It covers:

- what was built and where it lives;
- the exact contract the backend must implement;
- what still has to be proven in the M0 PoC.

## 1. One agent, same connection

The guide (§0) requires that backup extends the existing agent. There is no second agent. Enrollment and the `/agent` WebSocket are unchanged, and the following are added on top of them:

| Unchanged | Added |
|---|---|
| `enroll --token "ENR-..."`, DPAPI `enrollment.dat` | `enroll --code ABCD-1234 [--api-base https://...]` (§4 external / backup-only devices) |
| `wss://…/agent`, `Authorization: Bearer` | HELLO now carries `protocolVersion: "2"`, `capabilities`, `engines` |
| COMMAND / COMMAND_STATUS / COMMAND_RESULT | Typed commands `backup.*`, `restore.execute`, `agent.update`, `agent.credential.renew` |
| `powershell-exec` | Gated by `ControlAgent:PowerShellExecEnabled`. It defaults to `true` only because provisioning still depends on it (§13 "no big-bang cutover"). |
| — | HTTPS control-plane calls (§12) using the same credential |

Backup bytes never pass through the backend. They flow agent → storage provider directly (§3, §8).

## 2. Process and module layout (§3, §13, §14)

```
{app}\ControlAgent\   Agent Core (Windows service, LocalSystem)
  Transport\          renewable credential, HTTPS client, retry policy (401/403/429/503, Retry-After),
                      device key (non-exportable CNG P-256), session renewal, mTLS cert hook
  Commands\           typed command dispatcher (allow-list, version check, durable inbox) + handlers
  DurableQueue\       SQLite (WAL, synchronous=FULL, ACL SYSTEM+Admins): inbox, outbox, jobs,
                      checkpoints, policies, schedule state; outbox dispatcher; retention/caps
  ResourceGovernor\   Eco/Balanced/Throughput profiles, Windows Job Object (CPU hard cap, job memory,
                      priority, per-volume disk bandwidth), pressure monitor, adaptive throttle
  Backup\             policy validator, scheduler (stable offset + catch-up spread), job coordinator,
                      job runner (admission → lease → grant → worker → outcome), event recorder
  LocalStatus\        named pipe for the tray: status + activate/retry (local only)
  Updates\            agent.update handler + updater result reporting
{app}\BackupWorker\   isolated worker process (one per job, inside the Job Object)
  Engines\            Kopia + Restic adapters, pinned-hash verifier, process runner (secrets env-only)
  Storage\            Google Drive (rclone + loopback token broker), S3-compatible (Wasabi/B2/S3/own DC)
  Consistency\        VSS feasibility, copy-on-write headroom, snapshot lifetime, Tally detection
  Scanning\           size estimate + gzip NDJSON manifest for the portal file explorer
  Restore\            restore path guard (traversal, links/junctions, system folders) + audit
{app}\Engines\        kopia.exe / restic.exe / rclone.exe + engines.manifest.json (SHA-256 pins)
{app}\Updater\        signed self-updater (hash + Authenticode, backup, install, health check, rollback)
{app}\Tray\           optional status icon: Not activated / Connecting / Connected / Problem only
```

## 3. Job lifecycle

1. `backup.policy.apply` stores the backend-resolved effective policy. The agent never does inheritance itself.
2. The local scheduler creates a durable QUEUED job when `slot + stableOffset(agent, policy)` is reached.
   - A missed slot is caught up once, at a random point within `catchUpSpreadMinutes`.
   - A newer slot supersedes an older run that is still waiting.
3. The coordinator runs one job at a time and calls `POST /api/v1/backup/admission/request`.
   - **GRANTED**: the lease is stored. The job moves to SNAPSHOTTING and a `backup.started` event is emitted.
   - **DEFERRED**: the job waits until `not_before + random(jitter_window_seconds)`. No attempt is consumed.
   - **DENIED** or 403: the job goes to FAILED.
   - Network error, 429 or 5xx: the job is deferred, honouring `Retry-After`.
4. The agent calls `POST /api/v1/storage/grants` for a short-lived, repository- and operation-scoped grant. The grant is held in memory only.
5. The Resource Governor computes the budget (profile × lease). The worker starts inside a Job Object, and the spec plus grant are sent over stdin only.
6. The worker reports stages SNAPSHOTTING → SCANNING → TRANSFERRING → VERIFYING. Meanwhile the agent:
   - renews the lease at TTL/2; a renewal refused with 4xx means the lease is lost and the job stops;
   - renews the grant on the worker's request, or 10 minutes before expiry.
7. The outcome is SUCCEEDED, PARTIAL (read errors, crash-consistent Tally, failed verification, missing source), FAILED, or RETRYING (transient, bounded full-jitter backoff, `maxAttempts`). The agent never claims more than it proved (§6, §15).
8. Every state change and its event are written in one SQLite transaction. The outbox delivers them in order, at-least-once.

After a crash, a service stop, an upgrade or a reboot, any interrupted job goes to RETRYING with reason `AGENT_RESTARTED`, and the engine's own resume handles the data (§8, §15). A graceful stop never marks a job CANCELLED; only an operator's `backup.cancel` does.

Other recovery rules:
- **Per-source checkpoints:** each source's completed snapshot is checkpointed durably, and a retry skips those sources.
- **Time limits:** each job has a ceiling of `MaxJobHours` (24 h by default). `JOB_TIMEOUT` and `SNAPSHOT_LIFETIME_EXCEEDED` are retried, and the retry resumes rather than starting from scratch.
- **Updates wait:** `agent.update` is refused with `UPDATE_DEFERRED_JOB_RUNNING` while a job is running, and the backend re-sends it later.

## 4. Backend contract the agent expects

Any field marked **(agent addition)** is not in §12 of the guide. It must be added to the backend and to the next Infrastructure revision.

### HTTPS (Bearer = the agent's session credential; JSON is snake_case)

| Route | Request | Success response |
|---|---|---|
| `POST api/v1/agent/enroll` | `{invitation_code, endpoint_binding{machine_id, machine_name, installation_id, os_version, device_public_key, csr_pem}, agent_version}` | Same as the existing enroll: `{agentId, serverId, backendUrl, credential, apiBaseUrl?, portalUrl?, credentialExpiresAt?, clientCertificatePem?}`. Errors: 400/404 = unknown code, 410 = expired, 409 = used/duplicate, 429 = too many attempts, 403 = entitlement. |
| `POST api/v1/agent/session/renew` | `{endpoint_id, nonce, timestamp, proof}`. The proof is a base64 DER ECDSA-SHA256 signature over `cloudorc-session-renew|v1|{endpoint_id}|{nonce}|{timestamp:yyyy-MM-ddTHH:mm:ssZ}`, verified with the stored `device_public_key`. Reject reused nonces and stale timestamps. | `{credential, expires_at?}` |
| `POST api/v1/agent/certificate/renew` **(agent addition)** | `{endpoint_id, csr_pem, current_thumbprint, nonce, timestamp, proof}`. The proof is a device-key signature over `cloudorc-cert-renew|v1|{endpoint_id}|{nonce}|{timestamp}|{sha256(csr_pem) hex lowercase}`. | `{client_certificate_pem}`. The agent calls this when 2/3 of the certificate's lifetime has passed or fewer than 14 days remain, and on `agent.certificate.renew`. |
| `POST api/v1/backup/admission/request` | `{job_id, endpoint_id, policy_version, estimated_bytes, idempotency_key, operation, repository_id, resource_profile, schema_version}` | `{decision: GRANTED, lease{lease_id, expires_at, fencing_token, max_parallel?, max_upload_mbps?}}`, or `{decision: DEFERRED, not_before, jitter_window_seconds, reason}`, or `{decision: DENIED, reason}` |
| `POST api/v1/backup/admission/{lease_id}/renew` **(agent addition)** | `{job_id, fencing_token}` | A lease object. Return 4xx when the lease is gone or fenced. |
| `POST api/v1/backup/admission/{lease_id}/release` **(agent addition)** | `{job_id, fencing_token}` | 2xx |
| `POST api/v1/storage/grants` | `{repository_id, operation, job_id, lease_id, previous_grant_id?}` | `{grant_id, provider: "gdrive" or "s3", expires_at, repository{repository_id, engine, path, password, create_if_missing, snapshot_host?, snapshot_user?}, gdrive{access_token, token_expires_at, root_folder_id, scope}` **or** `s3{endpoint, region, bucket, prefix, access_key_id, secret_access_key, session_token}, capabilities{}}` |
| `POST api/v1/backup/jobs/{id}/events` | A `BackupJobEvent` (see below) | 2xx. Return 409 if the `event_id` was already stored. |
| `POST api/v1/backup/restores/{id}/events` **(agent addition)** | A `BackupJobEvent` | 2xx / 409 |
| `POST api/v1/backup/jobs/{id}/manifest` **(agent addition)** | Body is gzip NDJSON (`Content-Encoding: gzip`, `application/x-ndjson`). Each line is `{root, path (relative, /), type: file, dir or link, size, mtime}`. | 2xx / 409 |
| `POST api/v1/agent/events` **(agent addition)** | `{event_id, event_type: "agent.update.result", endpoint_id, occurred_at, data{update_id, outcome, from_version, to_version, ...}}` | 2xx / 409 |

**`BackupJobEvent` fields:**
- `event_id`, `event_type`, `schema_version` ("1.0"), `correlation_id`, `job_id`, `job_kind`, `endpoint_id`
- `sequence` (strictly increasing per job; gaps are allowed), `stage`, `occurred_at`
- `policy_version`, `policy_effective_hash`, `repository_id`
- `lease_id`, `fencing_token` (reject a stale token), `attempt`, `reason_code`, `message` (already redacted)
- `progress{...}`
- `resource{profile, throttle_level, worker_working_set_bytes, worker_peak_memory_bytes, worker_cpu_percent, system_cpu_percent, disk_latency_ms, upload_bytes_per_second, scanner_duration_ms, cache_size_bytes, protected_app_active, agent_core_working_set_bytes, maintenance}`. These are the §7 acceptance metrics.
- `result{engine, engine_version, engine_snapshot_ids[{source_path, snapshot_id, root_object_id, files, bytes, errors}], consistency, verification_state, ...}`

Events never contain secrets.

Outbox delivery rules: 2xx or 409 means delivered. 401, 408, 429 and 5xx are retried with backoff and `Retry-After`. 400, 403, 404, 413 and 422 park the event as dead for 7 days, so a single bad event does not block the queue.

### Typed commands over the existing WebSocket (camelCase)

```json
{ "type": "COMMAND", "commandId": "…", "correlationId": "…", "commandType": "backup.policy.apply",
  "commandVersion": "1.0", "actor": "user@tenant", "parameters": { … } }
```

| commandType | parameters |
|---|---|
| `backup.policy.apply` | `{policy: BackupPolicyDocument}` (see `Contracts/Backup/BackupPolicyDocument.cs`) |
| `backup.policy.remove` | `{policyId}` |
| `backup.run` | `{jobId, policyId, reason}` (the `jobId` is the idempotency key) |
| `backup.cancel` | `{jobId}` |
| `backup.verify` | `{jobId, repositoryId, engine, engineSnapshotId, verifyFilesPercent}` |
| `backup.maintenance` | `{jobId, repositoryId, engine, policyId?, full}` |
| `restore.execute` | `{restoreJobId, recoveryPointId, repositoryId, engine, engineSnapshotId, sourceRoot, selection[], targetKind: original or alternate, targetPath, mode: overwrite, rename or skip-existing, approvedBy}`. Send this only after approval (§5, §10). |
| `agent.update` | `{updateId, version, packageUrl (https), sha256, sizeBytes, cohort}` |
| `agent.credential.renew` | `{}` |
| `agent.certificate.renew` | `{}` (rotates the mTLS client certificate now) |

**Signed commands (§10).** When the enroll response carries `commandSigningPublicKey` (base64 SPKI, ECDSA P-256), every typed command MUST include:
- `issuedAt` (accepted range: 15 minutes in the past to 5 minutes in the future);
- `signature`: base64 DER ECDSA-SHA256 over `cloudorc-command|v1|{commandId}|{commandType}|{commandVersion}|{issuedAt:yyyy-MM-ddTHH:mm:ssZ}|{sha256 hex lowercase of the exact parameters JSON text sent}`.

Unsigned or invalid commands are rejected (`SIGNATURE_MISSING`, `SIGNATURE_INVALID`, `SIGNATURE_EXPIRED`) before they reach the inbox. `ControlAgent:RequireSignedTypedCommands` makes signatures mandatory even without a key.

How the agent answers:
- It first sends COMMAND_STATUS RUNNING, then COMMAND_RESULT. The result's `output[0]` is `{accepted, jobId?, reasonCode?, message?}`.
- The answer only means the work was accepted. Progress then arrives through job events.
- If the same `commandId` is redelivered, the agent replays the stored result and does not run the command again.

## 5. Security properties (§10)

- **Secrets:** grants and repository passwords never appear in logs, events or command lines. They are passed in memory, on the worker's stdin, and in engine environment variables. `SecretRedactor` masks anything that slips through.
  - **Exception:** Kopia saves the S3 credentials it connected with inside its per-job config file (`{data}ackup\work\{jobId}\`). That file is protected by the data-directory ACL (SYSTEM + Administrators only). It is deleted when the job ends, and leftovers are deleted at the next start after a crash.
  - When Core renews the grant, Kopia reconnects before its next command, so it never keeps using expired keys.
- **Data directory ACL:** at startup the whole `C:\ProgramData\CloudOrc\ControlAgent` tree is restricted to SYSTEM + Administrators, with no inheritance. This covers command files, work folders, caches, manifests, state and logs.
  - Local command files are only executed when the `commands` folder is writable by privileged principals alone. Otherwise any local user could have run PowerShell as SYSTEM.
  - `powershell-exec` is gated by `PowerShellExecEnabled` on both the WebSocket path and the local-file path.
- **Google Drive:** the refresh token stays on the server. rclone gets a dummy refresh token, and its `token_url` points at a loopback broker inside the worker. The broker is bound to 127.0.0.1, uses a random path and port, and requires a per-job client secret. It hands out the grant's ~1 h access token and asks Core for a new grant when that token is about to expire.
- **Engine binaries:** each binary is pinned by SHA-256 in `engines.manifest.json` and verified before every launch. Verification fails closed.
- **Restore safety:** selections must be relative with no `..`. No link or junction may appear on the target path. Alternate targets inside Windows, Program Files or ProgramData are refused. `rename` writes to a new folder. A post-restore audit flags any link that points outside the target.
- **Tray:** the pipe is local only (the NETWORK SID is denied). Activation is accepted only while the device is not activated, or when its authorization has expired or been revoked.

## 5a. Other behaviours added after verification

- **Deep link (§4):** the portal can offer `cloudorc://activate?code=ABCD-1234`, registered by the installer's tray component. The tray always asks the user to approve before it activates. Only a single `code` parameter is accepted, and the service refuses the request if the device is already activated.
- **Tally `wait-for-close` mode (§6, ADR-05 option):** the worker waits up to `maxWaitMinutes` for Tally to close before snapshotting.
  - If Tally closes in time, the recovery point is application-consistent.
  - If it is still running, the recovery point is crash-consistent and reported as PARTIAL.
- **Agent Core budget (§7 parent budget):** `CoreBudgetMonitor` measures Core's own working set, private bytes and CPU.
  - When no job is running, it compacts and trims Core's memory. Measured on a Release build: idle working set 25–28 MB against a target of ≤ 50 MB, private ≈ 23 MB.
  - It flags breaches but never kills Core.
- **Disk bandwidth caps:** on Windows Server 2019 (build 17763), `SetIoRateControlInformationJobObject` fails with `ERROR_INVALID_FUNCTION`, i.e. per-job disk caps are unavailable there. This was verified on a real host. The governor detects this and slows reads through the CPU cap instead, so reads are still throttled before upload.
- **Code signing:** `scripts/sign-artifacts.ps1` signs CloudOrc's own binaries and the installer in CI. Signing is mandatory on `v*` tags.
- **Signed parameter text (backend integration):** the CloudOrc backend stores commands in `jsonb`, which reorders keys. It therefore sends the exact signed text as the string `parametersJson`.
  - When `parametersJson` is present, the verifier hashes it, and `TypedCommandContext.Parameters<T>()` deserializes it, so the agent executes exactly what was verified.
  - The `parameters` object is used only when `parametersJson` is absent.
- **WebSocket verdicts after HELLO:** the backend authenticates on HELLO, not on the HTTP upgrade. A refusal is a `HELLO_NACK` message followed by a close code, which the agent maps onto the same retry policy as HTTP statuses:
  - `4401` is treated as 401: renew the session, then mark it Expired.
  - `4403` is treated as 403: mark it Revoked, then stop reconnecting.
  - `1013` is treated as 503: honour `reconnectAfterMs`.
  - `GOING_AWAY` with `reconnectAfterMs` sets the delay before the next reconnect.
  - Retry counters reset only once the backend has accepted HELLO, meaning the first regular message has arrived, so repeated refusals still escalate.
- **USN Journal (§6, evaluate):** not used in Phase 1. Kopia and Restic find changed files themselves, using metadata and content-defined chunking. A USN-journal change feed is only worth evaluating if ADR-01 measurements show that scan time dominates on very large volumes.

## 6. Open items: M0 PoC and ADRs

| Item | Why |
|---|---|
| ADR-01 Kopia vs Restic | Run the benchmark on 2 vCPU / 4 GB with Tally, 1–5 TB data, and a 50 GB modified file. Both adapters are ready. |
| Pin engine versions | Fill `engines/engines.lock.json` (url + sha256), then run `scripts/fetch-engines.ps1`. |
| Verify against the pinned binaries | 1. That the rclone child process started by Kopia inherits Kopia's environment. 2. Kopia's progress-line format. 3. That the rclone `token_url` broker flow works with drive.file across a job longer than 1 h. 4. `kopia maintenance set --owner=me`. |
| ADR-02 storage | Phase 1 uses customer Google Drive (rclone). Managed DR on Wasabi is only an `s3` grant, so no agent change is needed. Never give an agent a token for a shared admin Drive: that would break the cross-tenant boundary. |
| ADR-04 offline grace | Default 0. Even with a grace period, a job still needs an unexpired grant. |
| ADR-05 Tally procedure | Three modes exist:<br>• `require-closed` (default): if Tally is running, the result is CRASH_CONSISTENT and reported as PARTIAL. The job only fails when `onInconsistent = fail`.<br>• `wait-for-close`<br>• `crash-consistent-ok`<br>ADR-05 must pick the default, and confirm with a restore drill whether a Tally-specific export or quiesce step is also needed. |
| S3 grant lifetime | A single engine command, such as one large snapshot, cannot switch credentials mid-run. An S3 grant must therefore live at least as long as the longest single snapshot (cap: `maxSnapshotLifetimeMinutes`, 720 by default). Google Drive is not affected, because its token broker renews tokens live. |
| Resource numbers | The Eco, Balanced and Throughput limits are starting values. Confirm them with the §7 measurements reported in `resource{}`. |
| Code signing | The CI step exists. Add the `CODE_SIGNING_PFX_BASE64` and `CODE_SIGNING_PFX_PASSWORD` secrets (an EV/OV certificate). |
| Real engine run | Kopia 0.23.1, Restic 0.19.1 and rclone 1.76.0 matched their official checksums, but running them on the dev host was not permitted in the session. The first PoC task is: backup, verify, restore and maintenance against S3 (`rclone serve s3` works as a local target) and against Google Drive. |
