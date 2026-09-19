# Task 37 — Security Privacy Retention Deletion

## Goal

Implement defense-in-depth security, privacy routing, audit, retention, logical/physical deletion, and consent lifecycle enforcement.

## Context

Binding: JWT+RBAC, repo-level tenant scoping, RLS production hardening + session interceptor + maintenance role, app filters retained. Tenant-prefixed storage/Redis keys, ownership before URLs, message tenant validation (reject cross-tenant to error/skipped + metric). Secrets via env/manager, no hardcode/log/hash; rotation policy. mTLS service-to-service where topology requires + K8s network policies. Containers non-root/read-only-root/restricted volumes/no-egress/limits + scanning/SBOM/signing (CI task wires, hardening defined here). Audit append-only for create/delete/start/cancel/retry/review/export/consent/admin. Retention intermediate 30d/final 90d/audit 365d. Logical delete (flag + ref removal + refcount) then physical only when refs zero + retention satisfied + no hold. Deletion jobs + sweeper. Consent grant/revoke/audit enforced at assignment/cloning. Cloning default-off. Privacy in routing.

## Starting State

Auth/matrix/ownership/audit service/RLS SQL/interceptor/storage keys/consent service basics exist. No retention/deletion jobs/sweeper, no network/mTLS/container hardening docs, no rotation runbook, no RLS negative tests.

## Scope

Must implement: RLS enablement verification, tenant guards, secret policy, mTLS/network/container specs, audit wiring verification, retention/deletion services + workers, consent enforcement completion. Must not implement: CI scan/sign steps (next tasks), dashboards.

## Instructions

1. Tenant guards: add `TenantGuard.AssertMatch(callerTenant, resourceTenant)` called in every service + BaseConsumer (already validates; ensure metric `security.cross_tenant_rejected` on reject). Redis keys always `"{tenant}:{rest}"` (audit RateLimiter + progress keys). Storage keys already prefixed (verify). RLS: ensure migration applied policies; add `SET app.tenant_id` interceptor active in Api+Workers; maintenance role `maintenance` bypasses RLS for reconcilers/migrations (connection string `Maintenance__Connection`).
2. Secrets: `SecretPolicy.cs` (no `Password|Secret|Key|Token` in logs/hashes/errors; `[Secret]` redaction verified); rotation doc `docs/security/secret-rotation.md` (create): steps rotate DB/Rabbit/S3/provider keys without downtime (dual-support window), drill command. mTLS: `docs/security/mtls.md` + config `Security: { MtlsEnabled=false, CaPath, CertPath }` default false local, true prod topology (document); K8s NetworkPolicy YAML in deploy task references this spec (define required policies here: deny-all default + allow api→pg/rabbit/redis/minio, workers→same, no egress to internet except provider endpoints allowlist).
3. Containers: document hardening checklist `docs/security/container-hardening.md` (non-root, read-only root, writable /tmp only, resource limits, no egress, image scan/SBOM/sign required) — Dockerfiles already non-root; verify read-only compatible.
4. Audit: ensure all privileged ops call AuditService (project create/delete, start, cancel, retry, review decisions, export, consent grant/revoke, admin access); AuditEvent append-only (no update/delete API; DB revoke update/delete via RLS/GRANT — include SQL `REVOKE UPDATE,DELETE ON audit_events FROM app_role`).
5. Retention/deletion: `RetentionService.cs` with `RetentionOptions` (30/90/365); `RequestDeletionAsync(project)` sets IsDeleted + removes Artifact refs (mark Deleted) + updates ContentObject refcounts; `DeletionJobWorker : BaseConsumer<DeletionJobRequested?>` + `RetentionSweeper : BackgroundService` daily: query `artifacts where deleted + retention expired + refcount zero + no active hold` → delete S3 blob + mark ContentObject Deleted; respect RetentionHold (IsActive blocks). Create message `DeletionJobRequested` (add to Contracts here if missing — document addition).
6. Consent: wire Revoke to block new cloning (already checked live in Task 27; add test here for revocation enforcement + audit).

## Requirements

- R1: Cross-tenant API/storage/message denied + tested.
- R2: RLS negative tests pass.
- R3: Holds block deletion; physical only when zero refs + expired.
- R4: Cloning default-off; revocation enforced; privacy in routing.
- R5: Audit covers all listed ops, append-only.

## Edge Cases and Error Handling

- Delete with hold → 409 POLICY_DENIED + hold id in message.
- Retention not yet expired → skip, not fail.
- RLS without tenant → deny (fail closed).
- Secret missing → fail fast, never default credential.

## Security and Safety Requirements

- All above; image scan/SBOM/sign required in CI (define, implement next task).

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Security/TenantIsolationTests.cs`: `Api_CrossTenant_403`, `Storage_CrossTenant_Denied`, `Consumer_CrossTenant_Rejected`, `Redis_Keys_Scoped`, `Rls_Negative` (bypass app filter, direct SQL with wrong tenant returns 0 rows), `Hold_Blocks_Delete`, `Physical_Only_Zero_Refs`, `Revocation_Blocks_Cloning`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~TenantIsolationTests
```

## Completion Criteria

- Security/privacy/retention/deletion enforced + tested.

## Traceability

- Plan Section 26; Assumptions 76–78; Security+Privacy checklist; Error checklist holds respected.
