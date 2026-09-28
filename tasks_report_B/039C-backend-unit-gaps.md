# 039C — Backend Unit Gap Closure

## Status
COMPLETED

## Summary

Closed every backend *unit* coverage gap in the 039C scope, growing
`DubbingPlatform.UnitTests` from 412 to 2990 passing tests (+2578, 33 new
files) with **no production source change** — `git status` shows only new test
files plus edits to `scripts/coverage-gap.mjs` and `docs/coverage.md`. To make
R5 mechanically checkable I extended `scripts/coverage-gap.mjs` with a
`--backend` mode that merges the coverlet payloads, reports lines *and*
branches below 80% for a declared `BACKEND_SCOPE` manifest, and now prints
**zero** `COVERAGE_GAP` lines. Every file outside that manifest is a recorded
exclusion with reason, owner and expiry in `docs/coverage.md` (R1's stated
alternative), and nothing in the new suite needs a container, a network, a
database or a wall clock (R2). No endpoint integration test was duplicated —
the new specs target pure domain/application/policy helpers, and the
integration-owned surfaces are named in the exclusion table (R3).

## Files Created/Modified

### Modified (3) — no production code touched
- `scripts/coverage-gap.mjs` (**modified**) — added the `--backend` mode plus
  the `BACKEND_SCOPE` manifest that defines the 039C contract. Frontend mode is
  byte-for-byte unchanged (`node scripts/coverage-gap.mjs` still prints nothing
  for 039B).
- `docs/coverage.md` (**modified**) — new "039C unit scope" area→path table and
  the "039C intentional exclusions (backend units)" table (reason / owner /
  expiry per file), plus the corrected `tests/**/TestResults` artifact path and
  the two-format reader note.
- `master-prompt.md` — pre-existing scratch file, **not** part of this task and
  intentionally left uncommitted.

### New test files (33, all under `tests/DubbingPlatform.UnitTests/`)
- `StateMachines/StateMachineMatrixTests.cs` — full (from,to) product of all 8
  lifecycle machines, same-state idempotency, manual-retry gate, throw legs.
- `Authorization/PermissionMatrixTests.cs` — `PermissionResolver.Resolve` role
  matrix (Disabled ⇒ empty, TenantAdmin/Service ⇒ `Permissions.All`, Operator
  elevation), `RoleMatrix`/`Roles`/`Permissions`/`AuthPolicies`,
  `ClaimsPrincipalExtensions`, and the `ResolveAsync` cache path via the EF
  InMemory provider already used by `AuthSessionTests`.
- `Middleware/ErrorMappingTests.cs` — exception ⇒ HTTP status + error code,
  correlation-id propagation into the envelope, unknown-exception ⇒ generic
  500, and both correlation middlewares (accept/emit, header echo, absent).
- `Processing/IdempotencyMathTests.cs` — key normalisation, tenant scoping,
  and `IdempotencyRetention` expiry at 0 / exactly-at / ±1 tick / long-expired.
- `Validation/DtoValidatorTests.cs`, `Validation/ProjectProcessingSettingsValidatorTests.cs` — request- and settings-schema validation matrices.
- `Projects/ProjectSettingsGuardTests.cs`, `Projects/ProjectListQueryMatrixTests.cs` — settings-change guard, config-hash determinism, list-query filter/sort/paging incl. the negative-tenant predicate, `PaginatedResult` page math.
- `Segments/SegmentApiExceptionsTests.cs` — every exception type's code, status and exact message.
- `Notifications/NotificationLogicTests.cs` — **dedup** keyed on
  `(TenantId, RecipientUserId, SourceEventId)` asserted against stored rows,
  recipient resolution, unread counters, and the `Notification` entity guards.
- `Diagnostics/DiagnosticsAggregationTests.cs` — lease ages, queue-depth
  percentages with zero total, DLQ/retention counts, provider health,
  worker heartbeat staleness, review-backlog age, and the
  `DiagnosticsAccessChecker` denial that leaks no row data.
- `Activity/ActivityProjectionTests.cs` — event→projection mapping, unknown
  type ⇒ safe default, and the advanced-detail allowlist (forbidden keys dropped).
- `Enrichment/EnrichmentGateTests.cs`, `Sse/SsePolicyTests.cs` — gate decision
  matrix, envelope construction per event type, and the SSE payload policy
  (forbidden keys never reach a frame).
- `Exports/ExportGeneratorMatrixTests.cs`, `Exports/ExportFormatParserMatrixTests.cs` — SRT/VTT/timeline/transcript/translation/speaker/quality output for empty, single, many and negative-time input, escaping, plus format parsing and profile validation.
- `Services/DurationEstimatorMatrixTests.cs`, `Services/GuidUtilityMatrixTests.cs` — boundary math at 0/1/999/1000/60000 ms and overflow/NaN.
- `Security/SecurityHelperMatrixTests.cs`, `Security/TenantGuardMatrixTests.cs` — `SecretPolicy`/`SecretRedactor` classification and `RedisKeys`/`TenantGuard` **tenant-isolation negatives** (A ≠ B, neither a prefix of the other, cross pairs denied both ways).
- `Storage/StorageKeyMatrixTests.cs` — key shape + tenant isolation, `SignedUrlPolicy` expiry boundaries, `SignedUrlService` issue/validate with an explicit `now`.
- `Models/PublicIdParserTests.cs`, `Domain/Identity/PublicIdMapperTests.cs` — encode/decode round trips, all 22 prefixes, malformed bodies, unknown prefix.
- `Options/OptionsValidationMatrixTests.cs` (306 cases), `Options/ProviderEndpointMatrixTests.cs` — every shipped default passes, every rule has an invalid case, and the endpoint allow/deny matrix.
- `Domain/Entities/EntityGuardMatrixTests.cs` — data-driven `Validate()` matrix for 13 entities: every throw branch, 0/1/-1/99/100/`int.MaxValue` boundaries, blank strings, enum-byte rejection, tenant scoping.
- `Domain/Entities/{ProjectStatusProjectionTests,VoicePreviewJobTests}.cs`, `Domain/ValueObjects/ValueObjectMatrixTests.cs`, `Domain/Voice/ConsentGateTests.cs`, `Domain/Exceptions/DomainExceptionTests.cs` — status projection incl. the unknown-enum `DomainException` path, job lifecycle, hash value objects, consent evaluation.
- `Previews/VoicePreviewQuotaConsentTests.cs` — consent-state matrix, quota at
  the boundary (remaining 1 / 0 / negative / unlimited), preview-text and
  voice-id guards including the SSRF-shaped id, and the tenant-isolation
  negative (tenant A's quota never satisfies tenant B).
- `Voices/VoiceApiExceptionsTests.cs`, `Voices/VoiceCompatibilityBranchTests.cs` — voice error contracts plus the compatibility rule set's JSON-coercion matrix (aliases, string-encoded numbers, non-object roots, malformed JSON, wrong value kinds, reason truncation) and the partial selection-conflict envelope.

## Decisions Made
- **The scope is a declared manifest, not "every pure file".** R1 allows a unit
  test *or* a recorded exclusion. `BACKEND_SCOPE` in `scripts/coverage-gap.mjs`
  enumerates the §15.1 areas from the task's instruction 1 plus its Context
  list; anything outside is an exclusion with an owner. The manifest is
  path-based (directories and named files), not a per-file allowlist, so it
  cannot be quietly narrowed to whatever happened to be tested — a newly-pure
  file inside a scoped directory is still reported.
- **Excluded the three preview orchestration services, explicitly.** `MediaPreviewGenerator`,
  `QcEvidenceLinker` and `VoicePreviewService` stamp artifact rows with
  `DbContext.Database.ExecuteSqlRawAsync` and publish through
  `ArtifactService` → `IArtifactStorage`. `ExecuteSqlRaw*` is relational-only
  and the unit project references only `Microsoft.EntityFrameworkCore.InMemory`,
  so the success path is unreachable without adding a provider or a container —
  both forbidden. Per instruction 3 the test is re-tagged `Integration` and
  moves to 006–013/040A. Their pure guards, constants and
  degrade-on-failure contracts *are* unit-tested; only `PreviewAudio.cs` stays
  in scope. I did not add a test that can only assert "the in-memory harness
  cannot run raw SQL".
- **Two coverlet quirks had to be fixed before the number meant anything.**
  (1) The `json` schema exposes branch hits as `Hits`, not `Coverage`; reading
  the wrong field reported every in-scope file at `branches=0.0`. (2) The
  same instrument appears in *every* test project's payload, so summing hits
  double-counted the denominator four times; the merge now takes the **max** per
  instrument. Both were verified by probing known files (in-scope 100%/100%,
  excluded `MediaPreviewGenerator` 45.2%/36.8%, excluded `ProjectsController`
  0%/0%), so an empty report is a real pass, not a scope that matched nothing.
- **Support both collection commands.** The task's validation command
  (`--collect:"XPlat Code Coverage"` with no `--settings`) writes
  `tests/<Project>/TestResults/coverage.cobertura.xml` and no json, and emits
  `filename` **relative to `src/`**. The runsettings command writes
  `tests/TestResults/coverage.json` with absolute paths. `--backend` prefers
  json and falls back to cobertura (re-rooting onto `src/`, and reading
  `condition-coverage="50% (1/2)"` for exact per-line branch totals).
- **R3 by construction, not by review.** Endpoint behaviour is never
  re-asserted: the new specs call pure/static helpers and value objects. The one
  exception is `PermissionResolver.ResolveAsync`, which reaches its cache path
  through the in-process EF InMemory provider already used by the existing
  `AuthSessionTests` — no container, no network — and it asserts caching and
  tenant scoping, not an endpoint contract.
- **Defensive branches are covered, not excluded.** Where a guard is only
  reachable from EF materialization (the 15 entities' private parameterless
  constructors) or is provably dead (`DurationEstimator`'s NaN check, both
  operands are non-zero ints), the file still clears 80% without it; the
  limitation is named in the report rather than papered over.
- **xUnit 2.9.3, not v3.** The unit project pins `xunit 2.9.3`; specs match the
  project. `Assert.Throws<T>(Func<Task>)` and `Record.Exception(Func<Task>)` are
  hard errors on 2.9.3, so a `() => throw …` lambda must be bound to a typed
  `Action` first. `TreatWarningsAsErrors` is on repo-wide, so `xUnit1025`
  (duplicate `InlineData`) fails the build.
- **Two production defects found, deliberately not fixed** (production source is
  outside a test task). Both are pinned by named tests so a future fix shows up
  as a deliberate diff: `ProjectProcessingSettingsValidator.Parse` /
  `HaveSupportedSchemaVersion` throw an uncoded `InvalidOperationException`
  (→ HTTP 500) for a valid-JSON *non-object* root, which violates the task's
  "never a 500-style throw" rule; and `PreviewAudio.BuildWav` throws
  `ArgumentException` past ~134 M ms because the sample count is unclamped,
  breaking the preview lane's "generation never throws, it degrades" contract.
  A third observation: `RoleMatrix.IsAllowed` returns `false` for an
  *undeclared* endpoint even for `Service`, although the doc comment reads
  "Service satisfies every entry" — the tests pin the actual behaviour.

## Build/Test Results

- `dotnet build` (exit 0). Last 10 lines:
  ```
    DubbingPlatform.UnitTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.UnitTests\bin\Debug\net10.0\DubbingPlatform.UnitTests.dll
    DubbingPlatform.E2ETests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.E2ETests\bin\Debug\net10.0\DubbingPlatform.E2ETests.dll
    DubbingPlatform.ContractTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\bin\Debug\net10.0\DubbingPlatform.ContractTests.dll
    DubbingPlatform.IntegrationTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll

  Build succeeded.
      0 Warning(s)
      0 Error(s)

  Time Elapsed 00:00:02.87
  ```
- `dotnet test --filter FullyQualifiedName~UnitTests` (exit 0). Last 10 lines:
  ```
  No test matches the given testcase filter `FullyQualifiedName~UnitTests` in C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.E2ETests\bin\Debug\net10.0\DubbingPlatform.E2ETests.dll
  No test matches the given testcase filter `FullyQualifiedName~UnitTests` in C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\bin\Debug\net10.0\DubbingPlatform.ContractTests.dll


  No test matches the given testcase filter `FullyQualifiedName~UnitTests` in C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll


  Passed!  - Failed:     0, Passed:  2990, Skipped:     0, Total:  2990, Duration: 23 s - DubbingPlatform.UnitTests.dll (net10.0)
  ```
- `dotnet test --filter FullyQualifiedName~UnitTests --collect:"XPlat Code Coverage"` (exit 0). Last 10 lines:
  ```
  Passed!  - Failed:     0, Passed:  2990, Skipped:     0, Total:  2990, Duration: 25 s - DubbingPlatform.UnitTests.dll (net10.0)

  Attachments:
    C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.UnitTests\TestResults\f56f00aa-eb38-4a50-baac-970bbfa165e8\coverage.cobertura.xml
    C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\TestResults\2f2a185c-6201-45f6-a460-c1f4855c8398\coverage.cobertura.xml
    C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.E2ETests\TestResults\08795b1c-ac47-4b3f-9384-46c65c655a3d\coverage.cobertura.xml
    C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\TestResults\68c8d120-7dd6-4539-9b6c-260a2c1bd81c\coverage.cobertura.xml
  ```
- `node scripts/coverage-gap.mjs --backend` → **no output**, exit 0 (R5: zero
  backend-unit gaps). Verified against probe values (in-scope 100%/100%;
  excluded `MediaPreviewGenerator` 45.2%/36.8%; excluded `ProjectsController`
  0%/0%), so the empty result is a real pass.
- `node scripts/coverage-gap.mjs` (frontend, 039B non-regression) → no output,
  exit 0.
- `git status --short` → 33 new test files + `docs/coverage.md` +
  `scripts/coverage-gap.mjs`; **no `src/` file modified** (R5-style separation
  from the frontend side preserved).

## Recommendations for Next Agent (040A)

- **State:** 039C done. Backend unit suite is **2990 tests green** (was 412 at
  039A/039B). `node scripts/coverage-gap.mjs --backend` is **empty**; the
  frontend `node scripts/coverage-gap.mjs` is also empty. Do not expect to
  iterate on either — 040A owns cross-layer seams, 040B integration, 041A–D
  journeys/visual/a11y/perf.
- **Gap script contract (do not regress):** `scripts/coverage-gap.mjs` has two
  modes. Default = frontend (`frontend/coverage/coverage-summary.json`).
  `--backend` = coverlet, reporting **lines and branches** for the
  `BACKEND_SCOPE` manifest. Three things will silently produce a *false pass*
  if broken, so re-verify with a known file after any change:
  (1) coverlet's `json` branch field is `Hits` (not `Coverage`/`Total`);
  (2) the same instrument appears in every test project's payload, so hits are
  merged with **`Math.max`**, never summed, or the denominator is multiplied by
  the project count;
  (3) the plain collector run emits `filename` **relative to `src/`** with no
  `src/` prefix and no `..` — it must be re-rooted, or nothing matches the
  scope and the report is empty for the wrong reason.
- **Coverage artifacts:** `tests/**/TestResults/` and `frontend/coverage/` are
  gitignored and overwritten by every run. `dotnet test … --collect:"XPlat
  Code Coverage"` writes cobertura only; adding `--settings
  tests/coverage.runsettings` adds json under `tests/TestResults`. Delete
  `tests/**/TestResults` before a run you intend to measure, or a stale payload
  from a previous run is merged in.
- **xUnit gotchas that cost time:** the project pins **xunit 2.9.3** (not v3).
  `Assert.Throws<T>(Func<Task>)` and `Record.Exception(Func<Task>)` are hard
  errors — bind the lambda to a typed `Action` first. `TreatWarningsAsErrors`
  is repo-wide, so `xUnit1025` (duplicate `InlineData` in a `[Theory]`) breaks
  the build. Entity constructors validate aggressively, so a "malformed input"
  test must first survive `Validate()`: `DubbingProject` rejects a blank
  `SettingsJson`, non-2-3-letter languages and equal source/target;
  `VoiceProfile` rejects a non-2-3-letter `Language`.
- **Test helpers available:** EF InMemory via
  `Microsoft.EntityFrameworkCore.InMemory` (already referenced; used by
  `AuthSessionTests` and `PermissionMatrixTests` for cache/tenant-scoping
  paths — in-process, no container). `Moq` 4.20.72, `FluentAssertions` 8.10.0,
  `Microsoft.AspNetCore.Mvc.Testing` 10.0.12, `Xunit.SkippableFact` 1.3.12.
  Testcontainers + WireMock are referenced **only** for the integration
  project — using them in `UnitTests` would break R2.
- **Namespace collision to watch:** a new test folder named `Activity/` creates
  the namespace `DubbingPlatform.UnitTests.Activity`, which then shadows a bare
  `Activity` type reference inside other specs (this broke
  `Middleware/ErrorMappingTests.cs` mid-run). Qualify such references.
- **Known pre-existing flake (not introduced by 039C, not fixed):**
  `Diagnostics/DiagnosticsReadTests.cs::StaleLease_RespectsConfiguredTtl_Boundary`
  seeds rows from `DateTimeOffset.UtcNow` and asserts against
  `TimeProvider.System` at a 1-second TTL boundary, so scheduling delay can tip
  it. It passed every run here. Fixing it means capturing `now` once and
  deriving the row timestamps from it — worth doing before it flakes CI.
- **Production defects found and deliberately left unfixed** (own them if you
  touch these files; both have named tests pinning current behaviour so a fix
  shows as a deliberate diff):
  - `Application/Validation/ProjectProcessingSettingsValidator.cs` —
    `Parse`/`HaveSupportedSchemaVersion` throw an uncoded
    `InvalidOperationException` (→ HTTP 500) for a valid-JSON *non-object*
    root (`[]`, `42`, `null`); `HaveValidShape` handles the same input
    correctly. Test:
    `Parse_Throws_An_Uncoded_Exception_For_A_Valid_Json_Non_Object_Root_Known_Gap`.
  - `Application/Previews/PreviewAudio.cs` — `BuildWav` sample count is
    unclamped, so `BuildWav(int.MaxValue)` throws, breaking the preview lane's
    "generation never throws, it degrades" contract. Test:
    `PreviewAudio_Absurd_Durations_Throw_Known_Gap`.
  - `Application/Authorization/RoleMatrix.cs` — `IsAllowed` returns `false` for
    an undeclared endpoint even for `Service`, although the doc comment reads
    "Service satisfies every entry"; the `Service` shortcut sits behind the
    `Endpoints.TryGetValue` lookup. Decide whether it is a universal bypass.
  - `Application/Security/SecretRedactor.cs` — does not redact JSON-quoted keys
    (`"apiKey":"sk-…"` survives) and is not string-idempotent (re-redacting
    `[REDACTED]` appends a stray `]`); `RedisKeys.TenantKey` rejects `' '`,
    `'\n'`, `'\r'` but not `'\t'` despite a "no whitespace" contract.
  - `Api/Sse/SsePayloadPolicy.cs` is an 8-key *denylist*, not an allowlist, so
    keys like `transcript` / `translationText` / `mediaBody` are not matched —
    the call sites are the real boundary. Test:
    `PayloadPolicy_DocumentedResidual_BodyKeysAreNotInTheFrozenList`.
- **`PublicIdMapper.PrefixFor` maps by type *name* `"Job"`, but no `Job` entity
  exists.** `Domain/Identity/PublicIdMapperTests.cs` keeps a private shim class
  named `Job` with a comment; swap it for `typeof(Job)` if the entity lands.
- **Still unowned:** the full 046 harness (Playwright tags, `Synthetic*.cs`
  factories, scrubber tests, quarantine doc) and the backend *numeric* gate.
  There is deliberately no numeric backend threshold today — the per-file 80%
  target lives in `coverage-gap.mjs --backend` plus the `docs/coverage.md`
  tables, mirroring the frontend's report-then-gate shape. Raise it only after
  measuring, and never lower one to green a red gate.
