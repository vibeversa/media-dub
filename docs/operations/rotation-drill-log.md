# Secret Rotation Drill Log

Rotation procedure: `docs/security/secret-rotation.md` (dual-support
window — provision new credential alongside old, roll secret-manager
value, restart hosts one role at a time verifying `/health/ready`, revoke
old after one pool lifetime + one JWT lifetime). No secret material is
recorded here.

## 2026-09-17 — Rotation drill (redaction + isolation verification)

- Date: 2026-09-17 (local, UTC+03:30).
- Scope: prove that after a rotation, no secret material reaches logs,
  hashes, or error responses, and tenant isolation still holds (the two
  drill commands from `docs/security/secret-rotation.md`).
- Commands:
  - `dotnet test --filter FullyQualifiedName~SecretRedactionTests` (via
    the combined `SecretRedactionTests|TenantIsolationTests|
    ObservabilityTests` run) → Unit assembly 4/4 passed.
  - `dotnet test --filter FullyQualifiedName~TenantIsolationTests` →
    included in the Integration result: 11 passed, 3 skipped
    (live-DB cases need Docker, live in CI).
- Rotation walk-through (this host, no live secrets touched): dual-key
  JWT window (new signing key primary, previous accepted for one token
  lifetime), DB role-password overlap, broker/object-store second access
  key, provider-key roll via secret manager only (never commit `CHANGE_ME`
  values), per-role restart order api → workers with `/health/ready`
  checks, post-revoke log scan for `STORAGE_UNAVAILABLE`/auth failures.
- Live credential rotation against staging: **STAGING-GATED** — must be
  performed via the secret manager with the rotation recorded as an
  `admin.access`-adjacent audit note (audit trail is append-only).
- Overall: **PASS** (R5 rotation tier: redaction + isolation green; live
  rotation deferred to staging by repo convention).

## 2026-09-19 — Rotation drill re-verification (Task 044)

- Date: 2026-09-19 (UTC).
- Scope: re-prove the rotation tier alongside the Task 044 CI tiers: no
  secret material reaches logs, hashes, or error responses, and tenant
  isolation still holds (the two drill commands from
  `docs/security/secret-rotation.md`). No live secrets touched from this
  host; no `CHANGE_ME` values committed.
- Commands:
  - `dotnet test --filter FullyQualifiedName~SecretRedactionTests` →
    Unit assembly **PASS** 4/4.
  - `dotnet test --filter FullyQualifiedName~TenantIsolationTests` →
    Integration **PASS** 7 passed, 1 skipped (live-DB case needs Docker,
    live in CI).
- Rotation walk-through (this host): unchanged procedure — dual-key JWT
  window, DB role-password overlap, broker/object-store second access
  key, provider-key roll via secret manager only, per-role restart order
  api → workers with `/health/ready` checks, post-revoke log scan.
- Live credential rotation against staging: **STAGING-GATED** (same gate
  as above).
- Overall: **PASS** (R5 rotation tier green hermetic; live rotation
  deferred to staging by repo convention).

## 2026-09-19 — Task 045 local rotation re-verification (full stack up)

- Date: 2026-09-19 (UTC). No live secrets touched; no `CHANGE_ME` values
  committed. Compose `full` stack up (11/12) for the procedure
  walk-through (per-role restart order verified operationally by the C4
  `control` kill/start and `redis` restart in the chaos drill).
- Commands:
  - `dotnet test --filter FullyQualifiedName~SecretRedactionTests` →
    Unit **PASS** 4/4.
  - `dotnet test --filter FullyQualifiedName~TenantIsolationTests` →
    Integration **PASS** 8/8 (live-DB cases run live — Docker available).
- Rotation walk-through (this host): unchanged procedure — dual-key JWT
  window, DB role-password overlap, broker/object-store second access
  key, provider-key roll via secret manager only, per-role restart order
  api → workers with `/health/ready` checks, post-revoke log scan.
- Live credential rotation against staging: **STAGING-GATED** (same gate
  as above).
- Overall: **PASS** (R5 rotation tier green live-hermetic; live rotation
  deferred to staging by repo convention).
