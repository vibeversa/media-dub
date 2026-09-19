# Task 040A — Cross-Layer Seam Harness

**Required/Optional:** Required
**Complexity:** S

## Goal
Own the cross-layer test harness (real FE + API + PG + storage + transport, mock AI) that named seam specs run against.

## Context
Rewrite/split from oversized Task 040 (which duplicated endpoint integration already owned by 006–013). Endpoint integration stays in 006–013; this task owns the shared cross-layer rig; 040B owns the seven named seam specs. Without this rig the seams cannot run deterministically.

## Starting State
Depends on Tasks 046 (fixtures, reset, auth seeds), 006–013 (endpoints under test), 014 (contract bundle for mock-AI shape). Task 040 (combined) is superseded by 040A + 040B.

## Scope
Included: compose/up script, seeded environment, mock-AI provider, SSE-aware test client, artifact assertion helpers, runbook for local execution.
Excluded: endpoint integration tests (006–013), the seven seam specs themselves (040B), journeys/visual/a11y/perf (041A–D), CI wiring (042B).

## Instructions
1. Add `tests/cross-layer/docker-compose.cross.yml` (or reuse root compose with `cross` profile): API + PostgreSQL + object storage emulator + message transport + mock-AI provider; frontend served from `frontend/dist` (built) or vite preview on fixed port; document ports in `tests/cross-layer/README.md`.
2. Add `tests/cross-layer/harness/` : `seed.ts` (or `seed.cs`) building synthetic tenant/user/project via 046 factories, `mockAi.ts` (deterministic transcript/translation/voice outputs), `sseClient.ts` (event-driven waits from 046, no sleeps), `assertArtifacts.ts` (project row, segments, export file, notification row exist post-run).
3. Add harness smoke `tests/cross-layer/harness.spec.ts` (tagged `@cross-layer`): boots stack, seeds, runs one processing-start → SSE → workspace-read round trip, asserts artifacts, tears down; failing harness blocks 040B (fail-closed).
4. Make mock-AI failure explicit: mock down → `AI_MOCK_UNAVAILABLE` fail-fast, never timeout-hang; record in README.
5. Document local run: `docker compose -f tests/cross-layer/docker-compose.cross.yml up -d && npx playwright test --grep="@cross-layer-harness"` (or equivalent runner).

## Requirements
- R1: Harness boots real FE+API+PG+storage+transport with mock AI only.
- R2: Seed/reset/auth/SSE helpers shared by all 040B specs (no per-spec bespoke boot).
- R3: Harness smoke passes deterministically (seeded data, frozen clock where applicable).
- R4: Mock-AI outage fails fast with named error.
- R5: No endpoint-integration duplication: harness asserts seams, not per-route status codes (those live in 006–013).

## Edge Cases and Error Handling
- Port collision → fixed ports from 046 with collision error naming the holder, not silent skip.
- Partial boot (DB up, broker down) → fail-closed with service matrix, never half-run specs.
- Leftover state from prior run → `reset` runs before seed, always.

## Security and Safety Requirements
- Synthetic tenants only; seeded credentials ephemeral; no prod connection strings in compose (emulator images + env placeholders only).
- Cross-layer logs scrubbed (046 scrubber) before attaching to CI artifacts.

## Testing
- This task IS the harness + `harness.spec.ts` (`@cross-layer-harness`).
- Type: cross-layer rig smoke.

## Validation
```bash
docker compose -f tests/cross-layer/docker-compose.cross.yml up -d
npx playwright test --grep="@cross-layer-harness"
docker compose -f tests/cross-layer/docker-compose.cross.yml down
```

## Completion Criteria
- Cross-layer stack + helpers + harness smoke + README exist; 040B specs can run on this rig; supersedes the harness half of Task 040.

## Traceability
- Plan B §15.4–§15.5 (harness slice). Split from 040; endpoint integration stays in 006–013; seams in 040B.
