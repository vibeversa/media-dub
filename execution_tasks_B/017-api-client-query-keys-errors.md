# Task 017 — API Client, Query Keys, Error Normalization

## Goal
Wire the generated client with a query-key factory, auth headers, correlation IDs, and normalized errors.

## Context
All feature tasks fetch through this layer; no ad-hoc query keys or hand-rolled fetch shapes may exist elsewhere. Backend error envelope comes from Task 013; generated types from Task 014; Query defaults from Task 015.

## Starting State
Tasks 014 (generated client in `frontend/src/api/generated/`) and 015 (scaffold, `QueryProvider`) done. No `api/client`, `api/queryKeys`, or `api/errors`. Depends on Tasks 014, 015.

## Scope
Included: `api/client` (headers, token attach, correlation, idempotency), `api/queryKeys` factory + invalidation registry, `api/errors` normalization with recovery hints, GET-only retry, stale-generated-types build gate.
Excluded: feature hooks/screens, auth session logic (Task 019), telemetry event emission (Task 018).

## Instructions
1. Create `frontend/src/api/client/httpClient.ts`: wraps the generated client with base URL from `lib/env`; attaches `Authorization: Bearer <in-memory token>` via a token-provider callback (registered by Task 019; default throws `NOT_AUTHENTICATED`); sends `X-Correlation-ID` (uuid per request, propagated to telemetry); supports `Idempotency-Key` header on POST mutations (caller-supplied or generated; retries reuse the same key).
2. Create `frontend/src/api/queryKeys/keys.ts`: hierarchical factory with `all`/`lists`/`detail` scopes for `dashboard`, `projects`, `project(id)`, `workspace(id)`, `progress(id)`, `segments(id, params)`, `segment(id, segId)`, `speakers(id)`, `review(id)`, `reviewContext(id)`, `exports(id)`, `notifications`, `admin`; export `queryKeyRegistry` (event-type → key mapping consumed by Task 026 invalidation). String-literal `queryKey` outside this module is forbidden (no ad-hoc keys).
3. Create `frontend/src/api/errors/` (`kinds.ts`, `normalizeError.ts`): map backend `{code, message, correlationId, details}` envelope → `AppError {kind, code, message, correlationId, retryable, recoveryHint}` where kind ∈ `Validation | Auth | Conflict | Quota | Media | Review | Unknown`; per-code recovery hints (e.g. `QUOTA_EXCEEDED` → manage-usage link; `STALE_VERSION` → refresh-and-retry; `401` → re-login). `retryable` is true only for idempotent GETs and network errors.
4. GET-only retry: `httpClient` retries network failures + 502/503/504 on GET up to 2× with exponential backoff; never auto-retry POST/PUT/PATCH/DELETE (mutations rely on idempotency keys + explicit user retry).
5. Stale-generated-types fail build: `frontend/package.json` `prebuild` runs `make check-api-drift` (Task 014 script); feature code imports generated types only via the `api/client` barrel, never deep-imports `api/generated` (lint `no-restricted-imports`).
6. Add `frontend/src/api/hooks.ts` barrel: re-export generated hooks/client plus `useAppMutation` wrapper (attaches idempotency key + returns normalized `AppError`).

## Requirements
- R1: No ad-hoc query keys (test scans for inline `queryKey:` literals outside `api/queryKeys`).
- R2: Every request carries `X-Correlation-ID`, echoed into normalized errors and telemetry.
- R3: Token read from in-memory provider only, never `localStorage`/`sessionStorage`.
- R4: Error kinds exhaustive over backend codes; unknown code → `Unknown` with correlation ID and report hint.
- R5: POST mutations accept/reuse idempotency keys; double-submit sends one effective request.
- R6: Stale generated client fails the build via the drift gate.

## Edge Cases and Error Handling
- 401 on any request → emit `auth:expired` event (Task 019 listens); never retry 401.
- 409 `STALE_VERSION` → surfaces expected/actual versions in `details` for conflict UX.
- Offline/network failure → `Unknown` + `retryable: true` with offline recovery hint.
- Correlation ID shown in error UI ("report this ID") for support triage.

## Security and Safety Requirements
- Tokens in memory only, never logged or sent to telemetry.
- Correlation IDs are random opaque uuids (no tenant/user info).
- Error `details` rendered as text, never HTML; no media/transcript content in error telemetry.

## Testing
- Create `frontend/src/api/__tests__/queryKeys.test.ts` (factory shape/scope stability), `normalizeError.test.ts` (every backend code → kind + hint), `httpClient.test.ts` (headers, GET-only retry, idempotency-key reuse) with MSW.
- Type: unit (vitest); run `npm run test -- src/api`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/api
make check-api-drift
```

## Completion Criteria
- Client, key factory + registry, and error normalization exist; no ad-hoc keys; GET-only retry verified; drift gate fails build on stale types; api tests pass.

## Traceability
- Plan B §10.3, §10.4, §9.12. Depends on Tasks 014, 015.
