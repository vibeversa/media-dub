# Task 042A - Basic CI Early Gate (Typecheck, Lint, Unit, Build)

## Status

**COMPLETED.** `.github/workflows/basic-ci.yml` exists, runs on every
`pull_request` and every `push` to `main`, fails closed on TS errors, lint
errors (at zero warnings), test failures, build failures, compiler warnings,
skipped tests, and an untagged container dependency — and reproduces locally
command for command. The `Validation` block's six commands all pass verbatim
(`dotnet build` 0 warnings, 3001 unit tests, 1587 frontend tests, typecheck,
lint, build).

The one thing I could **not** do is what the task's Testing section asks for
first: open a test PR. `gh` is not installed on this host and no `origin` is
configured (a pre-existing condition, recorded in 042's report too), and `act`
is not installed either. I substituted the strongest hermetic equivalent —
four gate classes driven to red on an injected fault and back to green on the
fix — and the substitution is listed in "Decisions Made" §6 and in Findings §2.
`scripts/workflow-lint.sh` reports **0 findings** across all five workflow files.

**One cost is accepted deliberately and is not hidden:** this workflow repeats
restore/build/unit and lint/typecheck/test/build that `ci.yml` already runs.
That is ~7 minutes of duplicated runner time per PR. It buys an early
red/green that arrives in minutes instead of ~90. `docs/ci.md` §6 states the
cost, why the duplication cannot simply be deleted (the contract gate needs a
pull-request ref), and the three conditions under which it should be retired.

## Summary

The task asked for a minimal always-on gate that runs before the full
contract/security/perf CI exists. It does — and the interesting engineering was
not the workflow file, it was the one failure mode the task names explicitly.
`backend-basic` runs with no container runtime, so a container-backed test in
the unit tier does not **fail**, it is **skipped**; `dotnet test` exits 0 and the
job is green having proved nothing. That is enforced as a source-level rule
(`tools/unit-tier-containers.mjs`, 28 unit tests) rather than at runtime,
because a runtime check cannot see a test that never started. While building it
I found the same class of hole in a second place: `dotnet test --filter
FullyQualifiedName~UnitTests` runs against the whole solution, prints *"No test
matches the given testcase filter"* for the other three test projects, and
**still exits 0** — so a renamed namespace or an emptied project is
indistinguishable from a pass. That is now `UNIT_TIER_EMPTY`. The pin story is
`global.json` and a new `.nvmrc`, read by the workflows rather than repeated in
them, so the local `Validation` block and CI cannot disagree about which
toolchain ran.

## Files Created/Modified

### Created

| File | Purpose |
| --- | --- |
| `.github/workflows/basic-ci.yml` | The gate. `push`→`main` + `pull_request` + `workflow_dispatch`; jobs `backend-basic` and `frontend-basic`; `permissions: contents: read`; no `paths:` filter; no `continue-on-error`; no retry. |
| `tools/unit-tier-containers.mjs` | The fail-closed container rule. Pure rules (`stripCommentsPreservingLines`, `findTypeScopes`, `findContainerDependencies`, `countTestAttributes`, `findMaySkipSites`, `classifyFile`, `evaluateUnitTier`) plus a CLI that is the file's only I/O and only runs as the process entry point. |
| `tools/unit-tier-containers.test.mjs` | 28 tests over those rules, including the repository's own unit tier as a fixture. Picked up by `npm run test:tools`. |
| `docs/ci.md` | The entry-point page: what runs, the toolchain pins, exact local reproduction, the container rule, the warnings policy, the audit decision, the overlap with `ci.yml` and its sunset condition, a measured timing baseline, and a troubleshooting table. |
| `.nvmrc` | The Node pin (`24`), read by `setup-node` via `node-version-file`. |

### Modified

| File | Change |
| --- | --- |
| `docs/ci-branch-protection.md` | §1 is now **five** required checks (the three from 042 plus the two from this task) with a migration note; new §2 subsection for the `Basic CI / backend-basic` reason vocabulary; the three new reasons added to the §5 "may never be bypassed" list; §7 tool-pin table now points at `.nvmrc` and records the two-file Node pin; §9 gains the early gate's local commands; §1.1 records that `basic-ci.yml` is a standalone subscriber and is *not* the double-run the aggregator rule forbids. |
| `.github/CODEOWNERS` | Owns `basic-ci.yml`, both `unit-tier-containers` files, `global.json`, `.nvmrc` and `docs/ci.md` under `@CHANGE_ME/platform`. |
| `package.json` | One script: `check:unit-containers` → `node tools/unit-tier-containers.mjs`. |

Nothing owned by 042 was changed. `ci.yml`, `backend.yml`, `frontend.yml`,
`contract.yml`, `scripts/workflow-lint.sh`, `scripts/contract-canary.sh` and
`tools/trx-assert.mjs` are byte-identical to how 042 left them — verified by
`scripts/contract-canary.sh` still passing 15/15 and `workflow-lint` still
reporting 0 findings.

## Decisions Made

1. **`basic-ci.yml` gets its own `pull_request` trigger even though `ci.yml`
   already has one, and the duplication is accepted.** The task specifies the
   triggers; 042's design says one subscriber; both cannot hold. I chose the
   literal instruction plus a documented cost over deleting 042's coverage,
   because the alternatives are worse: dropping `pull_request` from `ci.yml`
   would stop the contract gate gating merges (it compares `main` against the
   PR head — it is meaningless on a push), and the two jobs have distinct check
   names so there is no merge block, only runner minutes. The two run in
   parallel, so elapsed time to first verdict *improves*. `docs/ci.md` §6
   states the cost and the three sunset conditions; §1.2 of the branch-protection
   doc says plainly that this is not the double-run the aggregator rule forbids.

2. **The container rule is a source check, not a runtime check.** A runtime
   assertion cannot see a test that never started, and the failure mode *is* a
   test that never started. The rule therefore runs before `dotnet test`, costs
   0.4 s, and its reason is the exact string the task names:
   `TESTCONTAINERS_REQUIRED_BUT_UNAVAILABLE`. The runtime half
   (`tools/trx-assert.mjs --forbid-skipped` over the TRX) is kept *as well*,
   because it catches a `[SkippableFact]` that skipped for a reason no source
   rule can see.

3. **`Skip.If(…)` is reported, not failed.** The task's rule is about
   containers. This repository's `MediaValidationTests.Probe_Real_Files_Via_Ffprobe`
   uses `Skip.If` for an **ffmpeg** probe, not Docker. Treating any `Skip.If` as
   a container dependency would have been red on the tree it guards. Those sites
   are printed as `ADVISORY` lines and the job stays green; `--forbid-skipped`
   is what makes an actual skip red. The workflow therefore **installs ffmpeg
   explicitly** rather than inheriting it from the runner image, so the job's
   verdict does not depend on an image detail nobody reviews.

4. **`npm audit` is advisory and does not use `continue-on-error`.** The task
   asks for a non-blocking annotation. `continue-on-error: true` would have
   required a `docs/ci-quarantine.md` entry (an owned, expiring, issue-linked
   suppression) for something that is *not* a suppression — a step that reports
   and exits 0 by construction. So the step captures the exit code, writes the
   report to `$GITHUB_STEP_SUMMARY`, and distinguishes **three** outcomes:
   clean, advisories found, and *could not run*. That last one matters: a gate
   whose silence would mean "nothing" is a gap, not an advisory.

5. **The two deltas from the task's `Validation` block, and why they are not a
   violation of R4.** CI uses
   `dotnet test --filter "FullyQualifiedName~UnitTests&Category!=Integration" --logger … --results-directory …`.
   The `Category!=Integration` clause is the half of the tag that actually
   excludes a test from this tier (R3 says tagged tests must be "excluded
   here"); it matches nothing today, so the two filters select an identical set
   today and the clause is what keeps that true. `--logger`/`--results-directory`
   only redirect where results are written so the skip assertion can read them.
   Neither changes *which* tests run or the pass/fail verdict. Both were run
   verbatim and pass; `docs/ci.md` §3 gives the exact CI command and states the
   two deltas. Every other command is character-for-character the Validation
   block's.

6. **The Testing requirement was met hermetically, not by a test PR.** The task
   says "validated by opening a test PR (or `act -j backend-basic` …)". Neither
   was available: `gh` is absent, no `origin` is configured, `act` is not
   installed. I did the equivalent for the only logic in this task that *is*
   logic — the container rule — and for the two frontend gates. All four classes
   are in "Build/Test Results". A test PR remains unrun and is the first thing
   the next agent should do once a remote exists.

7. **No `scripts/*.sh` driver for the new gate, against the repo's own
   convention.** The repo's convention is a pure `tools/*.mjs` plus a bash
   driver. I wrote the bash driver first and **it failed on the development
   host**: the `bash` on `PATH` here is WSL2, which does not carry the Windows
   Node install on its `PATH`, so a bash script that shells out to `node` passes
   in CI and fails on the machine that wrote it. Node does this I/O natively and
   identically on every platform, so there was nothing for the shell to
   contribute; the driver was deleted and the CLI is self-sufficient. This is
   the same class of problem 042's report records three times (npm spawn,
   `$TMPDIR`, `mktemp -d`). The pure-rules-plus-unit-tests half of the convention
   is kept intact.

8. **The exemption is resolved per dependency, not per file — because the tests
   caught the per-file version.** See Findings §1. Tagging one `Integration`
   class in a file would have exempted every other class in it.

9. **`basic-ci.yml` runs `scripts/workflow-lint.sh`.** A malformed workflow is
   the most basic CI defect there is, it costs 16 s, and this is the always-on
   gate. It also means the new file is checked by the same repository it checks.
   It does *not* run `quarantine-check.sh`: that registry is about 042B's
   suppressed gate, and this workflow has no `continue-on-error` to suppress.

10. **Coverage thresholds are not run here.** The `Validation` block says
    `npm run test`, so basic CI runs `npm run test` with no `--coverage`. The
    80/80/80/80 thresholds in `frontend/vite.config.ts` are 042B's
    (`CI / frontend` passes `--coverage`). Duplicating them here would add a
    coverage run to the early gate for a floor that has not moved since 039B.

## Build/Test Results

### The task's `Validation` block, verbatim

```
$ dotnet build
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:07.69
EXIT=0
```

```
$ dotnet test --filter FullyQualifiedName~UnitTests
No test matches the given testcase filter `FullyQualifiedName~UnitTests` in
  …\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll
Passed!  - Failed:     0, Passed:  3001, Skipped:     0, Total:  3001, Duration: 29 s
          - DubbingPlatform.UnitTests.dll (net10.0)
EXIT=0
```

(The three `No test matches` lines are the *discovery* of the problem in
Decisions 2 and Finding 3 — one per non-unit test project, and the command
still exits 0.)

```
$ npm run typecheck --prefix frontend
> dubbing-frontend@0.1.0 typecheck
> tsc --noEmit -p tsconfig.json
EXIT=0

$ npm run lint --prefix frontend
> dubbing-frontend@0.1.0 lint
> eslint . --max-warnings=0
EXIT=0

$ npm run test --prefix frontend
 Test Files  144 passed (144)
      Tests  1587 passed (1587)
   Duration  147.87s
EXIT=0

$ npm run build --prefix frontend
dist/assets/index-nXvDviEu.js                 459.49 kB │ gzip: 138.34 kB
✓ built in 5.89s
EXIT=0
```

### The exact `backend-basic` test command, and the skipped-test assertion

```
$ dotnet test --filter "FullyQualifiedName~UnitTests&Category!=Integration" \
    --logger "trx;LogFileName=basic-ci-unit.trx" --results-directory $env:TEMP\basic-ci-trx
Passed!  - Failed:     0, Passed:  3001, Skipped:     0, Total:  3001, Duration: 32 s
          - DubbingPlatform.UnitTests.dll (net10.0)
TEST_EXIT=0

$ node tools/trx-assert.mjs $env:TEMP\basic-ci-trx --forbid-skipped
trx-assert: 1 TRX file(s), 3001 passed, 0 failed, 0 skipped, 0 other
CI_GATE_RESULT reason=OK status=PASS
TRX_EXIT=0
```

### The new gate, and the repository's existing gates

```
$ npm run check:unit-containers
unit-tier-containers: 75 file(s) under tests/DubbingPlatform.UnitTests, 1412 test method(s) declared, 0 untagged container dependency(ies), 0 correctly tagged.
  ADVISORY …/Media/MediaValidationTests.cs:160: can report Skipped, but is not a container dependency: Skip.If(true, $"Temp dir unavailable: {ex.Message}");
  ADVISORY …/Media/MediaValidationTests.cs:169: can report Skipped, but is not a container dependency: Skip.If(true, "ffmpeg/ffprobe missing: ffprobe -version failed. Install ffmpeg to run media tests.");
  ADVISORY …/Media/MediaValidationTests.cs:182: can report Skipped, but is not a container dependency: Skip.If(true, $"ffmpeg fixture generation failed (exit {gen.ExitCode}). Install a full ffmpeg build with libx264/aac.");
  Not a container dependency, so it does not fail this gate. A test that actually reports Skipped fails the job:
  the workflow asserts it over the TRX with tools/trx-assert.mjs --forbid-skipped.
CI_GATE_RESULT reason=OK status=PASS files=75 tests=1412
EXIT=0

$ npm run test:tools            # 77 pre-existing + 28 new
ℹ tests 105
ℹ pass 105
ℹ fail 0
EXIT=0

$ bash scripts/workflow-lint.sh
workflow-lint: actionlint is not installed; running the structural pass only. …
workflow-lint: 5 workflow file(s), 0 finding(s)
EXIT=0

$ bash scripts/quarantine-check.sh
quarantine-check: 1 entr(ies), 0 problem(s), today=2026-09-30, max window=14 days
quarantine-check: every entry is owned, tracked, in window and unexpired.
CI_GATE_RESULT reason=OK status=PASS
EXIT=0

$ bash scripts/contract-canary.sh          # 042's gate, unchanged, with basic-ci.yml present
canary ok     aggregator-wiring   ci.yml calls all three pipelines, all three declare workflow_call, and none also subscribes directly
== canary result: 15 passed, 0 failed ==
EXIT=0
```

### Red on injected failure, green on fix

This is the task's Testing requirement, done hermetically. Nothing was committed
and the tree is clean afterwards.

| Class | Injected fault | Result | After fix |
| --- | --- | --- | --- |
| container, untagged | `using Testcontainers.PostgreSql;` + `new PostgreSqlBuilder(...)` in `tests/DubbingPlatform.UnitTests` | `CI_GATE_RESULT reason=TESTCONTAINERS_REQUIRED_BUT_UNAVAILABLE status=FAIL`, **exit 1**, two `::error` lines naming file, line and type | — |
| container, tagged | the same file with `[Trait("Category", "Integration")]` on the type | `CI_GATE_RESULT reason=OK status=PASS files=2 tests=2`, **exit 0** | — |
| empty tier | a class whose `[Fact]`s were renamed away | `CI_GATE_RESULT reason=UNIT_TIER_EMPTY status=FAIL`, **exit 1** | — |
| unreadable tier | `--project tests/DubbingPlatform.DoesNotExist` | `CI_GATE_RESULT reason=UNIT_TIER_UNREADABLE status=FAIL`, **exit 2** | — |
| TS type error | `export const canaryProbe: number = 'not a number';` in `frontend/src` | `src/__basic_ci_canary__.ts(1,14): error TS2322: Type 'string' is not assignable to type 'number'.` — **exit 2** | removed → `tsc --noEmit` **exit 0** |
| lint error | `const x: any = 1;` in `frontend/src` | `2:12 error Unexpected any. Specify a different type  @typescript-eslint/no-explicit-any` / `✖ 1 problem (1 error, 0 warnings)` — **exit 1** | removed → `eslint . --max-warnings=0` **exit 0** |

The container classes were driven against a *copy* of the tier in a temp
directory (`--project`), so the working tree was never modified. The two
frontend classes used a temporary untracked file, removed in a `finally`.

### Timing baseline (measured, `docs/ci.md` §7)

```
dotnet restore (warm cache)                   4.4s
dotnet build --no-restore                    10.4s
unit-tier-containers                          0.4s
dotnet test --filter … (3001)                29.0s
npm ci --prefix frontend (warm cache)       176.4s
npm run typecheck --prefix                   17.3s
npm run lint --prefix                        12.1s
npm run test --prefix frontend              199.0s
npm run build --prefix                       30.7s
npm audit --prefix --omit=dev                 6.3s
bash scripts/workflow-lint.sh                15.9s
```

`backend-basic` ≈ **1 min**; `frontend-basic` ≈ **7 min 20 s**, dominated by
`npm ci` and `npm run test`. Both run concurrently.

## Findings

Each of these was found by running the thing, and each would have produced a
green gate that meant nothing.

### 1. A per-file trait check would have exempted untagged classes in the same file

My first `classifyFile` asked *"does any type in this file carry
`[Trait("Category", "Integration")]`?"*. A test written to pin the opposite
(`a Trait on the second of two types does not exempt the first`) failed against
it. The consequence in production: add one container test to a file, tag it,
and every *other* class in that file becomes exempt — including the untagged one
that would have been skipped forever, with nothing ever reporting it. The
exemption is now resolved per dependency, against the type the dependency sits
in; a `using` directive, which is genuinely file-scoped, falls back to the
file. Also fixed in the same pass: a `[Trait(` written across four lines was
discarded entirely (the attribute run was cleared on the closing bracket), and
two stacked one-line attributes overwrote rather than joined.

### 2. `bash` on this host is WSL2 and has no `node` — a bash driver passed in CI and failed locally

`bash scripts/unit-tier-containers.sh` printed
`::error title=UNIT_TIER::node is required to read the unit tier.` while
`node --version` worked in PowerShell. The `bash` first on `PATH` is
`/bin/bash` under WSL2, whose `PATH` does not carry
`/mnt/c/Program Files/nodejs`. A gate that works in CI and fails on the machine
that wrote it is the worst possible split: only the people who would have
reported it ever see it. This is the fourth instance of the pattern 042's report
records. Resolution in Decisions 7: the CLI is self-sufficient Node and the bash
driver was deleted.

(Consequence for the next agent on this host: run the repo's bash scripts with
`C:\Program Files\Git\bin\bash.exe -lc '…'`. WSL bash can run
`workflow-lint.sh` and `quarantine-check.sh` — they only need `python3` — but
**not** `contract-canary.sh`, which shells out to `node`.)

### 3. `dotnet test --filter FullyQualifiedName~UnitTests` exits 0 when it matched nothing anywhere

Measured on this repository. The command runs against the whole solution; the
three non-unit test projects each print
`No test matches the given testcase filter …` and the command **exits 0**. So
the Validation command named in the task is a green no-op in three of its four
projects, and a namespace rename that stopped the filter matching would be
indistinguishable from 3001 passing tests. `UNIT_TIER_EMPTY` closes it, from the
source, before the tests run. This is the reason the task's `--filter` is
carried into CI verbatim *and* paired with a check that the filter can match
something.

### 4. This repository's unit tier already contains a `Skip.If` that is not a container

`MediaValidationTests.Probe_Real_Files_Via_Ffprobe` probes for **ffprobe** and
reports `Skipped` when it is absent, from the *unit* project. So:

- a rule that treated any `Skip.If` as a container dependency would be red on
  correct code on day one (see Decisions 3);
- and the unit tier's greenness depends on `ffmpeg` being installed, which
  `ubuntu-latest` happens to provide. `basic-ci.yml` installs it explicitly so
  the verdict does not rest on a runner-image detail, and
  `trx-assert --forbid-skipped` turns the regression into a red job if that ever
  changes. This is pre-existing (inherited from 039's media tests), not
  introduced here.

### 5. "container" is ordinary English in this codebase

`AllowedContainers`, `probe.Container`, `result.Container`, `outcomes.Container`,
and doc comments reading *"no container, no network"* all appear in
`tests/DubbingPlatform.UnitTests/Media/MediaValidationTests.cs` and
`Options/OptionsValidationTests.cs`. A grep-based rule would have failed the
build on a dozen correct lines, and a gate that is red on the tree it guards
gets disabled within a week. Hence: code-shaped markers only, and comments
blanked before matching (preserving line numbers, and tracking strings so a
`//` in a URL does not eat the rest of the line). Both are pinned by tests.

## Recommendations for Next Agent (043)

### Repo state

- `main` carries this task. `master-prompt.md` is modified and uncommitted — a
  pre-existing scratch file, deliberately not committed (042 did the same).
- Green: `dotnet build` 0 warnings, 3001 backend unit tests, 1587 frontend
  tests, typecheck, lint, `vite build`, `npm run test:tools` **105/105**
  (77 from 042 + 28 new), `check:unit-containers` PASS, `workflow-lint` 0
  findings across 5 workflows, `quarantine-check` PASS, `contract-canary`
  **15/15**.
- Still red **by design and pre-existing**: `scripts/contract-snapshot.sh`
  (quarantined as `API_CONTRACT_DIVERGENCE`, issue `#422` is still a
  placeholder) and the 042 blocking audit (8 HIGH/CRITICAL advisories). Do not
  "fix" either by suppressing it.
- **Branch protection is not configured.** It is a repository setting.
  `docs/ci-branch-protection.md` §1.1 is the checklist, and it now lists
  **five** required checks, not three. If the five are never added, the entire
  early gate is advisory, which is the gap this task exists to close.

### Read these before you touch CI

1. **Five required checks, two workflows on `pull_request`.** That is
   deliberate — `docs/ci.md` §6 and `docs/ci-branch-protection.md` §1.2 explain
   it, including why `ci.yml` cannot simply lose its `pull_request` trigger (the
   contract gate compares `main` against the PR head; it is meaningless on a
   push). Do not merge the two workflows without reading §6's three sunset
   conditions first.
2. **`basic-ci.yml` is a standalone subscriber, not a `workflow_call` target.**
   It must NOT be given `workflow_call`, and it must not be given a
   `paths:` filter. The three 042B pipelines are the ones `contract-canary.sh`
   class 15 protects.
3. **`continue-on-error: true` is a quarantine.** It needs a
   `# QUARANTINE reason=<R> EXPIRY: <date>` marker in the step above it and a
   row in `docs/ci-quarantine.md`, or `scripts/quarantine-check.sh` fails the
   build — and fails the day the date passes. The advisory `npm audit` step
   deliberately avoids the key by exiting 0 in the script instead.
4. **`${{ }}` in a `run:` block must arrive through `env:`.** PR titles, bodies
   and branch names are attacker-controlled; `workflow-lint` fails the unsafe
   form.
5. **`--update-snapshots` must never appear in a CI job**, and there is no
   retry anywhere in `basic-ci.yml` either. A flake is quarantined per 046's
   policy with an owner and an issue.

### Naming and config conventions

- `tools/*.test.mjs` is picked up by `npm run test:tools`
  (`node --test tools/*.test.mjs`). A new gate's rules go in a pure
  `tools/*.mjs` with its tests there; the decision layer is pure and the I/O is
  in the CLI at the bottom of the same file, entered only via
  `import.meta.url === pathToFileURL(process.argv[1]).href` (see
  `unit-tier-containers.mjs` for why this one is not a `scripts/*.sh`).
- Every gate ends with one machine-readable line. This task's:
  `CI_GATE_RESULT reason=<R> status=<PASS|FAIL> files=<n> tests=<n>`.
  **New reasons — add them to `docs/ci-branch-protection.md` §2 in the same
  commit.** This task added `TESTCONTAINERS_REQUIRED_BUT_UNAVAILABLE`,
  `UNIT_TIER_EMPTY` and `UNIT_TIER_UNREADABLE`, and added all three to the §5
  "may never be bypassed" list.
- **Tool pins are files, not literals.** `global.json` (SDK) and `.nvmrc`
  (Node) are read by `setup-dotnet`'s `global-json-file` and `setup-node`'s
  `node-version-file`. A Node bump must change **both** `.nvmrc` and
  `NODE_VERSION: 24` in `frontend.yml` (5 uses) and `contract.yml` (2 uses) —
  that duplication is a known, recorded debt, not an oversight. Nothing asserts
  the two agree; a `workflow-lint` rule for it would be the natural follow-up.

### Incomplete integration points (mine, explicitly)

- **No test PR was opened and `act` was never run.** `gh` is absent, no
  `origin` is configured, `act` is not installed. `scripts/workflow-lint.sh`
  ran its structural pass only — `actionlint` is not installed here, so **the
  first CI run is the first full actionlint pass.** A PR that shows
  `Basic CI / backend-basic` and `Basic CI / frontend-basic` red on an injected
  fault and green on the fix is the outstanding verification.
- **`--max-warnings=0` in `frontend/package.json`'s `lint` script is not
  asserted anywhere.** Drop it and `npm run lint` keeps exiting 0 on a warning
  and R2 weakens silently. `docs/ci.md` §5 carries a one-line `node -e` check to
  paste into a CI step; adding it as a real step is a two-minute change.
- **`tools/unit-tier-containers.mjs`'s per-type trait scoping is
  line-based.** A container dependency inside a nested local function or a
  lambda inside an untagged class is attributed to the enclosing class, which is
  correct C#, but the implementation does not track brace depth. It is
  sufficient for this repository (one class per file) and would need revisiting
  only if that stops being true.
- **`Skip.If` in the unit tier is advisory, not enforced structurally.** A new
  unit test that skips on a missing host tool passes the source check and is
  caught only at runtime by `--forbid-skipped`. That is deliberate (see
  Decision 3) but it means the structural half of the rule does not cover
  non-container skip sites.

### Open items 043 inherits from 042, that neither this task nor 042 fixed

- **The five HIGH/CRITICAL advisories.** Upgrade `vitest`/`@vitest/coverage-v8`
  (2.1.8 → ≥2.1.9), `postcss`, `vite` and `react-router-dom`.
  `react-router-dom` is the runtime one. As a dependency PR, not a gate change.
- **The contract divergence (`#422` placeholder).** Regenerate the bundle from
  the server document, `make generate-api`, update `OpenApiCoverageTests`,
  delete the registry row and remove the `continue-on-error` in the same commit.
- **The `CHANGE_ME` handles** in `.github/CODEOWNERS` (now 30+ rows including
  this task's) and `deploy/helm/dubbing/values-prod.yaml`.
- **Admission control for unsigned images** — cluster-side, not CI-side.
- **The `--require-post-deploy` release job is not wired.** 043 owns it; the
  exact command is in 042's report.
- **The `@smoke` spec still does not exist**, so
  `deploy/verify.sh --post-deploy` correctly fails with `SMOKE_SPECS_MISSING`.
