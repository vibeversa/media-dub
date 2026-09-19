# Task 006 — Auth, Session, Me, Preferences API

## Goal
Expose login/refresh/logout, GET /me, and GET|PUT /me/preferences with permission-string UX hints.

## Context
Frontend shell cannot render navigation, guards, or locale before resolving who the user is, which tenant/roles they hold, and their persisted preferences. Auth tokens stay in memory (Task 019); the backend owns session issuance, permission strings (UX hints only — every endpoint re-authorizes), and preference validation against the Task 001 whitelist.

## Starting State
Task 001 done (TenantUser, UserPreference, ProjectMembership exist). Plan A auth issuance (external subject) exists. No `/api/v1/auth/*`, `/me`, or preferences endpoints.

## Scope
Included: `POST /api/v1/auth/login|refresh|logout`, `GET /api/v1/me`, `GET|PUT /api/v1/me/preferences`, permission-string contract, OpenAPI updates for these routes, audit on login/logout.
Excluded: project membership management UI logic, frontend auth UX (Task 019), settings screens (Task 035), admin role assignment.

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/AuthController.cs`: `POST /api/v1/auth/login` (validates external credential → issues access+refresh pair, writes AuditEvent), `POST /api/v1/auth/refresh` (rotates refresh token, rejects reuse with 401 `TOKEN_REUSED`), `POST /api/v1/auth/logout` (revokes refresh token, idempotent — unknown token still 200).
2. Create `src/DubbingPlatform.Api/Controllers/MeController.cs`: `GET /api/v1/me` returns `{ user, tenant, roles, permissions[], locale, featureFlags, session: { issuedAt, expiresAt } }`; `GET|PUT /api/v1/me/preferences` reads/writes Task 001 whitelist keys only (unknown key → 400 `PREFERENCE_KEY_UNKNOWN`; oversize ValueJson → 413).
3. Define permission strings in `src/DubbingPlatform.Application/Authorization/Permissions.cs`: `project.view`, `project.edit`, `project.delete`, `processing.start`, `processing.cancel`, `processing.retry`, `review.view`, `review.resolve`, `export.create`, `export.download`, `admin.manage`, `diagnostics.view`. Document in XML comment: UX hints only, never a security boundary.
4. Implement `IPermissionResolver` in `src/DubbingPlatform.Application/Authorization/PermissionResolver.cs`: resolves permissions from TenantUser status + ProjectMembership roles (Disabled user → zero permissions); cache per-request only (no cross-request cache).
5. Update OpenAPI (`src/DubbingPlatform.Api/OpenApi/` or Swashbuckle annotations): schemas for login/refresh/me/preferences, 401/403/validation error examples, idempotency not required on these routes (document why).
6. Wire rate limiting on login (5/min/IP) + refresh (30/min/user) via existing ASP.NET Core rate-limiter config in `src/DubbingPlatform.Api/Program.cs`.

## Requirements
- R1: `GET /me` returns tenant, roles, full permission list, locale, flags, and session expiry in one call (no second round-trip).
- R2: Disabled user gets 403 `USER_DISABLED` on `/me` and zero permissions.
- R3: `PUT /me/preferences` rejects unknown keys and oversize values; valid keys round-trip exactly.
- R4: Refresh-token reuse is rejected and the whole token family revoked (theft detection).
- R5: Logout with unknown/expired token still returns 200 (idempotent).
- R6: Permission strings match exactly the 12 defined names (contract test asserts set equality).

## Edge Cases and Error Handling
- Bad credentials → 401 `INVALID_CREDENTIALS` (no user-enumeration detail).
- Expired refresh → 401 `TOKEN_EXPIRED`; frontend redirects to login (Task 019 consumes this code).
- Concurrent preference writes → last-writer-wins per key (documented; no 409 here — 409 lives in Tasks 003/009).
- Missing tenant claim → 401 `TENANT_REQUIRED`.

## Security and Safety Requirements
- Tenant isolation: `/me` resolves only the caller's tenant; no `?tenantId` override accepted.
- Refresh tokens hashed at rest (SHA-256 + salt), never logged; access tokens short-lived.
- Permission strings are hints: every controller still calls authorization handlers (test asserts 403 when hint present but server policy denies).
- Login/logout audited with correlationId; no passwords/secrets in logs.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Auth/MePreferencesTests.cs`: login→me→preferences round-trip, unknown-key 400, disabled-user 403, refresh rotation + reuse rejection, logout idempotency, permission-set contract, cross-tenant isolation, rate-limit 429 on login burst (relaxed in CI via options).
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~MePreferencesTests
```

## Completion Criteria
- Auth/me/preferences endpoints + permission contract + OpenAPI exist; `MePreferencesTests` pass; refresh-reuse revokes token family.

## Traceability
- Plan B §9.1, §12.1, §13.1–§13.3.
