# Task 039A — Test Infrastructure and Coverage Enforcement

**Required/Optional:** Required
**Complexity:** S

## Goal
Own coverage configuration and the MSW/unit harness contract so gap-closure tasks have a measurable baseline.

## Context
Rewrite/split from oversized Task 039 (which mixed infra, frontend matrices, and backend unit gaps while overlapping feature-level tests). Feature tasks (006–036) author their own specs against harness 046; this task owns the shared config: coverage thresholds, MSW taxonomy conformance, and the gap report that 039B/039C close. No feature specs are authored here.

## Starting State
Depends on Task 046 (harness + taxonomy + factories). Task 039 (combined) is superseded by 039A + 039B + 039C.

## Scope
Included: Vitest coverage config + thresholds, MSW conformance test, gap-report script, ownership doc enforcement.
Excluded: feature specs (006–036 own them), state-matrix closure (039B), backend unit gaps (039C), integration/cross-layer (040A/B), E2E (041A–D).

## Instructions
1. Add coverage config in `frontend/vitest.config.ts` (or `vitest.coverage.ts`): thresholds per area (e.g. `api/, lib/, hooks/` minimums agreed once and recorded), `npm run test -- --coverage` output, uncovered-file report in `coverage/` (gitignored) + `scripts/coverage-gap.mjs` listing files below threshold.
2. Add MSW conformance test `frontend/src/mocks/conformance.spec.ts`: asserts every taxonomy entry from 046 returns the documented envelope (success/401/403/404/409/429/500/validation/provider-error/partial/stale-conflict) — fails on missing or envelope-incorrect handler.
3. Add backend coverage gate reference: `coverlet` (or equivalent) settings in `tests/` props; `dotnet test --collect:"XPlat Code Coverage"` documented; gap list consumed by 039C.
4. Enforce `docs/test-ownership.md` (046): this task fails CI if a feature area has zero specs (presence gate), but does not author those specs — it reports the gap for 039B/039C.
5. Document thresholds + exclusion policy in `docs/coverage.md` (what is measured, what is excluded and why, how to close a gap).

## Requirements
- R1: Coverage thresholds configured and reported for frontend + backend.
- R2: MSW taxonomy 100% conformant (every entry present + envelope-correct).
- R3: Gap script lists every below-threshold file; empty output means 039B/039C are done.
- R4: Presence gate: every feature area has at least one spec; missing area fails with the owning task ID.
- R5: No feature test logic added here — only config, conformance, and reporting.

## Edge Cases and Error Handling
- Coverage tool version drift → lockfile-pinned versions; mismatch fails with version message, not silent zero-coverage.
- Generated code (`api/generated/`) excluded from thresholds by policy, never by accident (explicit exclude list).
- Flaky conformance (port collision) → fixed MSW ports per 046, retry forbidden.

## Security and Safety Requirements
- Coverage reports contain file paths + counts only; never source excerpts with secrets.
- No coverage gate bypass flag without expiry-tracked exception (quarantine policy 046).

## Testing
- This task IS config + `conformance.spec.ts` + `coverage-gap.mjs` + presence gate.
- Type: config smoke + conformance.

## Validation
```bash
npm run test --prefix frontend -- src/mocks/conformance.spec.ts
npm run test --prefix frontend -- --coverage
dotnet test --filter FullyQualifiedName~UnitTests --collect:"XPlat Code Coverage"
node scripts/coverage-gap.mjs
```

## Completion Criteria
- Coverage config + conformance + gap script + ownership presence gate exist and run; gap list feeds 039B/039C; supersedes the infra third of Task 039.

## Traceability
- Plan B §15.1–§15.3 (infra slice). Split from 039; harness in 046; closure in 039B/039C.
