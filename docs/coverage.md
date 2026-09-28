# Coverage (Task 039A)

What is measured, what is excluded and why, and how to close a gap. The 80%
per-file target is enforced by `scripts/coverage-gap.mjs`; the gate that fails
builds is the vitest `thresholds` block (raised to 80 by 039B once the
frontend matrices were closed) plus the presence gate. 039C still works the
backend side toward the same target.

## Frontend (Vitest + V8)

- Canonical config: `frontend/vite.config.ts` (`test.coverage`). There is no
  separate `vitest.config.ts` by design — one config, no dual-config drift.
- Command: `npm run test --prefix frontend -- --coverage`.
- Artifacts (gitignored, never committed): `frontend/coverage/` — `text`
  (console), `lcov` (`lcov.info`), `html` (browseable), `json-summary`
  (`coverage-summary.json`, machine input for `scripts/coverage-gap.mjs`).
- Tool versions are lockfile-pinned: `vitest` and `@vitest/coverage-v8` must
  share a major (currently `2.1.8`); `msw` is pinned (`2.15.0`) for the
  taxonomy server. `scripts/coverage-gap.mjs` verifies the vitest/coverage-v8
  majors before reading any summary and fails with
  `COVERAGE_TOOL_VERSION_MISMATCH` on drift — never a silent zero-coverage
  pass. If the summary is absent it fails with `COVERAGE_SUMMARY_MISSING`
  (run the coverage command first).

### Thresholds

| Layer | Lines | Branches | Functions | Statements | Enforced by |
| --- | --- | --- | --- | --- | --- |
| CI gate (global) | 80 | 80 | 80 | 80 | `test.coverage.thresholds` in `vite.config.ts` |
| Per-file target | 80 | 80 | 80 | 80 | `scripts/coverage-gap.mjs` report |

The gate started at a 72/64/68/72 floor (measured 039A baseline 78.2 lines /
71.18 branches / 74.66 funcs, minus headroom so it was stable run-to-run) and
was raised to 80 across the board by 039B once the state-matrix suites
emptied the gap report; the frontend now measures 96.5 / 91.9 / 98.5 / 96.5.
`node scripts/coverage-gap.mjs` lists every below-target file as
`COVERAGE_GAP:<path> <metric>=<pct>...`; empty output means the per-file target
is met. The script exits 0 with gaps present (it is a report; the vitest
thresholds are the gate). Never lower a threshold to green a red gate — add a
spec, or record an expiry-tracked quarantine entry per the policy below.

### Exclusion policy

Explicit `exclude` list in `vite.config.ts` (policy, never accident):

- `src/api/generated/**` — generated OpenAPI client owned by Task 014
  (generator + drift gate); testing generated output would test the generator.
- `src/**/*.test.*`, `src/**/*.spec.*`, `src/testSetup.ts` — tests and harness.
- `src/**/*.stories.*` — Storybook stories (Task 039B, permanent): dev-only
  component demos, never shipped in the app bundle. State coverage for the
  underlying components lives in Vitest (`*.test.*`); visual coverage lives
  in 041B visual regression, which consumes the stories. Counting stories in
  Vitest would double-count demos as product states.
- `**/*.d.ts`, `playwright.config.ts`, `e2e/**` — types and E2E (041A–D own).
- `dist/**`, `storybook-static/**`, `.storybook/**` — build/output artifacts.
  `include` stays `src/**` so V8 never pulls bundles into the table.

### 039B intentional exclusions (state-matrix scope)

No frontend-matrix area is excluded. 039B closes every `COVERAGE_GAP` line
for `src/app`, `src/components` (runtime `.tsx`/`.ts` only, stories excluded
above), `src/api` (excl. `generated/`), `src/hooks`, `src/lib`, `src/stores`,
`src/telemetry`, `src/i18n`, `src/features`, and `src/mocks` with real specs
(see the area specs under each `__tests__/` dir). Pure re-export barrels
(`src/**/index.ts` except `src/stores/index.ts`, which owns the Zustand
root) are covered by import assertions in
`src/app/__tests__/barrels.test.ts` — executing the barrel is the behavior.
Expiry: none (permanent policy entries above); any future quarantine still
requires owner + issue + expiry per the policy below.

Defensive-only branches that stay uncovered are ordinary code, not exclusions:
`WorkspacePage.formatBytes`'s `units[unit] ?? 'B'` and `capitalize('')`
unreachable guards, and the `useActivity` page-size clamps reachable only from
a hand-typed URL. They are exercised through their real call sites rather than
excluded, so a refactor that makes them reachable fails the gate instead of
silently losing coverage.

## Backend (coverlet + XPlat Code Coverage)

- Collector `coverlet.collector` is pinned (`6.0.4`) in every test project
  (`tests/DubbingPlatform.{UnitTests,IntegrationTests,ContractTests,E2ETests}/*.csproj`).
- Gate reference settings: `tests/coverage.runsettings` (formats
  `cobertura,json`; excludes test assemblies and `*.Generated`;
  `IncludeDirectory ../src`).
- Commands (from the repo root):
  - `dotnet test --filter FullyQualifiedName~UnitTests --collect:"XPlat Code Coverage"`
  - With the runsettings: `dotnet test --filter FullyQualifiedName~UnitTests --collect:"XPlat Code Coverage" --settings tests/coverage.runsettings`
- Reports land under `tests/**/TestResults/` (gitignored).
  `scripts/coverage-gap.mjs --backend` merges those coverlet payloads and
  reports the in-scope files below 80% as
  `COVERAGE_GAP:<path> lines=<pct> branches=<pct>`; empty output means the
  backend unit target is met. It reads `coverage.json` when the run used
  `tests/coverage.runsettings` and falls back to `coverage.cobertura.xml`
  (what the plain `--collect:"XPlat Code Coverage"` command emits), and it
  takes the **max** hit count per instrument across test projects so a file
  instrumented by four projects is not counted four times.

### 039C unit scope

`scripts/coverage-gap.mjs --backend` reports only the `BACKEND_SCOPE` manifest
in that file. The manifest mirrors task 039C instruction 1 plus its Context
list, and is the contract 039C had to make empty:

| Area | In-scope paths (`src/…`) |
| --- | --- |
| Status mapping + lifecycle | `DubbingPlatform.Domain/Entities/ProjectStatusProjection.cs`, `DubbingPlatform.Application/StateMachines/` |
| Permission evaluation | `DubbingPlatform.Application/Authorization/` |
| Settings-schema validation | `DubbingPlatform.Application/Validation/`, `Application/Projects/{ProjectSettingsGuard,ProjectConfigHash,ProjectExceptions}.cs`, `Application/Configuration/{ConfigurationHashCalculator,ExecutionSnapshotCalculator}.cs`, the `Application/Options/*` validators |
| Selection-version support | `Domain/Entities/{SegmentSelection,SegmentOverlap,SegmentContextAssignment,OverlapGroup,StageUnitCompletion,TranscriptVersion,TranslationVersion}.cs`, `Application/Segments/SegmentApiExceptions.cs` |
| Notification dedup + recipients | `Application/Notifications/`, `Domain/Entities/Notification.cs` |
| Voice-preview quota + consent | `Domain/Voice/ConsentGate.cs`, `Domain/Entities/{VoicePreviewJob,SpeakerVoiceAssignment,ConsentRecord,VoiceProfile}.cs`, `Application/Previews/`, `Application/Voices/{VoiceCompatibility,VoiceApiExceptions}.cs` |
| Diagnostics aggregation | `Application/Diagnostics/` (logic; its `Dto/` record shapes are excluded) |
| Error-code mapping | `Application/Errors/`, `Application/Exceptions/`, `Api/Errors/`, `Api/Middleware/ErrorResponse.cs`, `Domain/Exceptions/` |
| Idempotency-key handling | `Application/Processing/ProcessingIdempotency.cs`, `Application/Services/Idempotency*.cs`, `Domain/Entities/IdempotencyRecord.cs` |
| Correlation propagation | `Api/Middleware/CorrelationIdMiddleware.cs`, `Api/Middleware/CorrelationMiddleware.cs` |
| Signed-URL expiry + keys | `Application/Storage/{SignedUrlPolicy,StorageKeyBuilder}.cs`, `Api/Services/SignedUrlService.cs` |
| Formatting / export generation | `Application/Exports/` generators and parsers, `Application/Services/{DurationEstimator,GuidUtility}.cs`, `Domain/ValueObjects/*` |
| Query construction + paging | `Application/Common/PaginatedResult.cs`, `Application/Projects/ProjectListQuery.cs`, `Application/Security/RedisKeys.cs`, `Application/MultiTenancy/TenantGuard.cs`, `Domain/Identity/PublicIdMapper.cs`, `Api/Models/PublicIdParser.cs` |
| Security helpers | `Application/Security/{SecretPolicy,SecretRedactor}.cs` |
| Enrichment + SSE policy | `Application/Enrichment/`, `Api/Sse/` |
| Activity projection | `Application/Activity/`, `Domain/Entities/ActivityEvent.cs` |
| DTO request validation | `Api/Validation/DtoValidators.cs` |

### 039C intentional exclusions (backend units)

Task 039C R1 accepts a unit test **or** a recorded exclusion. Everything
outside the manifest above is excluded by policy, never by accident:

| Excluded | Reason | Owner | Expiry |
| --- | --- | --- | --- |
| `Api/Program.cs`, every `Placeholder.cs` | Composition root / DI wiring; the behaviour is "the host boots", asserted by `SmokeTests` + the contract tests | 015/018, 040A | none (permanent) |
| `Api/Controllers/**`, `Api/Middleware/**` (except the pure helpers listed in scope), `Api/Filters/**`, `Api/Auth/**` | HTTP endpoint plumbing — endpoint-level assertions belong to Tasks 006–013 and the cross-layer seams in 040 | 006–013, 040A/B | none (permanent) |
| `Api/OpenApi/**`, `Infrastructure/Persistence/Migrations/**` | Generated schema/document output; testing it tests the generator | 014 | none (permanent) |
| DTO/record-only types: `Api/Models/*Dtos.cs`, `Contracts/Messages/**`, `Application/**/Dto*/**`, `Abstractions/Providers/Dtos/**` | No logic — coverlet counts record constructors and accessors as uncovered lines; the wire shape is asserted by the contract tests (014) and the endpoint tests (006–013) | 014, 006–013 | none (permanent) |
| `*Registration.cs`, `Infrastructure/Health/**`, `Infrastructure/**/*Metrics.cs`, `Infrastructure/**/*Meters.cs` | Registration / health-check / meter plumbing; asserting it means booting a host, which is an integration concern | 040A | none (permanent) |
| `Application/Previews/{MediaPreviewGenerator,QcEvidenceLinker,VoicePreviewService}.cs` — only `PreviewAudio.cs` is in scope | These stamp artifact rows with `DbContext.Database.ExecuteSqlRawAsync` and publish through `ArtifactService` → `IArtifactStorage`. `ExecuteSqlRaw*` is relational-only: the EF InMemory provider throws on it and no SQLite/relational provider is referenced by the unit project, so the success path is unreachable here. Per instruction 3 the test is re-tagged Integration and moves to the owning endpoint task. Their pure guards, constants and degrade-on-failure contracts are still unit-tested. | 006–013, 040A | none (permanent) |
| Any type whose collaborators require Postgres, Redis, RabbitMQ, object storage, or a live HTTP client | Task 039C instruction 3: such a test is re-tagged `Integration` and moved to its owning endpoint task. A unit test that stubs every collaborator would assert the stub, not the code | 006–013, 040 | none (permanent) |
| The `Application/Services/*` orchestration services (run/stage/export/upload/tts/translation/cost/review/retention pipelines) | Aggregate orchestration over a `DbContext`; behaviour is asserted end to end by the endpoint integration tests | 006–013, 040A | none (permanent) |

Expiry "none" means the exclusion is a permanent policy statement, not a
waiver: if a file later becomes pure logic it must be added to `BACKEND_SCOPE`
and covered. Any temporary quarantine still requires owner + issue + expiry
per the policy below.

## Closing a gap

1. Run the coverage command for your side (frontend/backend above).
2. Frontend: `node scripts/coverage-gap.mjs` names the below-80 files; add
   specs in the owning feature task's suite (ownership: `docs/test-ownership.md`),
   reusing the MSW taxonomy (`frontend/src/mocks/`) for failure shapes.
3. Backend: `node scripts/coverage-gap.mjs --backend` names the below-80
   in-scope files; add units in `tests/DubbingPlatform.UnitTests` (no
   containers, no network, frozen clocks).
4. Re-run until the gap script output for your area is empty.

## Quarantine and bypass policy

There is no coverage-gate bypass flag. A red gate is closed by adding specs or
by an expiry-tracked quarantine entry per the 046 policy (owner + issue +
expiry date, never a silent retry or a lowered threshold without a tracking
issue). Coverage reports contain file paths + counts only — never source
excerpts, tokens, URLs, media, or transcript content.
