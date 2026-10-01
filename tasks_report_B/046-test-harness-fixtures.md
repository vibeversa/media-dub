# Task 046 — Test Harness and Fixtures

## Status

**COMPLETED.** All five instructions implemented, R1–R5 satisfied, and the
task's `Validation` block passes **verbatim** (five commands). 44 frontend
harness tests, 19 E2E harness-smoke tests against a real stack, and 16 backend
fixture tests; the whole frontend suite is **1793/1793** (was 1762 in the 045
report) and `dotnet build` is 0 warnings / 0 errors.

**The finding worth carrying forward is that all five deliverables already
existed in partial, mutually inconsistent form — and the harness smoke, which is
the one file that is allowed to say so, found four real defects in the first
version of every layer it touched.**

## Summary

Task 046 was an "early prerequisite" that never landed, so 039A built the MSW
taxonomy, 040A built a cross-layer rig, and 041B–D each grew their own wait
helpers, all without a shared contract. This task makes the harness executable
rather than emergent: a `TestFixtures` library that three different executables
reference, a frontend harness whose opt-in lifecycle is explicit, an E2E
harness with named reason codes and a tenant-scoped reset, and three smoke files
that prove each layer works. The harness smoke is not a formality — it caught an
unanchored tag regex, a `psql` in the wrong container, `await` on a synchronous
WebCrypto call, and `DELETE FROM` with a comma-separated table list.

## Files Created/Modified

### Created — backend fixtures (R3, R4)

| File | What it is |
| --- | --- |
| `tests/DubbingPlatform.TestFixtures/DubbingPlatform.TestFixtures.csproj` | A **library**, not a test project, so the 040A seeder and the test project can both reference one implementation. |
| `tests/DubbingPlatform.TestFixtures/FixtureIds.cs` | `Derive(seed, label, scope?)`, `DeriveHexHash(seed, label)`. SHA-256-derived, never `Guid.NewGuid()`. |
| `tests/DubbingPlatform.TestFixtures/SyntheticTenants.cs` | `Build(seed, slug?)` + `ReservedEmailDomain` (`fixtures.invalid`). |
| `tests/DubbingPlatform.TestFixtures/SyntheticUsers.cs` | One user per seeded role, `AdminSubject`…`ViewerSubject`, `ServiceRole` (deliberately unseeded), `SyntheticEmail`. |
| `tests/DubbingPlatform.TestFixtures/SyntheticProjects.cs` | The vocabulary: languages, provider name, texts, `ProcessingSettingsJson`, `ExportFixtureBytes`, `ExportCompletenessJson`, `InitialSelectionVersion`. |
| `tests/DubbingPlatform.TestFixtures/SyntheticProjectGraphBuilder.cs` | `BuildGraph` / `BuildStartableGraph` → the fully wired graph, plus `ToProjectRole`. |
| `tests/DubbingPlatform.TestFixtures/SyntheticProjectGraph.cs` | The wired record and `AllRows()` in foreign-key dependency order. |
| `tests/DubbingPlatform.TestFixtures/SyntheticEnvironment.cs` | `BuildForWorker(seed)` → tenant + five users + two projects. `Membership(...)` helper. |
| `tests/DubbingPlatform.TestFixtures/PiiScrubber.cs` | `Scan` / `AssertClean` / `IsReservedAddress` / `Mask`. Eight structural rules; evidence is masked so a report is not a second copy of the secret. |
| `tests/DubbingPlatform.TestFixtures.Tests/DubbingPlatform.TestFixtures.Tests.csproj` | The proofs, in a project `dotnet test` discovers. No database, no Docker. |
| `tests/DubbingPlatform.TestFixtures.Tests/TestFixturesTests.cs` | **16 tests.** R3 isolation/determinism/referential integrity, R4 scrubber, plus the role-list and validator agreements. |

### Created — frontend harness (R1)

| File | What it is |
| --- | --- |
| `frontend/src/test/setup.ts` | The shared Vitest harness. The jsdom `Request` shim, and nothing else — see Finding 4. |
| `frontend/src/mocks/fixtures.ts` | `buildFixtures(seed)` / `fixtureId(seed, label, scope?)` / `buildMe` / `userForRole`. Mirrors the C# factories. |
| `frontend/src/mocks/handlers.spec.ts` | **31 tests.** The harness smoke. |
| `frontend/src/mocks/README.md` | Rewritten: how to write a spec against the harness, the taxonomy table, the named failure modes. |

### Created — E2E harness (R2)

| File | What it is |
| --- | --- |
| `e2e/playwright.config.ts` | `TAGS`, `ALL_TAGS`, `CROSS_ENGINE_TAGS`, `ENGINE_TAG_MATRIX`, `tagGrep()`, and three projects (chromium runs everything; webkit/firefox run the engine subset). |
| `e2e/support/config.ts` | Ports, `HARNESS_IDENTITIES` (one per role), `WORKER_SEEDS`, `seedForWorker`, backoff table, `assertNoRealSecrets()`. |
| `e2e/support/auth.ts` | `signInThroughForm` (default — the path a user takes), `signInThroughApi`, `signOut`, `HARNESS_AUTH_ERROR_CODES`. |
| `e2e/support/reset.ts` | `resetHarness`, `assertStorageEmulatorAvailable` (`STORAGE_EMULATOR_UNAVAILABLE`), `resetDatabaseRows`, `resetStorageObjects`, `ensureStorageBucket`, `readSeededSubjects`. Hand-rolled SigV4. |
| `e2e/support/sse-waits.ts` | `HarnessSseClient` (`waitForEvent`, `waitForPayload`), `parseSseFrame`, `waitForUiSettled`, `waitForScreen`. |
| `e2e/support/smoke.spec.ts` | **19 tests**, two tiers: 13 hermetic contract tests and 6 against the real stack. |
| `e2e/support/quarantine.md` | The policy: 2 failures/50 runs → quarantine with owner + issue + expiry; never a silent retry. |

### Modified

| File | What changed |
| --- | --- |
| `frontend/src/mocks/handlers.ts` | Adds `taxonomyHandler` / `successHandler` / `noContentHandler`, `missingHandlerError` / `incorrectEnvelopeError` / `assertHandlerExists`, and rewrites `coveredTaxonomyIds` honestly. |
| `frontend/src/mocks/server.ts` | Adds `installMocks` / `useMocks` / `resetMocks` / `uninstallMocks` / `mocksInstalled`, and the `baseHandlers` fix behind `resetMocks`. |
| `frontend/src/testSetup.ts` | **Deleted**, moved to `frontend/src/test/setup.ts`. |
| `frontend/vite.config.ts` | `setupFiles` → `./src/test/setup.ts`; coverage exclusion `src/testSetup.ts` → `src/test/**`; a comment on why MSW is not installed globally. |
| `tests/cross-layer/seed/Program.cs` | Seeds the 046 role set from the fixture library; verifies every role landed. `checks` became a `List` so the role set can be appended. |
| `tests/cross-layer/seed/CrossLayerSeed.csproj` | References `DubbingPlatform.TestFixtures`. |
| `tests/cross-layer/harness/assertArtifacts.ts` | Guards the previously-unchecked `undefined` from a `psql` split, so the file typechecks under the widened `tsconfig.json`. |
| `playwright.config.ts` | Adds `e2e/support/**` to `testMatch` and a `chromium` project for it, with an explicit `testIgnore` on `cross-layer-chromium`. |
| `tsconfig.json` | `include` gains `e2e/support/**/*.ts` and `e2e/playwright.config.ts`. |
| `scripts/check-frontend-topology.mjs` | Exempts `test/setup.ts` — **both** spellings, both path forms. See Finding 3. |
| `deploy/frontend/topology.test.mjs` | Asserts all four exemption spellings. |
| `docs/test-ownership.md` | The ownership contract: what the harness is, the table of surfaces, and the two non-negotiable rules. |
| `docs/coverage.md`, `deploy/frontend/README.md` | Path updates for the moved harness. |
| `DubbingPlatform.sln` | Two new projects under the `tests` folder. |

## Decisions Made

1. **The fixtures are a library plus a separate test project, not one test
   project.** R3 says the factories must be usable by "backend + E2E tests",
   which means three executables reference them — and a test project cannot be
   referenced by a console app. `tests/cross-layer/seed/Program.cs` now calls
   `SyntheticUsers.Build`, so the seeder and the harness cannot disagree about
   what a role is.

2. **The seeder was changed, because it had drifted.** 040A seeded one user,
   `cross-layer-owner`. The harness expected `harness-owner`. Running the smoke
   produced `401 INVALID_CREDENTIALS`, which reads as an auth defect rather than
   as a seed that did not do what it said. The seeder now derives the whole role
   set from the fixture library and **verifies each one landed**, so the failure
   is `Seed completed without persisting: missing tenant_user:harness-editor`
   at the seeder instead of a 401 three steps later.

3. **The harness does not install MSW globally.** `frontend/vite.config.ts` sets
   `setupFiles` to the harness and nothing else. ~1760 tests use
   `setInnerFetchForTests` and never reach the network; a global interceptor
   would make an unmocked request in a pure state-machine suite fail for a reason
   unconnected to it. Suites opt in with `installMocks()`, which is greppable.
   The fail-closed `onUnhandledRequest: 'error'` lives there instead, where it
   applies to exactly the suites that asked for a mock server.

4. **`e2e/support/reset.ts` deletes by `tenant_id`, not by truncate.** 040A
   truncates because it owns the database. This harness may share a stack with the
   rig, so a truncate would destroy the rig's seed. The table list is discovered
   from `information_schema` (52 tables today) and each identifier is
   re-validated before it reaches SQL.

5. **`resetHarness`'s preflight runs before the database delete.** Otherwise a
   missing emulator is discovered halfway through, leaving the run half-wiped and
   the failure reading as a data problem.

6. **The harness smoke FAILS when the stack is down; it never skips.** A skipped
   harness smoke is a green job that verified nothing — the exact failure
   `scripts/require-docker.sh` exists to make impossible in the backend tiers.
   Tier 1 (13 hermetic tests) runs anywhere; tier 2 fails with the named reason
   code. `--grep=@smoke/contract` selects tier 1 alone.

7. **The root `playwright.config.ts` gained a `chromium` project for
   `e2e/support/**`.** Task 046's own validation command is
   `npx playwright test --project=chromium e2e/support/smoke.spec.ts`, and
   Playwright resolves the config from the working directory — so without a
   project by that name the documented command fails with "no projects match".
   `e2e/playwright.config.ts` remains the standalone config for running the
   harness against a stack that is not the rig.

8. **SigV4 is hand-rolled rather than pulled from an SDK.** The repository root
   has no AWS SDK dependency, and adding one to run a test helper would put an S3
   client into the dependency surface of a project whose whole point (043A's
   topology gate) is that the browser never sees one.

9. **The frontend fixtures mirror the C# ones rather than being generated from
   them.** The frontend suite runs in jsdom with no API and no database, so it
   cannot call the C# factories. Generating the file from the C# one would put a
   .NET build in the path of `npm run test`. What makes the duplication
   survivable is that both sides are asserted against the same *shape* in their
   own layer.

10. **`coveredTaxonomyIds()` does not inspect the server.** MSW does not expose a
    handler's resolved path, so "which ids are covered" cannot be read back off
    the server. It is derived from `TAXONOMY` and is only sound because
    `handlers.spec.ts` asserts the two lists have equal length. The doc comment
    says so, because the first version looped over the handlers to build a `Set`
    that could not have been anything but `TAXONOMY`'s ids.

11. **The unseeded-subject control asserts "no session issued", not "401".**
    `POST /auth/login` is limited to 5/min per IP and the test above spends five
    of them, so the sixth call can legitimately be answered 429 — which is a
    stronger rejection than 401. Asserting the exact code made the control a test
    of the rate limiter.

## Build/Test Results

### The task's `Validation` block, verbatim

```
$ dotnet build
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:02:13.62
EXIT=0
```

```
$ dotnet test --filter FullyQualifiedName~TestFixturesTests
Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration: 1 s - DubbingPlatform.TestFixtures.Tests.dll (net10.0)
EXIT=0
```

```
$ npm run typecheck --prefix frontend
> tsc --noEmit -p tsconfig.json
EXIT=0
```

```
$ npm run test --prefix frontend -- src/mocks
 ✓ src/mocks/handlers.spec.ts (31 tests) 147ms
 ✓ src/mocks/conformance.spec.ts (13 tests) 118ms

 Test Files  2 passed (2)
      Tests  44 passed (44)
EXIT=0
```

```
$ npx playwright test --project=chromium e2e/support/smoke.spec.ts
cross-layer: identity subject=cross-layer-owner
Running 19 tests using 1 worker

  ✓  14 … › @smoke/@api harness against the real stack › @api reset removes the tenant rows and is idempotent (14.2s)
  ✓  15 … › @smoke/@api harness against the real stack › @api the seeded rows are gone and the harness can restore them (5.0s)
  ✓  16 … › @smoke/@api harness against the real stack › @api seeded login works for every role (998ms)
  ✓  17 … › @smoke/@api harness against the real stack › @api an unseeded subject is rejected rather than signed in (13ms)
  ✓  18 … › @smoke/@api browser sign-in › @api the login form signs in and reaches the shell (1.4m)

  19 passed (2.8m)
EXIT=0
```

### The rest of the frontend suite, and the other gates

```
$ npm run test          # 151 files, 1793 tests  (was 150 / 1762 before 046)
  Test Files  151 passed (151)
       Tests  1793 passed (1793)
    Duration  245.63s
EXIT=0

$ npm run lint --prefix frontend          # --max-warnings=0
EXIT=0

$ npm run check:frontend                  # node --test deploy/frontend/*.test.mjs
1..62
# tests 62
# pass 62
# fail 0
EXIT=0

$ npm run check:frontend-topology
check-frontend-topology: 295 file(s) scanned under frontend/src, 0 finding(s).
FRONTEND_TOPOLOGY_RESULT reason=OK status=PASS files=295 findings=0
EXIT=0

$ node scripts/check-no-hardcoded-copy.mjs
COPY_GATE_RESULT reason=OK status=PASS files=217 findings=939 new=0
EXIT=0

$ npm run test:presence --prefix frontend
PRESENCE_OK:27 areas with specs
EXIT=0

$ npm run check:no-hex --prefix frontend
check-no-hex: no hardcoded hex outside tokens.css.
EXIT=0

$ npm run typecheck:e2e
EXIT=0

$ npm run check:rollout
ROLLOUT_GATE_RESULT reason=OK status=PASS migrations=7 findings=0
EXIT=0

$ npm run check:unit-containers
CI_GATE_RESULT reason=OK status=PASS files=77 tests=1440
EXIT=0

$ npm run check:vite-env
VITE_ENV_AUDIT_RESULT reason=OK status=PASS files=2 keys=15
EXIT=0

$ bash scripts/quarantine-check.sh
quarantine-check: 1 entr(ies), 0 problem(s), today=2026-10-01, max window=14 days
CI_GATE_RESULT reason=OK status=PASS
EXIT=0
```

### Backend — the pre-existing environmental pair, unchanged

```
$ dotnet test tests/DubbingPlatform.UnitTests -c Release
Failed!  - Failed:     2, Passed:  3038, Skipped:     0, Total:  3040, Duration: 44 s
```

The 2 failures are the **pre-existing environmental** pair documented in the
043C, 044 and 045 reports, byte-identical (`Failed: 2, Passed: 3038, Total:
3040`): `MediaValidationTests.Probe_Real_Files_Via_Ffprobe` (`ffprobe` absent)
and `OptionsValidationMatrixTests.AuthOptions_Rejects_NonAbsolute_Authority`.
No `DubbingPlatform.UnitTests` file was modified by this task.

### Cross-layer rig regression check

```
$ npx playwright test --project=cross-layer-chromium --grep="@cross-layer"
  2 failed
  [cross-layer-chromium] › seam-export-download.spec.ts:40  (mc absent in the substitute emulator)
  [cross-layer-chromium] › seam-upload-storage.spec.ts:47   (mc absent in the substitute emulator)
  2 did not run
  20 passed (50.5s)
```

Both failures are the local storage substitution, not a regression — see
"Environment" below. The other 20 rig specs pass, including the 040A harness smoke
and the seeder changes this task made.

## Findings

Every one of these was found by running the thing.

### 1. The seeder and the harness disagreed about what a role is, and the only symptom was a 401

040A seeded exactly one identity, `cross-layer-owner`, because that was all the
rig needed. Task 046 needs one per role. The natural implementation — write the
five users into the harness — produced this on first contact:

```
401   harness-admin … harness-viewer all 200
401   not-a-seeded-subject
```

except that the harness expected `harness-owner` while the seeder produced
`cross-layer-owner`, so **every** signed-in harness spec would have failed with
`401 INVALID_CREDENTIALS` and read as an authorisation defect. The fix is
structural: the seeder now derives the role set from `SyntheticUsers` and
verifies each one is readable before it prints success, so the failure is
`Seed completed without persisting: missing tenant_user:harness-editor` **at the
seeder**. Same failure mode as 045's Finding 8: a violated invariant reported the
wrong thing first.

### 2. `DELETE FROM` does not take a comma-separated table list

The first reset built `DELETE FROM "a", "b", … WHERE tenant_id = …` and PostgreSQL
answered `syntax error at or near ","` with `LINE 1: DELETE FROM "activity_events",
"artifact_parents", …`. It is the most obvious SQL mistake available and it was
made by a module whose whole purpose is to be obviously correct. Now one
statement per table, so a failure names the table it failed on.

Related, and found in the same run: **PostgreSQL's `psql` defaults to the OS
user**, and `docker exec` runs as root, so the first working version failed with
`FATAL: role "root" does not exist` — a message that reads like a misconfigured
database rather than a missing `-U` flag.

And before that: **the first version ran `psql` inside the API container**, on
the reasoning that the API is the service under test. `Dockerfile.api` installs
`curl` for its healthcheck and nothing else, so that container has no `psql` at
all. The rig's own `assertArtifacts.ts` reaches into the *postgres* container for
exactly this reason, and that is where it belongs.

### 3. An exemption that silently matched nothing, in a gate whose entire job is to match things

Moving `src/testSetup.ts` → `src/test/setup.ts` required updating the topology
gate's exemption. The first attempt anchored the new form as
`normalized.endsWith('/test/setup.ts')` — and the scan root is `frontend/src`, so
callers pass `test/setup.ts` **with no leading slash**. The exemption matched
nothing. `deploy/frontend/topology.test.mjs` caught it, because it is the one
place that asserts the exemption list.

This is the third instance of the same defect class in this repository (043C
Finding 1, 045 Finding 1, and now this): **a rule whose evidence for "no
violations" was a rule that had never matched anything.** The exemption now
checks both spellings and both path forms, and the test asserts all four.

### 4. A rule I wrote that could never fire, and deleted before shipping

`frontend/src/test/setup.ts` originally registered an `unhandledRejection`
listener intended to convert an escaped request into a named failure. It had a
counter, `escapedRequests`, that **nothing incremented** — so the rule had never
matched anything and "no unmocked requests" would have been a pass on a codebase
with no requests in it. It is deleted, and the file now says why in its header.
The no-network property is enforced where it can actually be enforced: in
`server.ts`, whose `installMocks()` defaults MSW to `'error'`.

### 5. `resetHandlers()` restores the `setupServer` list, not what `installMocks` added

`installMocks` uses `use()` (it has to — a suite may install, then add more), so
a bare `resetHandlers()` in `afterEach` silently removed the suite's *own*
handlers. The next test failed with:

```
InternalError: [MSW] Cannot bypass a request when using the "error" strategy for
the "onUnhandledRequest" option.
```

which points at MSW's configuration rather than at the harness. `server.ts` now
tracks `baseHandlers` and re-applies them after the reset, and the test proves it
twice — once would not distinguish "restores the base set" from "happens to work
once".

### 6. A "run-less" project cannot be built at all

The first draft had `BuildWithoutRunGraph` for processing-start tests, on the
reasoning that a project with no run can accept a start. `SpeechSegment.Validate`
requires a non-empty `RunId`, so a run-less project cannot carry a segment — and
the segment subtree is most of what a feature spec needs. The distinction that
actually matters is not "has a run" but "has an **active** run": `POST
/processing` filters on `ProcessingController.ActiveStatuses`, so a Completed
anchor is invisible to it. Both variants now carry a Completed anchor and differ
only in project id, and the reason is recorded in the type's doc comment.

### 7. `await crypto.subtle.exportKey(...)` typechecks and silently corrupts the signature

`exportKey` is synchronous. `await` on a non-promise is legal TypeScript, so the
mistake compiled and handed the downstream `importKey` a `CryptoKey` where an
`ArrayBuffer` was expected — every S3 call then failed with
`SignatureDoesNotMatch`, which reads as a credential problem and sends you to
rotate a key that was never wrong. Found by running it, not by reading it.

### 8. An unbounded tag grep silently widened every selection that shared a prefix

`tagGrep(['@a11y'])` produced `/(@a11y)/`, which also matches `@a11y-extra`.
Escaping does not help — `-` is not a regex metacharacter — so the boundary has
to be stated: `/(tags)(?![\w-])/`. The class excludes `-` as well as `\w` because
tags are hyphen-separated and `@perf-extra` is exactly the kind of near-duplicate
that gets added later. Caught by a test that asserts `@a11y` does not match
`@a11y-extra`.

### 9. A destructive test that left the stack unusable for everything after it

The reset test passed on its own and then every login test after it failed,
because reset had deleted the seed and nothing put it back. The fix is not
ordering (which would be fragile across files) but ownership: the spec that
proves reset works is the spec that re-seeds, via 040A's own
`seedCrossLayerEnvironment` — the same operation the global setup performs, not a
second subtly different one. The reset test also asserts the seeded users are
*gone*, because "deleted 0 rows" and "deleted 52" look identical in a log and
only one of them means reset works.

### 10. `frontend/src/mocks/fixtures.ts` and its C# twin are duplicated on purpose

Called out so the next agent does not "fix" it by generating one from the other:
the frontend suite runs in jsdom with no API and no database, so it cannot call
the C# factories, and generating the file from the C# one would put a .NET build
in the path of `npm run test`. What makes the duplication survivable is that both
sides are asserted against the same shape in their own layer.

## Incomplete integration points (mine, explicitly)

- **G1 — the frontend fixture mirrors are hand-transcribed, not generated.** See
  Finding 10. A field added to one side and not the other fails a test in the
  layer that gained it, which is the cheapest available tripwire, but it is a
  tripwire and not a link. **Owner: whoever next changes a fixture shape.**
- **G2 — `PiiScrubber` reads text.** A fixture with a real name in a non-obvious
  encoding, or a screenshot with a face in it, is out of reach of any regex. What
  the rules do cover is asserted mechanically: every fixture field in
  `TestFixturesTests`, and every taxonomy body in `handlers.spec.ts`.
- **G3 — the C# fixtures are not yet used by `IntegrationTests`/`E2ETests`.** The
  library is referenceable and the seeder uses it, but the existing suites still
  build their own rows. **Owner: 039C (backend presence), which 046's contract
  names as the gap-closure task.**
- **G4 — `e2e/playwright.config.ts` has no `globalSetup` of its own.** It runs
  the harness against whatever `E2E_BASE_URL` points at and expects the operator
  to have brought the stack up. That is deliberate for a config whose purpose is
  "point me at a deployed environment", but it means `--config
  e2e/playwright.config.ts` on a fresh checkout fails with a connection error
  rather than a preflight message. **Owner: whoever adds a non-rig target.**
- **G5 — `webkit` and `firefox` collect but have not been executed.** Both
  browsers are configured and their tag subsets are asserted, but this host could
  not install them. The engine split is proven structurally
  (`ENGINE_TAG_MATRIX` + `tagGrep` tests), not empirically.
  **Owner: 041B/041C, who already run a Chromium matrix.**
- **G6 — `Tag` is not a type.** `TAGS` is a `const` object, so a typo in a spec's
  tag string is not a compile error. Making it one needs a `title:
  \`${TAGS.smoke} …\`` template type per spec, which fights Playwright's own
  `test()` typing.

## Environment

- **MinIO is blocked on this host.** `quay.io/minio/minio` is unauthorized on
  every tag, including the one `tests/cross-layer/docker-compose.cross.yml` pins,
  and `minio/minio` on Docker Hub does not exist any more. The rig therefore
  cannot complete `docker compose up` as written here. I verified this against
  every registry I could reach, and substituted `adobe/s3mock:latest` (S3
  compatible) through a **local-only** compose override at
  `/tmp/opencode/docker-compose.local-storage.yml`. **The committed compose file
  is unmodified** — the MinIO pin is 040A's contract and CI can pull it.
  Everything the harness does is unchanged by which S3 implementation answers.
- **Under that substitution, two 040B seams cannot pass**: `seam-upload-storage`
  and `seam-export-download`. `tests/cross-layer/harness/objectStorage.ts` drives
  `mc` inside the minio container and s3mock ships no `mc`. This is an artifact of
  the substitution, not a defect, and both are expected to pass in CI.
- **`/tmp/opencode` is wiped on a server restart**, and this task lost its .NET
  10 SDK and its local override to one mid-run. Reinstall with:
  ```bash
  mkdir -p /tmp/opencode && cd /tmp/opencode
  curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
  bash dotnet-install.sh --channel 10.0 --install-dir /tmp/opencode/dotnet10 --no-path
  export PATH="/tmp/opencode/dotnet10:$PATH"
  ```
  **`--channel 10` fails; `--channel 10.0` works.**
- **A `git stash` interrupted by a restart leaves the work in the stash and the
  tree clean.** `git stash pop` restored it cleanly; check `git stash list`
  before re-running anything.

## Recommendations for Next Agent (047)

### Repo state

- `main` carries … → 043C → 044 → 045 → **this task**. `HEAD` before it was
  `663b269` ("feat(i18n): land the localization, pseudo-locale and RTL
  foundation (Task 045)").
- **New files land under six new paths**; everything else is modified in place:
  - `tests/DubbingPlatform.TestFixtures/` + `tests/DubbingPlatform.TestFixtures.Tests/`
  - `frontend/src/test/`, `frontend/src/mocks/{fixtures.ts,handlers.spec.ts}`
  - `e2e/support/`, `e2e/playwright.config.ts`
- **Green:** `dotnet build` 0/0; `TestFixturesTests` 16/16; frontend typecheck,
  lint, **1793/1793**, coverage, `check:no-hex`, `test:presence` (27 areas),
  `typecheck:e2e`, `check:frontend` (62/62), `check:frontend-topology` (0
  findings), `check:no-hardcoded-copy` (939 baselined, 0 new), `check:rollout`,
  `check:unit-containers`, `check:vite-env`, `quarantine-check`, and the
  **19/19** harness smoke against a real stack.
- **Red and pre-existing — do not "fix" by suppressing:**
  - 2 `DubbingPlatform.UnitTests` failures (`ffprobe` absent;
    `AuthOptions_Rejects_NonAbsolute_Authority`) — byte-identical to 043C/044/045.
  - `tools/npm-audit-gate.test.mjs` → 1 failure (from the 045 report).
  - Every Docker-gated `[SkippableFact]` in `AdminAuthzTests` /
    `AdminSseErrorContractTests` — Testcontainers cannot start PostgreSQL here.
  - `deploy/verify.sh` → `MANIFEST_CHECK_FAILED` (043A Finding 6, still open).
  - `src/features/translation/__tests__/translation.test.tsx:443` fails **once
    in a full-suite run and passes in isolation, both with and without this
    task's changes.** A pre-existing ordering flake, not a 046 regression.

### Conventions to follow

- **A component test that resolves data needs three things, not one:**
  `useAppStore.setSession(...)`, `useAuthStore.setState({ status: 'authenticated',
  accessToken })`, and `setTokenProvider(...)`. See `signIn()` in
  `frontend/src/i18n/pseudo.spec.tsx`.
- **A screen that never resolves looks like a screen that resolved to nothing.**
  Assert on the resolved branch, never only on the absence of the error branch.
- **Never inline an API error envelope in a spec.** Use
  `taxonomyHandler(method, path, id)` from `src/mocks/handlers.ts`.
- **`resetMocks()` in `afterEach`, not only `afterAll`** — see Finding 5.
- **`react-refresh/only-export-components` is an error** (`--max-warnings=0`): a
  function export must live in a `.ts` sibling, which is why `direction.ts` and
  `localePreference.ts` are not `.tsx`.
- **Write new gates' tests in `deploy/frontend/*.test.mjs`** — both
  `npm run check:frontend` and `npm run test:tools` glob it.
- **The two-hand structure** (synthetic input that MUST fire, then the real tree)
  is the repo convention and the reason 043C/044/045/046 each found a gate that
  passed on nothing.

### If you add a role, a tag, or a table

- **A role:** add it to `SyntheticUsers.SeededRoles` + a subject constant, map it
  in `SyntheticProjectGraphBuilder.ToProjectRole`, and add the subject to
  `tests/cross-layer/seed/Program.cs`'s `SubjectForRole`/`DisplayNameForRole`.
  `e2e/support/config.ts` must gain it too, or the E2E role test will not cover
  it. `TestFixturesTests` asserts the list still equals `Roles.All` minus
  `Service`.
- **A tag:** add it to `TAGS` in `e2e/playwright.config.ts`, then decide whether
  it belongs in `CROSS_ENGINE_TAGS`. `smoke.spec.ts` asserts every tag is
  `@`-prefixed and that the webkit/firefox sets are a subset of `ALL_TAGS`.
- **A table:** nothing to do — `resetDatabaseRows` discovers it from
  `information_schema`. If it lacks `tenant_id` it is not reset, which is
  deliberate.

### Names worth knowing

- `SyntheticEnvironments.BuildForWorker(Guid seed)` → `SyntheticEnvironment`
  with `.Tenant`, `.AnchorProject`, `.StartableProject`.
- `SyntheticProjectGraph.AllRows()` → `IReadOnlyList<object>` in FK order.
- `PiiScrubber.AssertClean(text, where)` — throws `PiiScrubber.PiiScrubberException`.
- `installMocks(handlers?, { onUnhandledRequest? })`, `useMocks(...h)`,
  `resetMocks()`, `uninstallMocks()`, `mocksInstalled()`.
- `taxonomyHandler(method, path, id, overrides?)`, `successHandler(method, path,
  data, status?, extra?)`, `noContentHandler(method, path)`,
  `assertHandlerExists(id)`.
- `buildFixtures(seed?)`, `fixtureId(seed, label, scope?)`, `buildMe(fixtures,
  subject?, roles?)`, `userForRole(fixtures, subject)`.
- `resetHarness(tenantId?)`, `assertStorageEmulatorAvailable(timeoutMs?)`,
  `readSeededSubjects(tenantId?)`, `ensureStorageBucket()`.
- `signInThroughForm(context, role, page?)`, `signInThroughApi(request, role)`,
  `signOut(page)`.
- `HarnessSseClient.open(token, projectId)`, `parseSseFrame(frame)`,
  `waitForUiSettled(page, quietMs?, timeoutMs?)`, `waitForScreen(page, testId)`.
- Reason codes: `STORAGE_EMULATOR_UNAVAILABLE`, `STORAGE_RESET_FAILED`,
  `API_UNAVAILABLE`, `DATABASE_RESET_FAILED`, `DOCKER_UNAVAILABLE`,
  `MSW_HANDLER_MISSING`, `MSW_ENVELOPE_INCORRECT`, `HARNESS_REAL_SECRET`.

### Gotchas that cost time

1. The frontend suite needs four env vars or 32 tests fail on
   `VITE_API_BASE_URL: Required`:
   ```bash
   export VITE_API_BASE_URL=http://localhost:5000 VITE_CDN_ORIGIN=http://localhost:5173 \
          VITE_ENVIRONMENT=local VITE_APP_VERSION=0.1.0-dev
   ```
2. `POST /auth/login` is 5/min per IP. A spec signing in more than five times
   needs `SIGN_IN_BACKOFF_MS`, and an assertion on the sixth response must accept
   429 as a rejection.
3. **Compose merges `ports` across `-f` files by appending.** An override needs
   `ports: !override`, or the second bind fails with "port is already allocated"
   and the message points at the host rather than the merge.
4. **MinIO images are blocked here.** See "Environment" above for the substitute
   and the two seams it cannot run.
5. Playwright collects from the **working directory's** config, so a documented
   `npx playwright test --project=chromium e2e/support/smoke.spec.ts` needs that
   project name in the *root* config — which is why it is there.