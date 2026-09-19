# Task 042 — CI Pipelines and Contract Management

**Required/Optional:** Required
**Complexity:** M

## Goal
Gate backend extensions and frontend on build/test/scan/contract checks with OpenAPI breaking-change detection blocking merges.

## Context
Backend work (Tasks 001–013, 037–038) and frontend work (Tasks 014–036) need reproducible pipelines per Plan B §16.1–§16.3; the generated TS client (Task 014) must never drift from the API; `deploy/verify.sh` is the release gate. Coverage thresholds from Task 039 and smoke from Task 041 are enforced here, not just defined.

## Starting State
Tasks 014, 039–040 done. Depends on Tasks 014 and 039–040.

## Scope
Included: backend CI, frontend CI, OpenAPI breaking-change detection, `deploy/verify.sh` gate.
Excluded: hosting/rollout (Task 043), E2E scenario authorship (Task 041), test authorship (Tasks 039–040).

## Instructions
1. Create `.github/workflows/backend.yml`: restore → build (warnings-as-errors, `TreatWarningsAsErrors=true`) → `UnitTests` → `IntegrationTests` (Testcontainers PG + storage emulator services) → contract tests (OpenAPI snapshot match) → EF migration-compat check (script applies on previous-release schema) → image scan (Trivy, fail on HIGH/CRITICAL) → SBOM (Syft artifact) → sign (cosign) → publish (versioned tag only).
2. Create `.github/workflows/frontend.yml`: install (locked `npm ci`) → lint → typecheck → unit/component (`npm run test` + coverage thresholds from Task 039, fail below) → build → codegen-verify (`make generate-api` + `git diff --exit-code` on `src/api/generated/`) → E2E (`@cross-layer` + journeys + `@smoke`) → `@visual` (baseline compare) → `test:a11y` → `npm audit` (fail on high) → image build + publish (versioned tag only).
3. Implement OpenAPI breaking-change detection in `.github/workflows/contract.yml` + `scripts/openapi-diff.sh`: compare PR OpenAPI against `main` (oasdiff); removed/renamed paths, removed required fields, tightened enums, changed auth scope → fail with annotated diff; additive changes pass; version bump required on any contract change (`openapi:version` check).
4. Create `deploy/verify.sh`: post-deploy gate — health endpoint, `/openapi.json` version match vs deployed tag, smoke spec (`@smoke`) against the deployed environment, rollback trigger on any failure (hands off to Task 043 runbook); script exits non-zero with machine-readable failure reason.
5. Set branch protection expectations in `docs/ci-branch-protection.md`: required checks (both pipelines + contract), no admin bypass without recorded reason, flake-quarantine list (Task 041) reviewed weekly; document the red-build protocol (revert-first for `main`, owner-assigned within 1h).

## Requirements
- R1: Backend pipeline fails on warnings, test failures, migration incompat, image HIGH/CRITICAL, or missing SBOM/signature.
- R2: Frontend pipeline fails on lint/type/coverage/codegen-drift/E2E/visual/a11y/audit failures.
- R3: Breaking OpenAPI changes fail with annotated diff; additive changes pass; version bump enforced.
- R4: `deploy/verify.sh` gates releases and triggers rollback on failure with machine-readable reasons.
- R5: Only versioned tags publish images; `latest`/untagged pushes never publish.

## Edge Cases and Error Handling
- Testcontainers unavailable on a runner → job fails closed with `INFRA_UNAVAILABLE`, never silently skips integration tests.
- Visual baseline missing for a new screen → job marks `BASELINE_NEEDED` and blocks, requires explicit baseline approval PR.
- `npm audit` new advisory on existing lockfile → fail with advisory id + upgrade path, owner auto-assigned.
- Contract diff tool version drift → pin oasdiff version in workflow, checksum-verified.
- Verify.sh partial failure (health ok, smoke fails) → rollback + `SMOKE_FAILED` reason artifact.

## Security and Safety Requirements
- Images signed (cosign) with provenance; unsigned images never deployable (admission note for Task 043).
- SBOM attached per image; secrets-scan (gitleaks) on every PR touching `deploy/` or workflows.
- CI OIDC only — no long-lived cloud credentials in repo secrets beyond the documented bootstrap set.

## Testing
- Pipeline definitions are validated by dry-run (`act -n` or workflow lint) + a deliberate-break canary PR (remove a required field → contract job fails; break coverage → frontend job fails).
- `deploy/verify.sh` tested against ephemeral review environment before release use.
- Type: pipeline config + shell; canary-PR verification recorded in the PR checklist.

## Validation
```bash
bash deploy/verify.sh
gh workflow list --all
git diff --exit-code frontend/src/api/generated/
```

## Completion Criteria
- Both pipelines + contract detection green on PR with canary-break proof, versioned signed publishes only, and `deploy/verify.sh` gating releases; CI green + verify.sh pass.

## Traceability
- Plan B §16.1–§16.3. Depends on Tasks 014 and 039–040.

## Review Fix — Renamed Scope (042B Full Gates)
- **This file is now 042B scope:** full gates (integration/contract, migration compat, image scan/SBOM/sign/publish, OpenAPI drift via oasdiff or equivalent, visual/a11y/perf, audit-blocking). Basic per-PR gates (typecheck/lint/unit/build) live in `042A-basic-ci.md` and must land first with harness 046.
