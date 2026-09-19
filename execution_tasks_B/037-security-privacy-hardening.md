# Task 037 — Tenant Isolation, Signed URLs, CORS/CSP, Consent, Secrets Hygiene

**Required/Optional:** Required
**Complexity:** M

## Goal
Harden authz, tenant isolation, transport, and consent enforcement end-to-end with negative cross-tenant tests proving the boundaries.

## Context
All product endpoints from Tasks 006–013 must carry per-endpoint auth + tenant + project + role checks; media/downloads use short-lived signed URLs (Tasks 004, 012, 030, 033); voice preview has quota/consent gates (Tasks 010, 029). This task is the cross-cutting hardening pass — no new features, only enforcement, headers, and negative tests.

## Starting State
Tasks 006–013, 019, 029, 030, 033 done. Hardening not yet applied uniformly. Depends on Tasks 006–013.

## Scope
Included: per-endpoint checks, tenant-scoped keys/storage/URLs/telemetry/query-keys, 15-min signed URLs, CORS allowlist, strict CSP, consent gate, secrets hygiene, negative cross-tenant tests.
Excluded: new endpoints or UI (all other tasks), observability wiring (Task 038), CI gates (Task 042).

## Instructions
1. Add per-endpoint authorization in `src/DubbingPlatform.Api/Endpoints/`: every handler asserts authenticated principal + `tenant_id` match + project membership (via `ProjectMembership` role from Task 001) with least-privilege role for the operation; anonymous-allowed routes enumerated in `src/DubbingPlatform.Api/Auth/AnonymousRoutes.cs` (login/refresh/health only).
2. Enforce tenant scoping in `src/DubbingPlatform.Infrastructure/Persistence/`: RLS policies on all product tables (Tasks 001–004); storage keys prefixed `tenant/{tenantId}/...` in `src/DubbingPlatform.Infrastructure/Storage/TenantKeyBuilder.cs`; signed download/preview URLs carry tenant-bound claims and expire in 15 minutes (`src/DubbingPlatform.Api/Services/SignedUrlService.cs`).
3. Apply transport hardening in `src/DubbingPlatform.Api/Program.cs` (or `SecurityHeaders.cs`): CORS allowlist from configuration (no wildcard with credentials); strict Content-Security-Policy with no `unsafe-inline` (nonces for any inline needs); `Referrer-Policy: no-referrer`; signed URLs never logged, never cached (Cache-Control: private, no-store on issuance endpoints).
4. Implement the consent gate in `src/DubbingPlatform.Domain/Voice/ConsentGate.cs` + enforcement in Task 010/029 call paths: voice features disabled-by-default; revocation blocks all new synthesis/preview use immediately while in-flight jobs drain; consent states (`unknown/granted/revoked/expired`) visible in UI from Task 029; every gate decision audited.
5. Apply secrets hygiene: no provider/DB/storage secrets in any API response DTO (allowlist-serialize in `src/DubbingPlatform.Api/Contracts/`); no secrets in frontend bundles (`VITE_*` audit — only non-sensitive keys); telemetry scrubber (shared with Task 038) strips tokens/URLs/media/transcript before emission; query keys (Task 017) include `tenantId` so caches never cross tenants.
6. Write negative tests in `tests/DubbingPlatform.IntegrationTests/Security/TenantIsolationTests.cs`: cross-tenant read/write on every product endpoint → 403/404 (never 200, never data); tampered-tenant signed URL → rejected; expired (>15-min) URL → rejected; member of project A cannot touch project B.
7. Write `tests/DubbingPlatform.IntegrationTests/Security/ConsentTests.cs`: revoked consent blocks new preview/assignment (409/403 with `CONSENT_REQUIRED`); disabled-by-default voice without grant is blocked; revocation audit event emitted; expired grant treated as revoked.

## Requirements
- R1: Every product endpoint enforces auth + tenant + project-membership + role; anonymous set is explicitly enumerated.
- R2: Storage keys, signed URLs, telemetry, and frontend query keys are all tenant-scoped.
- R3: Signed URLs expire in 15 minutes, are never logged/cached, and carry tenant-bound claims.
- R4: CORS allowlist (no credentialed wildcard); strict CSP with no `unsafe-inline`.
- R5: Consent is disabled-by-default; revocation blocks new use immediately; states are visible.
- R6: No provider/DB/storage secrets reach frontend responses, bundles, logs, or telemetry.
- R7: Negative cross-tenant and consent tests fail closed (403/404/409, never data).

## Edge Cases and Error Handling
- Token with valid signature but unknown tenant → 401, no tenant creation side-effect.
- Membership revoked mid-session → next call 403 with `MEMBERSHIP_REVOKED`, client routes to projects list.
- Signed URL used after project archival → rejected with reason, not silent 404.
- Consent revoked while preview job runs → job drains, result discarded, user notified.
- CORS preflight from unlisted origin → rejected without leaking allowlist contents.

## Security and Safety Requirements
- Fail closed everywhere: ambiguous authz → deny; ambiguous consent → block; ambiguous tenancy → 404 without existence oracle (403 vs 404 chosen to avoid id enumeration per endpoint policy).
- Structured errors carry `code/message/correlationId` only; no stack traces, paths, or provider messages to clients.
- Audit every denied cross-tenant attempt and every consent-gate decision (actor, tenant, endpoint, outcome).

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Security/TenantIsolationTests.cs`: cross-tenant matrix over all product endpoints, tampered/expired signed URLs, project-A vs project-B separation, query-key tenant scoping (frontend unit).
- Create `tests/DubbingPlatform.IntegrationTests/Security/ConsentTests.cs`: default-disabled, revocation blocks new use, in-flight drain, audit emission, expiry-as-revoked.
- Type: backend integration (Testcontainers PostgreSQL); frontend unit for query-key scoping.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~TenantIsolationTests
dotnet test --filter FullyQualifiedName~ConsentTests
```

## Completion Criteria
- Per-endpoint authz, tenant scoping, 15-min URLs, CORS/CSP, consent gate, and secrets hygiene enforced with `TenantIsolationTests` + `ConsentTests` green and no secret leakage into frontend or logs.

## Traceability
- Plan B §13.1–§13.8, §23.4. Depends on Tasks 006–013, 019, 029, 030, 033.

## Review Fix — Scope Clarification (Completion Gate)
- **Baseline stays in feature tasks:** per-endpoint auth + tenant + project + role checks, tenant-scoped queries/keys/URLs, and consent evaluation are implemented in 006–013 / 019 / 029 / 030 / 033. This task PROVES boundaries: cross-tenant/cross-user negative tests, header sweep (CSP/CORS/signed-URL expiry), secret scan (no provider/DB/storage secrets to frontend), consent-revocation proofs. It must not be the first place authz appears.
