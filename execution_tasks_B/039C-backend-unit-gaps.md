# Task 039C — Backend Unit Gap Closure

**Required/Optional:** Required
**Complexity:** M

## Goal
Close every missing backend unit test identified by the 039A gap report without duplicating endpoint integration tests.

## Context
Split from oversized Task 039. Endpoint integration already belongs to 006–013; this task owns pure backend unit gaps: status mapping, permissions, formatting, validation, error mapping, timeline calc, duration, query construction, editor transforms, upload retry math, settings validation, selection-version logic support, notification dedup logic, signed-URL policy helpers. No frontend, integration, or E2E work here.

## Starting State
Depends on Tasks 046 (fixtures), 039A (gap list). Endpoint tests from 006–013 are the baseline — this task adds only unit gaps. Task 039 (combined) is superseded.

## Scope
Included: missing xUnit unit tests for domain/application pure logic + policy helpers (audit writer, idempotency store contract, correlation helper, signed-URL policy, consent evaluation) where Plan A does not already own them.
Excluded: test infra (039A), frontend matrices (039B), endpoint integration (006–013), cross-layer seams (040A/B), E2E (041A–D).

## Instructions
1. Run coverage gap outputs (039A) for `src/` and close gaps in: `ProjectStatus` projection mapping, permission evaluation, settings-schema validation (001/007), selection-version atomicity support (003), notification dedup + recipient resolution (002), voice-preview quota/consent evaluation (004/010), diagnostics aggregation math (005), error-code mapping (013), idempotency-key handling, correlation propagation, signed-URL expiry computation, consent revocation evaluation.
2. If Plan A already owns a helper (audit writer, idempotency store, correlation), add only the Plan B extension unit tests and reference the Plan A owner explicitly in code comments — never re-implement the helper.
3. Each test is fast and isolated (no containers, no network); any test needing PG/rabbit/redis is re-tagged `Integration` and moved out of this task into its owning endpoint task or 040.
4. Record intentional exclusions in `docs/coverage.md` with reason + expiry.

## Requirements
- R1: Every pure-logic unit listed in §15.1 has a unit test or a recorded exclusion.
- R2: No test in this task requires containers or network.
- R3: No duplication of endpoint integration already asserted in 006–013.
- R4: Shared helpers (audit/idempotency/correlation/URL/consent) have contract tests or explicit Plan A ownership references.
- R5: 039A reports zero backend-unit gaps on completion.

## Edge Cases and Error Handling
- Logic with time dependence → frozen-clock tests only, never `Thread.Sleep`-based assertions.
- Mapping with unknown enum → tested `unknown → safe default + metric` path, never throw-to-500.
- Precision logic (durations ms, completeness fractions) → boundary tests (0, 1, 99/100, overflow).

## Security and Safety Requirements
- Unit tests assert tenant-scoping predicates on every query-building helper (negative tenant test at unit level where applicable).
- No secrets in test data; scrubber (046) applies.

## Testing
- Added specs: `tests/DubbingPlatform.UnitTests/**/*Tests.cs` (only the missing ones).
- Type: xUnit unit.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~UnitTests
dotnet test --filter FullyQualifiedName~UnitTests --collect:"XPlat Code Coverage"
```

## Completion Criteria
- 039A reports zero backend-unit gaps; all added unit tests pass; supersedes the backend-unit third of Task 039.

## Traceability
- Plan B §15.1. Split from 039; infra in 039A; frontend in 039B.
