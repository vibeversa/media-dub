# Task 042A — Basic CI Early Gate (Typecheck, Lint, Unit, Build)

**Required/Optional:** Required
**Complexity:** S

## Goal
Gate every PR on typecheck, lint, unit tests, and build long before full contract/security/perf gates exist.

## Context
Every task lists validation commands, but full CI lived in Task 042 after nearly all work — so early feature work had no shared red/green gate. This task adds the minimal always-on pipeline; Task 042 (now 042B scope) keeps full gates (contract drift, image scan/SBOM/sign, visual/a11y/perf, audit). Basic CI must land with the harness (046) before feature work (019+).

## Starting State
Depends on Tasks 015 (frontend scaffold), 046 (harness so `npm run test` and seeded configs exist). Backend (Plan A + 001) builds with `dotnet build`.

## Scope
Included: backend restore/build/unit, frontend install/typecheck/lint/unit/component/build, PR trigger, fail-closed on warnings/errors, CI timing baseline.
Excluded: integration/contract/E2E/visual/a11y (042B), container build/scan/SBOM/sign/publish (042B), OpenAPI drift (014/042B), Testcontainers suites (042B).

## Instructions
1. Create `.github/workflows/basic-ci.yml`: triggers `pull_request` + `push` to main; jobs `backend-basic` (setup-dotnet 10, cache NuGet, `dotnet restore`, `dotnet build --warnaserror`, `dotnet test --filter FullyQualifiedName~UnitTests`), `frontend-basic` (setup-node pinned, cache npm, `npm ci`, `npm run typecheck`, `npm run lint`, `npm run test`, `npm run build`). Fail on TS errors, lint errors, test failures, build failures, or compiler warnings.
2. Pin toolchain versions (dotnet SDK from `global.json`, node version file) so local `Validation` blocks reproduce CI; document versions at top of the workflow file.
3. Handle Testcontainers absence: basic CI runs unit-only (no containers); if a unit test requires containers it must be tagged `Integration` and excluded here (fail-closed rule: untagged container dependency fails the job with `TESTCONTAINERS_REQUIRED_BUT_UNAVAILABLE` rather than silently skipping).
4. Add `npm audit --omit=dev --audit-level=high` as non-blocking annotation in basic CI (blocking audit lives in 042B); record decision in workflow comments.
5. Document in `docs/ci.md` (one page or section): what basic CI runs, how to reproduce locally (`dotnet build`, `npm run typecheck/lint/test/build`), and that 042B adds the remaining gates.

## Requirements
- R1: Every PR runs backend + frontend basic jobs; either job failing blocks merge.
- R2: Warnings-as-errors enforced (`--warnaserror` / non-zero lint/typecheck exit).
- R3: Unit-only: no Testcontainers required; container-dependent tests excluded by tag, never silently skipped.
- R4: Local reproduction commands match CI exactly (same commands as task Validation blocks).
- R5: Audit is advisory here; blocking audit is 042B.

## Edge Cases and Error Handling
- Cache poisoning (stale NuGet/npm) → cache keys include lockfiles; cache-miss falls back to clean install, never partial restore.
- Frontend build passes but typecheck fails → job fails (build alone is not sufficient).
- Flaky unit test → quarantine per 046 policy with owner + issue; no silent retry in CI.

## Security and Safety Requirements
- CI runs on `pull_request` with read-only permissions where possible; no secrets injected into basic jobs.
- No publishing or image signing in this workflow (042B owns release-adjacent permissions).

## Testing
- This task IS CI config: validated by opening a test PR (or `act -j backend-basic` / `act -j frontend-basic` where available) showing red on injected failure and green on fix.
- No C# product tests added here beyond using existing `UnitTests` filter.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~UnitTests
npm run typecheck --prefix frontend
npm run lint --prefix frontend
npm run test --prefix frontend
npm run build --prefix frontend
```

## Completion Criteria
- `basic-ci.yml` exists, runs per-PR, fails closed on warnings/errors/test failures, reproduces locally, and documents the 042B follow-up gates.

## Traceability
- Plan B §16.1–§16.2 (CI subset), §22 (early B-2/B-3 enablement). Existing Task 042 retains full gates as 042B; this task is the early prerequisite.
