# Task 048 — Frontend Feature-Flag Evaluation Hook

**Required/Optional:** Required
**Complexity:** S

## Goal
Add the frontend runtime flag-evaluation layer: `useFeatureFlag` (or equivalent) sourced from `/me` or bootstrap config, fail-closed, rollout-only.

## Context
006 owns the `/me` flags payload (`GET /me` returns `featureFlags`); 018 owns shell/guards; 036 owns the admin FlagsPanel display. None provides a shared evaluation hook. This task provides it and wires it into admin-gated and enrichment-gated UI areas without touching authorization.

## Starting State
Depends on 006 (`GET /me` returns `featureFlags`), 018 (shell + `RequireAdmin` guard slots), 036 (admin/enrichment surfaces to gate). No shared flag hook exists (only payload + panel references exist).

## Scope
Included: hook + flag source resolution + fail-closed default + rollout-only wiring into admin/enrichment-gated areas.
Excluded: `/me` contract changes (006, frozen), shell/guard redesign (018, reuse), admin panel logic (036, consume), new flags backend (plan §6.9: DB-backed flags not required).

## Instructions
1. Create `frontend/src/config/featureFlags.ts` (flag-key type + fail-closed default map) and `frontend/src/hooks/useFeatureFlag.ts`: `useFeatureFlag(key): boolean`. Sources, in order: `/me.featureFlags` (006) where present, else bootstrap config (`VITE_ENABLE_*` family per plan §17.4). **Assumption:** plan names no hook path, key names, or per-flag defaults; path/keys are conventions, every default is `false` (closed).
2. Fail closed: unknown/missing flag → `false` + safe fallback UI (hidden or disabled experimental surface, never error crash). Test asserts missing-flag → `false`.
3. Rollout-only: hook controls visibility/exposure only; every gated action still passes existing permission/authz checks (006 permission strings, 018 `RequireAdmin`, server-side authz). Add negative test: flag ON + permission OFF → still denied.
4. Wire into admin-gated areas (036 surfaces) and enrichment-gated areas (044: video-intel, lip-sync, local-GPU exposure). No other behavior change.

## Requirements
- R1: Single shared hook; no ad-hoc flag reads elsewhere (lint/grep gate).
- R2: Missing/unknown flag evaluates `false`.
- R3: Flags never grant permission (flag cannot bypass 403/authz).
- R4: Flag source is `/me` or bootstrap config only; no new flag-service scope.
- R5: Admin + enrichment surfaces consume the hook (at least one wired call-site each).

## Edge Cases and Error Handling
- `/me` flags absent (e.g. load failure) → all flags `false`, non-admin shell (consistent with 018 fail-closed).
- Flag ON but backend 403/423 on use → dialog explains denial/freeze, flag state unchanged.
- Bootstrap vs `/me` conflict → `/me` wins where both present (documented).

## Security and Safety Requirements
- Flags are UX rollout hints only (plan §6.9); server remains authorization authority. No secrets in flag values; no flag value in telemetry beyond key + boolean.

## Testing
- Create `frontend/src/hooks/__tests__/useFeatureFlag.test.tsx`: true/false/missing-flag-default-false, `/me`-over-bootstrap precedence, flag-ON-without-permission still denied (with mocked guard).
- Type: unit (vitest); consumed E2E stays in 036/044 specs.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/hooks/useFeatureFlag
```

## Completion Criteria
- UI evaluates flags via the shared hook, missing flags default safely (false), flags do not replace permission checks.

## Traceability
- Plan B §6.9, §12.19, §19 (flag source: §9.1/§12.1 `/me`). Depends on 006, 018, 036.
