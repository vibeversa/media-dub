# Task 040B — Seven Named Cross-Layer Seam Specs

**Required/Optional:** Required
**Complexity:** M

## Goal
Prove the seven named frontend→backend→infrastructure seams on the 040A rig.

## Context
Split from Task 040. Endpoint integration is already owned by 006–013 and must not be re-asserted here; this task owns only the seams where layers meet (the exact list below). Each seam runs on the 040A harness with real FE+API+PG+storage+transport and mock AI.

## Starting State
Depends on Task 040A (rig + helpers). Endpoints from 006–013 and UI from 019–036 exist as the surfaces under test. Task 040 (combined) is superseded.

## Scope
Included: exactly the seven seam specs below, nothing more.
Excluded: endpoint status-code matrices (006–013), harness itself (040A), journeys/visual/a11y/perf (041A–D).

## Instructions
1. Add specs in `tests/cross-layer/seams/` (tagged `@cross-layer`), one file per seam, all using 040A helpers (no bespoke boot):
  - `seam-upload-storage.spec.ts`: frontend upload → object storage bytes + server validation → ready state (covers 023 + Plan A ingestion).
  - `seam-processing-sse.spec.ts`: processing start → SSE events → workspace refetch as source of truth (covers 024/026 + 008).
  - `seam-review-mutation.spec.ts`: review resolution → versioned mutation + audit + invalidation (covers 031 + 003/009/011).
  - `seam-export-download.spec.ts`: export creation → signed-URL download + completeness metadata (covers 033 + 012A).
  - `seam-voice-invalidation.spec.ts`: voice change → dependent invalidation/retry + consent gate (covers 029 + 010).
  - `seam-stale-conflict.spec.ts`: stale edit → 409 → refresh UX with draft preserved (covers 027/028 + 003/009).
  - `seam-notification.spec.ts`: backend event → durable notification → center render + deep link (covers 034 + 002/012B).
2. Each spec asserts both sides of the seam (client state + server rows/artifacts via `assertArtifacts`), and asserts the failure half (e.g. expired URL, revoked consent, conflicting version) — happy-path-only seams fail review.
3. Tag and quarantine per 046: `@cross-layer` required; flake → quarantine with owner + issue, never silent retry.
4. Record results mapping in `tests/cross-layer/seams/README.md` (seam → owning feature tasks → pass criteria).

## Requirements
- R1: All seven seams pass on the 040A rig.
- R2: No per-route status-code duplication (that coverage stays in 006–013).
- R3: Every seam asserts server-side rows/artifacts, not just UI text.
- R4: Every seam covers its named failure half.
- R5: Quarantine policy enforced (046).

## Edge Cases and Error Handling
- Seam passes alone but fails in full suite → tenant isolation per worker (046) is the fix; shared-tenant shortcuts forbidden.
- Mock-AI nondeterminism → deterministic mock outputs only; any randomness fails the seam.
- SSE flake → event-driven waits (040A) required; fixed-sleep waits rejected in review.

## Security and Safety Requirements
- Seams use synthetic tenants; cross-tenant assertions (where applicable) expect 404 without existence leak.
- Downloaded bytes in export seam are fixture media; scrubbed before CI attach.

## Testing
- Added specs: `tests/cross-layer/seams/*.spec.ts` (seven files).
- Type: cross-layer (Playwright or equivalent runner per 040A README).

## Validation
```bash
docker compose -f tests/cross-layer/docker-compose.cross.yml up -d
npx playwright test --grep="@cross-layer"
docker compose -f tests/cross-layer/docker-compose.cross.yml down
```

## Completion Criteria
- Seven seam specs exist and pass on the shared rig; mapping README complete; supersedes the seam half of Task 040.

## Traceability
- Plan B §15.4–§15.5 (seam slice). Split from 040; harness in 040A; endpoint tests stay in 006–013.
