# Basic CI — the always-on early gate

`.github/workflows/basic-ci.yml` is the gate every pull request runs, and the one
whose answer arrives in minutes rather than in an hour. It runs **typecheck,
lint, unit tests and build** for the backend and the frontend, and nothing else.

This page is what that gate does, how to reproduce it exactly on your machine,
and what it deliberately does not do. The full gate — contract drift,
Testcontainers integration, cross-layer E2E, visual/a11y/perf, image
scan/SBOM/sign — is Task 042B and lives in `ci.yml`. For the required-check
names, the reason vocabulary, ownership and the red-`main` protocol, see
[`ci-branch-protection.md`](ci-branch-protection.md).

---

## 1. What runs, and in what order

Two jobs. Both are required; either one failing blocks a merge.

### `Basic CI / backend-basic`

| # | Step | Why it is there |
| --- | --- | --- |
| 1 | checkout | — |
| 2 | `setup-dotnet` from **`global.json`** | the SDK pin is the file the repository already uses |
| 3 | NuGet cache, keyed on `**/*.csproj` + `global.json` + `.config/dotnet-tools.json` | a lockfile change is a cache **miss**, not a stale tree |
| 4 | `dotnet restore` | runs unconditionally; a cache hit never short-circuits it |
| 5 | `dotnet build --warnaserror` | a compiler warning is a failure |
| 6 | `node tools/unit-tier-containers.mjs` | the container rule, **before** the tests (§4) |
| 7 | install ffmpeg | the unit tier probes for ffprobe and skips without it (§4) |
| 8 | `dotnet test --filter "FullyQualifiedName~UnitTests&Category!=Integration"` | the unit tier, with a TRX logger |
| 9 | `node tools/trx-assert.mjs … --forbid-skipped` | a test that skipped is a missing check |
| 10 | `bash scripts/workflow-lint.sh` | a malformed workflow is the most basic CI defect there is |
| 11 | upload the TRX (`if: always()`) | the evidence survives a red build |

### `Basic CI / frontend-basic`

| # | Step | Why it is there |
| --- | --- | --- |
| 1 | checkout | — |
| 2 | `setup-node` from **`.nvmrc`**, `cache: npm` on the lockfile | the Node pin is a file, not a literal in the workflow |
| 3 | `npm ci` | never `npm install`: it fails when the manifest and lockfile disagree |
| 4 | `npm run typecheck` | its own step, so the failure is attributable |
| 5 | `npm run lint` | `eslint . --max-warnings=0`, so a warning is a failure |
| 6 | `npm run test` | vitest |
| 7 | `cp frontend/.env.example frontend/.env` | `.env` is gitignored; without it the build fails inside the env schema |
| 8 | `npm run build` | `prebuild` runs the OpenAPI drift gate first |
| 9 | `npm audit --omit=dev --audit-level=high` | **advisory only** (§6) |

### Fail-closed, everywhere

There is no `continue-on-error` on a gate step and no retry anywhere in this
workflow. A flake is quarantined per [`ci-quarantine.md`](ci-quarantine.md) with
an owner, an issue and an expiry — it is not retried into green. `contents: read`
is the only permission, and neither job references `secrets.` at all.

There is no `paths:` filter, and there must never be one. A filtered required
check does not report on a PR it does not match, so branch protection waits
forever for a status that never arrives. See `ci-branch-protection.md` §1.1.

---

## 2. The pinned toolchain

| Tool | Pin | Read from |
| --- | --- | --- |
| .NET SDK | `10.0.100`, `rollForward: latestFeature` | `global.json` |
| Node | `24` | `.nvmrc` |
| NuGet packages | the committed `*.csproj` graph | `dotnet restore` |
| npm packages | `frontend/package-lock.json` | `npm ci` |

Both pins are **files, not literals in the workflow**, and that is the point. A
literal version in a workflow is a second source of truth: it drifts from the
file, and the drift stays invisible until local `Validation` and CI disagree
about what passed. `setup-dotnet` reads `global.json`; `setup-node` reads
`.nvmrc`. Changing the pin means editing the pin.

`24` is a major track, not an exact patch, and deliberately so: it is the same
value the other workflows declare, and `global.json`'s own `rollForward:
latestFeature` is the same kind of choice. A Node bump is one reviewed diff.

---

## 3. Reproducing it locally

The commands are the same ones CI runs. Run them from the repository root.

```bash
# --- backend-basic ---
dotnet restore DubbingPlatform.sln
dotnet build DubbingPlatform.sln --warnaserror
node tools/unit-tier-containers.mjs

# Required: the unit tier probes for ffprobe and reports `Skipped` without it.
# The step below turns that skip into a failure, so install it.
#   Debian/Ubuntu:  sudo apt-get install -y --no-install-recommends ffmpeg
#   Windows:        winget install --id Gyan.FFmpeg
#   macOS:          brew install ffmpeg
dotnet test --filter "FullyQualifiedName~UnitTests&Category!=Integration" \
  --logger "trx;LogFileName=basic-ci-unit.trx" --results-directory ./test-results
node tools/trx-assert.mjs ./test-results --forbid-skipped
bash scripts/workflow-lint.sh

# --- frontend-basic ---
npm ci --prefix frontend
npm run typecheck --prefix frontend
npm run lint --prefix frontend
npm run test --prefix frontend
cp frontend/.env.example frontend/.env    # skip if you already have one
npm run build --prefix frontend
npm audit --prefix frontend --omit=dev --audit-level=high   # advisory; exit 1 is expected today
```

Or, for the two gates this task added, via npm:

```bash
npm run check:unit-containers     # the container rule, on the real tree
npm run test:tools                # 28 unit tests over the container rule
```

### Two deliberate deltas from the plan's `Validation` block

The `Validation` block in the task is:

```bash
dotnet build
dotnet test --filter FullyQualifiedName~UnitTests
npm run typecheck --prefix frontend
npm run lint --prefix frontend
npm run test --prefix frontend
npm run build --prefix frontend
```

Both were run verbatim and pass. CI uses the commands above, which differ in
exactly two ways, and neither changes *which* tests run:

1. **`&Category!=Integration` is added to the filter.** The tag is only half of
   the exclusion — the `Category` clause is the half that actually keeps a
   tagged test out of this tier. On the tree as it stands today the clause
   matches nothing (no test carries that category in the unit project), so the
   two commands select an identical set; the clause is what makes that stay
   true when somebody adds the first tagged test.
2. **`--logger` / `--results-directory` are added to `dotnet test`.** They
   redirect where results are written so the skipped-test assertion can read
   them. They do not change the outcome.

Run the shorter form locally and you reproduce the same verdict; run the longer
form and you reproduce the job exactly.

---

## 4. The container rule, and why it is a source check

`backend-basic` runs with **no container runtime**. That is the point: an early
gate that starts PostgreSQL and RabbitMQ is not an early gate.

The danger is that a container-backed test in this tier does not **fail**, it is
**skipped**. `dotnet test` exits 0, the job is green, and it has proved nothing.
So the rule is enforced at the source level, before the tests run:

> A container dependency in `tests/DubbingPlatform.UnitTests` must sit inside a
> type carrying `[Trait("Category", "Integration")]`. An untagged one fails the
> job with **`TESTCONTAINERS_REQUIRED_BUT_UNAVAILABLE`**. It is never skipped.

`node tools/unit-tier-containers.mjs` implements it. The rules are pure
functions in the same file and are covered by 28 unit tests
(`tools/unit-tier-containers.test.mjs`, run by `npm run test:tools`); the CLI at
the bottom of the file is the only I/O.

Two things it deliberately does **not** do:

- **It does not match the word "container".** This repository's media tests say
  "no container, no network" in their class summaries and use
  `AllowedContainers` and `result.Container` as real identifiers. A rule that
  matched the word would be red on day one on correct code, and a gate that is
  red on the tree it guards gets disabled. Only code-shaped markers count:
  `using Testcontainers…`, the four `*Builder` types, `IContainer<T>`, and
  `TestFixtureBase`. Comments are blanked before matching, so a doc comment
  saying "no container" is not a container dependency — it is the opposite of
  one.
- **It does not treat `Skip.If(…)` as a container dependency.** This repository
  also probes for ffmpeg that way. Those sites are reported as an **advisory**
  so a test whose result depends on a host tool stays visible, and the job
  stays green; the enforcement of "a skip is not a pass" is the runtime
  `--forbid-skipped` assertion over the TRX, which does fail the job.

There is a second, quieter hole in the same area, and it is closed too:

> If the unit project declares **no** `[Fact]`/`[Theory]` at all, the job fails
> with **`UNIT_TIER_EMPTY`**.

`dotnet test --filter FullyQualifiedName~UnitTests` runs against the whole
solution. The other three test projects print `No test matches the given
testcase filter` and the command still **exits 0** — so a rename that stopped
the filter matching, or an emptied project, is indistinguishable from a pass.
The check runs before the tests, so it costs a second rather than a full unit
run.

If `dotnet test` cannot produce a TRX, or the results cannot be read, that is
exit 2 and a failure too. A gate that could not read the result has not
verified it.

### What the tier currently looks like

```
$ node tools/unit-tier-containers.mjs
unit-tier-containers: 75 file(s) under tests/DubbingPlatform.UnitTests, 1412 test method(s)
  declared, 0 untagged container dependency(ies), 0 correctly tagged.
  ADVISORY …/MediaValidationTests.cs:160: can report Skipped, but is not a container dependency
  ADVISORY …/MediaValidationTests.cs:169: can report Skipped, but is not a container dependency
  ADVISORY …/MediaValidationTests.cs:182: can report Skipped, but is not a container dependency
CI_GATE_RESULT reason=OK status=PASS files=75 tests=1412
```

Those three advisories are all the ffmpeg probe in one test
(`MediaValidationTests.Probe_Real_Files_Via_Ffprobe`). The workflow installs
ffmpeg so they do not fire, and `--forbid-skipped` catches them if it ever is
missing.

Note the `1412` against the `3001` that `dotnet test` reports: 1412 is the
number of `[Fact]`/`[Theory]` **methods**, 3001 is the number of executed test
**cases** after theory expansion. Both are the same tier.

### The rule, proved red

```
$ node tools/unit-tier-containers.mjs --project <a copy with one injected untagged container test>
unit-tier-containers: 2 file(s), 2 test method(s) declared, 1 untagged container dependency(ies), 0 correctly tagged.
  ::error title=UNIT_TIER::…/InjectedContainerTests.cs:1: a Testcontainers using directive at file
    scope, and that scope declares no [Trait("Category", "Integration")]. … an untagged container
    dependency does not fail - it is SKIPPED, and a skipped test is a missing check. …
  ::error title=UNIT_TIER::…/InjectedContainerTests.cs:10: PostgreSqlBuilder (Testcontainers.PostgreSql)
    in type `InjectedContainerTests`, and that scope declares no [Trait("Category", "Integration")]. …
CI_GATE_RESULT reason=TESTCONTAINERS_REQUIRED_BUT_UNAVAILABLE status=FAIL
EXIT=1
```

The same file with the trait added goes green (exit 0), and a project whose
tests were all renamed away gives `UNIT_TIER_EMPTY` (exit 1). A project
directory that does not exist gives `UNIT_TIER_UNREADABLE` (exit 2). All four
are covered in `tools/unit-tier-containers.test.mjs`.


---

## 5. Where the warnings-as-errors policy actually lives

| Gate | Where the strictness is set |
| --- | --- |
| C# compiler warnings | `Directory.Build.props` → `TreatWarningsAsErrors=true` for every project, **and** `-warnaserror` on the CI command line. Both, deliberately. |
| ESLint | `frontend/package.json` → `eslint . --max-warnings=0` |
| TypeScript | `tsc --noEmit` exits non-zero on an error. TypeScript has no warning severity, so there is nothing to escalate — style findings are ESLint's job and ESLint is at zero tolerance. |

If someone drops `--max-warnings=0` from the `lint` script, `npm run lint` keeps
exiting 0 on a warning and R2 silently weakens. Nothing in CI notices. To close
that, assert it — the first line of the change should be:

```bash
node -e "const s=require('./frontend/package.json').scripts;
  if(!s.lint.includes('--max-warnings=0')) { console.error('lint must stay at zero warnings'); process.exit(1); }"
```

---

## 6. The audit is advisory here, and the overlap with `ci.yml`

### `npm audit`

The step in `frontend-basic` is `npm audit --omit=dev --audit-level=high` and it
**cannot fail the job**. The blocking audit is `CI / frontend` in `ci.yml`.

It exits 0 by construction, and that is **not a suppression**: there is no
`continue-on-error` key, so `scripts/quarantine-check.sh` does not treat it as
one and no registry entry is required. The exit code is captured and re-emitted
as an annotation, and the three outcomes are distinguished — clean, advisories
found, and *could not run*. A step whose silence means "nothing" is not an
advisory, it is a gap.

**The `--omit=dev` divergence from the other workflow is deliberate.** `ci.yml`'s
blocking audit does *not* use it, because a dev dependency is still code that
runs in CI with repository access. `react-router-dom` is a runtime dependency
and is covered either way; `vitest` is not, which is exactly why the
authoritative audit is the one that includes it. This annotation is a heads-up,
not the gate, and the comment in the workflow says so at the point of use.

### Why two workflows both subscribe to `pull_request`

`Basic CI / backend-basic` and `Basic CI / frontend-basic` repeat work that
`CI / backend` and `CI / frontend` also do. That is accepted, and the cost is
stated rather than hidden:

- The two run **in parallel** with the full pipeline, so the early red/green
  arrives *sooner* than it would from the full pipeline. Total elapsed time is
  unchanged; runner minutes roughly double for these two jobs.
- They are independent verdicts on different questions, so neither can mask the
  other, and the check names are distinct. There is no duplicated check name
  and therefore no merge block — which is the failure mode
  `ci-branch-protection.md` §1.1 warns about for a workflow that *both*
  subscribes to `pull_request` and is called as a reusable workflow.
  `scripts/contract-canary.sh` class 15 asserts that mistake for the three
  042B pipelines; `basic-ci.yml` is not one of them and does not need to be
  `workflow_call`.

### The sunset condition

Retire this workflow **only** when all three hold:

1. `ci.yml`'s `build`/`unit-tests` and `static`/`build` jobs have run on
   `pull_request` for long enough to be trusted.
2. A measurement exists showing the full pipeline's time-to-first-verdict is
   acceptable on its own. The argument for keeping this file is speed, and an
   argument with no number behind it is not an argument.
3. A plan for the contract gate, which **needs a pull-request ref** — it
   compares `main` against the PR head. `ci.yml` therefore cannot simply lose
   its `pull_request` trigger, or the contract gate stops gating merges. If
   the two are merged into one workflow, the contract job has to keep running
   on the pull request while the heavy jobs move behind it.

Until then the duplication is the price of a signal in minutes, and it is
recorded here so nobody has to rediscover it.

---

## 7. Timing baseline

Measured on the development host (Windows 11, 8 cores, warm NuGet/npm caches,
`obj/` and `bin/` already populated) on 2026-09-30. "Cold CI" is the same
commands on a fresh `ubuntu-latest` runner and is an **estimate** — the first
real run replaces it.

These are the numbers to compare against when this workflow changes. A step
that grows by an order of magnitude is a finding, not a rounding difference.

| Step | Local (measured) | Cold CI (estimate) |
| --- | --- | --- |
| `dotnet restore` | 4 s | 40–70 s |
| `dotnet build --warnaserror` (incremental) | 10 s | 2–4 min |
| `node tools/unit-tier-containers.mjs` | 0.4 s | < 1 s |
| `dotnet test --filter …` (3001 tests) | 29 s | 40–60 s |
| `tools/trx-assert.mjs --forbid-skipped` | < 1 s | < 1 s |
| `bash scripts/workflow-lint.sh` | 16 s | 5–15 s |
| **`backend-basic` total** | **~1 min** | **~5–7 min** |
| `npm ci --prefix frontend` | 176 s | 60–120 s |
| `npm run typecheck` | 17 s | 15–25 s |
| `npm run lint` | 12 s | 10–20 s |
| `npm run test` (1587 tests) | 199 s | 3–4 min |
| `npm run build` (incl. `prebuild` drift check) | 31 s | 30–45 s |
| `npm audit` (advisory) | 6 s | 5–15 s |
| **`frontend-basic` total** | **~7 min 20 s** | **~6–9 min** |

Two jobs, run concurrently, so a pull request gets both answers in roughly the
slower of the two: **~7 minutes locally, ~9 minutes on a cold runner**, against
the ~90 minutes the full `ci.yml` pipeline takes.

The two things worth watching:

- **`npm ci` costs more than the tests it precedes** (176 s against 199 s, and
  CI can flip that ordering on a cold cache). It is the single biggest lever in
  this workflow, and it is why `setup-node`'s npm cache is keyed on
  `frontend/package-lock.json` — a warm lockfile cache is the difference
  between a 3-minute and a 9-minute signal.
- **`npm run test` is over half the frontend job.** The 199 s is mostly
  per-worker jsdom setup, not assertions. If this gate ever needs to be faster,
  that is where the time is, and it is a testing question rather than a CI one.


---

## 8. Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `TESTCONTAINERS_REQUIRED_BUT_UNAVAILABLE` | a container dependency in the unit project outside a `[Trait("Category", "Integration")]` type | tag the enclosing type, or move the test to `tests/DubbingPlatform.IntegrationTests`. The message names the file, the line and the type. |
| `UNIT_TIER_EMPTY` | the unit project declares no `[Fact]`/`[Theory]` — a rename, not a deletion | the `dotnet test` filter stopped matching. Fix the namespace or the filter. |
| `TEST_SKIPPED` | a test reported `Skipped`/`NotExecuted` | read the attached TRX. If it is `ffmpeg missing`, install ffmpeg; the workflow does this for you. |
| `NO_TEST_RESULTS` / exit 2 | no TRX was produced, or none parsed | the run did not happen. Not a skip. |
| `unit-tier-containers: could not read the unit tier` | the project directory does not exist or holds no `.cs` | a failure, not a skip — the gate refuses to report a pass for something it did not read. |
| `workflow-lint` findings in this file | a structural workflow defect | the structural pass always runs; `actionlint` runs too when installed. |

---

## 9. Related

- [`ci-branch-protection.md`](ci-branch-protection.md) — required check names,
  the full reason vocabulary, owners, admin bypass, the red-`main` protocol.
- [`ci-quarantine.md`](ci-quarantine.md) — the only sanctioned way to land a red
  gate.
- `ci.yml`, `backend.yml`, `frontend.yml`, `contract.yml` — Task 042B's full
  gate.
