# CI and Branch Protection (Task 042, with 042A's early gate)

This file is the contract between the pipelines in `.github/workflows/` and the
repository settings they are supposed to be protected by. It states the exact
check names to require, the exact reasons each gate can emit, the rules for
bypassing them, and the protocol for a red `main`.

Everything here is a **setting you have to turn on**. A workflow file in the
repository is a description of what CI does; branch protection is what makes
merging depend on it. Until the settings below are applied, every gate in this
repository is advisory.

**For the early gate specifically** — what runs, how to reproduce it locally,
the container rule, and why two workflows both subscribe to `pull_request` — see
[`ci.md`](ci.md). That page is the entry point; this one is the contract.

---

## 1. The required checks

There are **five**, produced by two workflows.

`ci.yml` (Task 042B) is the full gate: `CI` is the only one of the two that
calls the three reusable pipelines, so a pull request produces exactly one run
of each. `basic-ci.yml` (Task 042A) is the always-on early gate and carries the
two fast checks.

| Required check name | Workflow | What it decides |
| --- | --- | --- |
| `Basic CI / backend-basic` | `basic-ci.yml` | Does the backend build, and do its unit tests actually run |
| `Basic CI / frontend-basic` | `basic-ci.yml` | Does the frontend typecheck, lint, test and build |
| `CI / contract` | `contract.yml` | May this change to the API surface merge at all |
| `CI / backend` | `backend.yml` | Does the backend build, test, scan, attest and sign |
| `CI / frontend` | `frontend.yml` | Does the frontend lint, typecheck, test, build and ship |

The two `Basic CI` checks are deliberately redundant with parts of the `CI`
checks. The reasoning, the cost and the condition for retiring them are in
[`ci.md`](ci.md) §6 — read that before proposing to merge the two workflows,
because the contract gate needs a pull-request ref and `ci.yml` cannot simply
lose its `pull_request` trigger.

### 1.1 Configure it

Settings → Branches → Branch protection rules → `main`:

- **Require a pull request before merging** — on, 1 approval. This repository has
  a single-author history; the rule is here because the contract gate exists to
  be reviewed, and a rule that cannot be bypassed is what makes it one.
- **Require status checks to pass before merging** — on. Add exactly:
  - `Basic CI / backend-basic`
  - `Basic CI / frontend-basic`
  - `CI / contract`
  - `CI / backend`
  - `CI / frontend`
- **Require branches to be up to date before merging** — on. Without it two PRs
  can each be green against different bases and merge into a broken `main`. The
  contract gate merges the PR head with the current base before comparing, so
  *it* is safe; the other two are not.
- **Require conversation resolution** — on.
- **Do not allow bypassing the above settings** — see §5.
- **Allow force pushes** — off. **Allow deletions** — off.

> **MIGRATION NOTE.** These five names replace the three that were documented
> here when only `ci.yml` existed. If the five are not added, the two
> `Basic CI` checks are advisory and the early gate can be ignored — which is
> precisely the gap Task 042A exists to close. If the three old `CI / …` names
> are left in place *and* the five are added, every PR waits on a check that no
> longer runs.


#### No path filters, anywhere

**No workflow in this repository uses a `paths:` filter on a required check**, and
that is a deliberate rule rather than an oversight.

A filtered check does not report on a PR it does not match. Branch protection then
waits for a status that will never arrive, and the PR is unmergeable — so the
branch ends up with a required check that everyone learns to bypass, which is
worse than not having the check. The failure mode is *silent and permanent*: there
is no red run to notice, only a PR that stopped merging.

So scope decisions are made **inside** the job, where they are cheap, visible and
reversible. The example is the secret scan in `contract.yml`: it compares refs to
decide whether the PR touches `deploy/` or the workflows, and reports either way.
A check that always reports can be a required check; a check that reports
sometimes cannot.

#### The aggregator, and why the pipelines have no triggers of their own

`backend.yml`, `frontend.yml` and `contract.yml` declare **only**
`workflow_call` and `workflow_dispatch`. `ci.yml` is the sole subscriber to
`pull_request`, `push` (branches and `v*` tags) and `workflow_dispatch`.

Both halves of that fail silently rather than loudly, and both are asserted by
`scripts/contract-canary.sh` on every run:

| Mistake | Symptom |
| --- | --- |
| Missing `on: workflow_call` | The aggregator's `uses:` is rejected at parse time. A push to `main` produces **no run of that pipeline at all**. The gate vanishes. |
| A direct `pull_request` trigger *as well* | Every job runs **twice** per PR (roughly 90 extra minutes) and the checks list carries two identically named entries — a merge block nobody can resolve. |

A reusable workflow's own `push`/`pull_request` triggers are **ignored** when it is
called; only the caller's triggers apply. That is why the `v*` tag trigger lives on
`ci.yml` and not on `backend.yml`.

**`basic-ci.yml` is a different case and is not covered by that rule.** It is a
standalone subscriber, not a pipeline that `ci.yml` calls, so it does not
double-run anything: its two jobs exist under those names nowhere else. The
overlap it *does* have is a deliberate, measured one with two of the three
pipelines — argued in [`ci.md`](ci.md) §6, and not the accidental double-run this
rule forbids.

### 1.2 Why the check names are the job names

GitHub's required-check name is `<workflow> / <job name>`, and the job name is
what a rename silently changes. If `contract.yml`'s job `breaking` were renamed
to `breaking-changes`, the required check would be missing **forever** and every
PR would block with a check nobody can find. Renaming a job is therefore a
branch-protection change: update this list in the same commit.

### 1.3 Migration from the Task 40 pipeline

The previous `.github/workflows/ci.yml` ran its own `build + unit`,
`integration + contract`, `e2e smoke` and `container build/scan/sbom/sign/publish`
jobs. Those are replaced, not kept alongside — two workflows pushing the same
eight images under the same tags is a race nobody would notice until a deploy
pulled a half-written layer. **Before merging the first PR that lands this
file**, remove these five names from the required list:

```
CI / build + unit
CI / api-contract + frontend
CI / integration + contract
CI / e2e smoke (FullPipeline)
CI / container build/scan/sbom/sign/publish
```

and add the three above. The `e2e smoke (FullPipeline)` step (a .NET
`FullPipelineTests` run) was not carried forward: the Playwright cross-layer rig
supersedes it and is what `CI / frontend` runs. The .NET E2E project remains in
the solution and can be re-added if it is wanted back.

---

## 2. What each gate can emit

Every gate prints a single machine-readable line and a JSON artifact. A release
job or a dashboard should branch on the reason, never on the prose.

### `CI / contract` — `scripts/openapi-diff.sh`

```
CONTRACT_GATE_RESULT reason=<REASON> status=<PASS|FAIL> exit=<n>
```

| Reason | Exit | Meaning |
| --- | --- | --- |
| `OK` | 0 | No breaking change, and any contract change was version-bumped |
| `CONTRACT_BREAKING` | 1 | A removed/renamed operation, a request field that became required, a tightened request enum, or a tightened auth scope |
| `CONTRACT_VERSION_BUMP_REQUIRED` | 2 | The contract changed and `info.version` did not move |
| `CONTRACT_VERSION_INVALID` | 3 | `info.version` does not parse (`v1`, `v1.2`, `v1.2.3`) |
| `CONTRACT_VERSION_REGRESSED` | 3 | The version moved backwards |
| `CONTRACT_MAJOR_WITHOUT_ROUTE` | 3 | `info.version` moved to a new major while `servers[0].url` still points at the old one |
| `CONTRACT_ROUTE_WITHOUT_MAJOR` | 3 | The route prefix moved while `info.version` stayed on the old major |
| `TOOL_VERSION_DRIFT` | 3 | The diff tool is not the pinned version. **A gate that ran the wrong tool is not a pass** |
| `CHECKSUM_NOT_PINNED` / `CHECKSUM_MISMATCH` | 3 | The pinned diff tool could not be verified, or the download did not match its digest |
| `TOOL_UNAVAILABLE` / `TOOL_FAILED` | 3 | The diff tool could not be obtained, or the comparison did not complete |
| `DOCUMENT_MISSING` / `DOCUMENT_UNREADABLE` | 3 | The base or head document could not be read |
| `CONTRACT_DIFF_UNREADABLE` | 3 | oasdiff did not return its pinned `--format json` output |

**Exit 3 is never a pass.** Every one of those reasons means the gate did not
reach a verdict, and a contract rule that is enforced "when it can run" is not
enforced.

### `CI / backend`

```
CI_GATE_RESULT reason=<REASON> status=<PASS|FAIL>
```

| Reason | Where | Meaning |
| --- | --- | --- |
| `INFRA_UNAVAILABLE` | `scripts/require-docker.sh` | No usable Docker daemon. Integration tests are **not** skipped — the job fails |
| `TEST_SKIPPED` | `tools/trx-assert.mjs` | A test reported `Skipped` instead of running. A missing check, so the job fails |
| `TEST_FAILED` | `tools/trx-assert.mjs` | A test failed |
| `NO_TEST_RESULTS` / `TEST_RESULTS_UNREADABLE` | `tools/trx-assert.mjs` | No TRX, or none parsed. An unverified run is not a passing run |
| `NO_PREVIOUS_RELEASE` / `PREVIOUS_SCHEMA_FAILED` / `CHAIN_FAILED` / `SCHEMA_NOT_ADDITIVE` | `scripts/migration-compat.sh` | The migration chain does not apply on the previous release's schema, or it is not additive |
| *(build / Trivy / SBOM / cosign)* | `dotnet build`, Trivy, Syft, cosign | A warning-as-error, a HIGH/CRITICAL finding, a missing or trivially small SBOM, or a signature that does not verify |

### `Basic CI / backend-basic` — `tools/unit-tier-containers.mjs`

This job runs the unit tier with **no container runtime**, which makes the
container rule a *source* check rather than a runtime one: a container-backed
test in a containerless tier is skipped, not failed, and `dotnet test` exits 0.

| Reason | Exit | Meaning |
| --- | --- | --- |
| `OK` | 0 | No untagged container dependency, and the unit project declares tests |
| `TESTCONTAINERS_REQUIRED_BUT_UNAVAILABLE` | 1 | A container dependency in `tests/DubbingPlatform.UnitTests` sits outside a `[Trait("Category", "Integration")]` type. **It would be skipped, so the job fails instead** |
| `UNIT_TIER_EMPTY` | 1 | The unit project declares no `[Fact]`/`[Theory]`. `dotnet test --filter FullyQualifiedName~…` prints "No test matches the given testcase filter" for every project and still exits 0, so an empty tier is indistinguishable from a passing one |
| `UNIT_TIER_UNREADABLE` | 2 | The project directory does not exist, or holds no readable C#. **A gate that could not read the project has not verified it** |

The runtime half of the same rule is `TEST_SKIPPED` from
`tools/trx-assert.mjs --forbid-skipped`, which catches a `[SkippableFact]` that
skipped for a reason the source check cannot see — in this repository, an ffmpeg
probe. `TEST_SKIPPED` and `NO_TEST_RESULTS` may **never** be bypassed (§5).

The two the task calls out explicitly:

- **Testcontainers unavailable** → `INFRA_UNAVAILABLE`, the job fails. The
  repository's container-backed tests are `[SkippableFact]`, so a runner without
  Docker produces a *green* run that executed nothing; `trx-assert.mjs --forbid-skipped`
  is what converts that back into a failure, and the Docker preflight stops it
  before 20 minutes of test time is spent discovering it.
- **Missing SBOM or signature** → the `The SBOM must exist` step fails on a
  missing or under-1 KB SBOM, and `Sign` + `Verify` are the same job, so a
  publish cannot reach completion unsigned.

### `CI / frontend`

| Reason | Meaning |
| --- | --- |
| `AUDIT_HIGH` | A HIGH or CRITICAL advisory on the committed lockfile. The report names the advisory id, the vulnerable range, the fix, and the resolved owner |
| `AUDIT_UNAVAILABLE` / `AUDIT_UNREADABLE` | `npm audit` did not produce a parseable report. An audit that could not run has not passed |
| `BASELINE_NEEDED` | A screenshot had no committed baseline. A **new screen**, not a regression — see §4 |
| `STACK_UNAVAILABLE` | The cross-layer rig did not become healthy. The container logs are attached |
| *(lint / typecheck / coverage / codegen-drift / Playwright)* | The named gate failed |

### `deploy/tests/hosting.test.sh` (the hosting gate, Task 043)

```
HOSTING_GATE_RESULT reason=<REASON> status=<PASS|FAIL|SKIP> exit=<n> static=<v> docker=<v>
```

| Reason | Exit | Meaning |
| --- | --- | --- |
| `OK` | 0 | Every tier that ran, passed |
| `CONFIG_MISSING` | 1 | A required policy file is absent. The static tier is not optional: a repository with no hosting policy would otherwise report a green hosting gate |
| `CONFIG_INVALID` | 1 | A file exists and violates the policy. Two sources of the same truth disagree, or a required rule is missing |
| `HEADER_ASSERTION_FAILED` | 1 | A response from the running image did not match `deploy/cdn/origin.json`. **Asserted over HTTP, not by reading the config** — `add_header` inheritance depends on which `location` block matched |
| `TOPOLOGY_VIOLATION` | 1 | A `NetworkPolicy` permits a path the topology forbids. The specific finding is in the printed line |
| `IMAGE_BUILD_FAILED` | 1 | `docker build -f Dockerfile.frontend .` failed. **Nothing was deployed**, so the previous release is still serving |
| `HOSTING_INPUT_MISSING` | 1 | A file the gate reads could not be read. An audit that did not read a file has not cleared it |
| `HOSTING_TOOL_UNAVAILABLE` | 1 | `node`, `python3` or the topology analyser is missing. The static tier fails rather than skipping |
| `HOSTING_IMAGE_UNVERIFIED` | 0 | Docker is absent, so the header matrix was **not** verified. `status=SKIP`, and the reason is on the result line |

The last row is the only SKIP, and it is the one place this gate does not fail
closed. The reason is a tool-availability decision (a developer without Docker
Desktop running) rather than a check-availability one — the same distinction
`deploy/verify.sh` draws for `kubectl`. It is a SKIP with a named reason and
`status=SKIP`, never a `PASS`; CI has Docker, so the header matrix must be
verified there before a release.

`tools/hosting-policy.mjs` is the decision layer (pure, 61 unit tests in
`tools/hosting-policy.test.mjs`); this script is the I/O that reads the real
files and starts the real image. The topology analysis is a separate Python
module (`deploy/tests/hosting-topology.py`) rather than a heredoc, because a
Python `SyntaxError` inside a `$( )` produces a traceback the caller then counts
as zero violations — a pass that checked nothing.

### `scripts/vite-env-audit.sh` (the public-config gate, Task 043)

```
VITE_ENV_AUDIT_RESULT reason=<REASON> status=<PASS|FAIL> files=<n> keys=<n>
```

| Reason | Exit | Meaning |
| --- | --- | --- |
| `OK` | 0 | Every `VITE_*` key is allowlisted, no key is secret-shaped, no value is credential material, and (with `--bundle`) the audited values are in the built artefact |
| `SECRET_IN_VITE_ENV` | 1 | A `VITE_*` key or value is credential material: a PEM block, a JWT, a connection string with a password, a broker/storage URI with inline credentials, an AWS key id, a bearer credential, or an allowlisted name that matches `SECRET\|KEY\|TOKEN\|PASSWORD` |
| `VITE_KEY_NOT_ALLOWLISTED` | 1 | A `VITE_*` key is not in `DEPLOY_CONFIG_ALLOWLIST`. Vite inlines it into the public bundle, so it is published |
| `ENV_FILE_UNREADABLE` | 2 | A scanned file could not be read, or has a line that is neither a comment nor `KEY=VALUE`. **A failure, not a skip** |
| `BUNDLE_MISMATCH` | 1 | A value cleared by the env-file rules is not in the built bundle. The build did not consume the file that was audited |
| `NO_BUNDLE` | 1 | `--bundle` was given and the directory has no JavaScript, or the build env file could not be identified |

The allowlist is `DEPLOY_CONFIG_ALLOWLIST` in `frontend/src/config/env.ts`,
duplicated in bash inside `deploy/config-inject.sh` so the injector can run
before a node layer exists. `--allowlist-sync` **fails** when the two diverge;
a comment saying "keep in step" is not a check.

`ENV_FILE_UNREADABLE` and `NO_BUNDLE` may **never** be bypassed (§5).

### `scripts/check-frontend-topology.mjs` (the frontend topology gate, Task 043A)

```
FRONTEND_TOPOLOGY_RESULT reason=<REASON> status=<PASS|FAIL> files=<n> findings=<n>
```

| Reason | Exit | Meaning |
| --- | --- | --- |
| `OK` | 0 | Nothing in `frontend/src` references a datastore, broker, cache, object store or worker, and no absolute URL names a host outside loopback and the IANA-reserved ranges |
| `FRONTEND_TOPOLOGY_VIOLATION` | 1 | A file references infrastructure the browser must not reach. Each finding is printed as `::error::<file>:<line>: …` and names the rule that produced it |
| `FRONTEND_TOPOLOGY_INPUT_MISSING` | 2 | `frontend/src` could not be walked. **A failure, not a skip** — a check that read no files has cleared nothing |

The rules are deliberately narrow, and `deploy/frontend/topology.test.mjs`
asserts that narrowness as well as the rules themselves: an endpoint rule
requires a host after `://` (so `features/exports/types.ts`'s defensive
`lowered.includes('s3://')` is not a finding), the port rule requires a colon
before the digits (so `90000` is not a port), and a `//` preceded by `:` is
read as a URL rather than a comment. A gate that fires on the defensive use of a
string gets disabled, and then it checks nothing.

`FRONTEND_TOPOLOGY_INPUT_MISSING` may **never** be bypassed (§5).

### `deploy/config-inject.sh` (per-environment config injection, Task 043)

```
CONFIG_INJECT_RESULT reason=OK|INVALID status=PASS|FAIL env=<n> release=<tag> openapi=<v>
```

`INVALID` means nothing was written: a secret-shaped allowlisted name, a
non-absolute or non-http(s) origin, an empty required value, a missing release
tag, or an unreadable OpenAPI bundle. `--check` evaluates and writes nothing,
which is the pre-flight form.

### `deploy/verify.sh` (the release gate, run after a rollout)

```
VERIFY_RESULT reason=<REASON> status=<PASS|FAIL> exit=<n>
```

| Reason | Meaning |
| --- | --- |
| `OK` | Every applicable check passed |
| `MANIFEST_CHECK_FAILED` | A structural or contract assertion over `deploy/` failed |
| `DEPLOY_URL_REQUIRED` | `--require-post-deploy` (the release wiring) with no `DEPLOY_URL`. A static check is not a deployed check |
| `HEALTH_FAILED` | `/health/live` or `/health/ready` did not return 200 |
| `OPENAPI_UNREACHABLE` | The deployed API serves no OpenAPI document, or the document has no readable `info.version` |
| `OPENAPI_VERSION_MISMATCH` | The served `info.version` is not the version the release pipeline verified. **This is the check that catches a rollback to the wrong image and a partial rollout** |
| `SMOKE_SPECS_MISSING` | No `@smoke` spec exists, so the release cannot be verified. A release is not verified on the strength of a health check |
| `SMOKE_RUNNER_UNAVAILABLE` | The verification host cannot run Playwright |
| `SMOKE_FAILED` | Health and the version both passed and the smoke suite failed: the deployment is up and is the expected build, and the behaviour is wrong |
| `INPUT_INVALID` | A required interpreter is missing |

Two facts about the design that matter when you read the artifact:

1. **`reason` names why the release failed; `rollbackTriggered` / `rollbackNote`
   name what was done about it.** They are separate fields because they are
   different facts. `SMOKE_FAILED` + a completed rollback is a different
   incident from `SMOKE_FAILED` with no rollback configured, and collapsing them
   makes a dashboard report rollback problems for every kind of failure.
2. **`--require-post-deploy` is what stops a release being reported as verified.**
   The release job sets it. Without `DEPLOY_URL` the static-only run then *fails*
   rather than passing, because a release gate that quietly degrades to a manifest
   check marks releases verified having contacted nothing.

---

## 3. Owners

`.github/CODEOWNERS` is the source of truth for who is assigned a finding. Every
handle in it is currently `CHANGE_ME` — **replace them before enabling the gates
on a real repository.**

Two gates depend on it and neither degrades usefully without it:

- `tools/npm-audit-gate.mjs` resolves the owning team for the lockfile. While the
  handle is `CHANGE_ME` the report prints **`No owner is assigned`** in the job
  summary rather than rendering a placeholder as though it were a team. It does
  not fail: a repository whose owner is not known yet is a normal state, and
  failing would mean the audit job is red before anyone has done anything.
- `gitleaks` findings on a PR touching `deploy/` or the workflows are reviewed by
  whoever owns those paths. `deploy/k8s/` and `deploy/helm/` are owned by platform
  **and** the backend team, deliberately — a manifest change that can put a broken
  API in front of users should not be reviewable by one team alone.

The mapping, in prose, so branch protection can be set without reading YAML:

| Path | Team |
| --- | --- |
| `frontend/src/`, `frontend/e2e/`, `e2e/visual/`, `e2e/a11y/`, `e2e/perf/` | frontend |
| `frontend/package-lock.json` | frontend-platform |
| `src/DubbingPlatform.Api/OpenApi/`, `frontend/src/api/generated/`, the contract gate | api |
| `src/DubbingPlatform.*`, `tests/DubbingPlatform.*` | backend |
| `tests/cross-layer/`, `playwright.config.ts` | frontend-platform + backend |
| `.github/workflows/backend.yml`, `frontend.yml`, `ci.yml`, `scripts/require-docker.sh`, `scripts/workflow-lint.sh` | platform |
| `deploy/k8s/`, `deploy/helm/` | platform + backend |
| `deploy/cdn/`, `deploy/nginx/`, `Dockerfile.frontend` | platform + frontend — a change to the cache classes or the CSP is a change to what a browser enforces, so neither team alone should approve one |
| `deploy/tests/`, `deploy/config-inject.sh`, `scripts/vite-env-audit.sh`, `tools/hosting-policy.mjs` | platform |
| `docs/runbooks/`, `docs/topology.md`, `docs/rollout.md` | platform + backend — a runbook that is wrong about a backend behaviour is a responder acting on it |
| `deploy/observability/`, `deploy/verify.sh`, `deploy/README.md`, `docs/ci-branch-protection.md` | platform |
| `scripts/coverage-gap.mjs`, `scripts/presence-gate.mjs`, `docs/coverage.md` | quality |

---

## 4. Flake quarantine, and the baseline rule

### 4.1 Visual baselines: a missing baseline is not a regression

`scripts/visual-baseline-gate.sh` exists because Playwright reports "no baseline"
and "the baseline does not match" as the same red test, and the correct response
to each is opposite.

`BASELINE_NEEDED` means a **new screen or a new breakpoint**. The procedure:

1. Run `npm run test:visual:update` **locally**. The Chromium build is pinned by
   `package.json`, so a locally produced baseline matches CI.
2. **Open the PNGs.** The point of a baseline is a human deciding that these
   pixels are correct. A baseline generated and committed without anyone looking
   records whatever the code happened to render, which is the opposite of a
   regression test.
3. Commit them in a **separate PR that contains nothing else**. A baseline PR
   that also changes a component is indistinguishable from a regression once
   merged.
4. Re-run the original PR.

`--update-snapshots` must never appear in a CI job. It rewrites the baselines in
the runner's working tree, the job goes green, and the change is discarded with
the runner — so the regression test would never have existed.
`scripts/workflow-lint.sh` fails the build on it.

If a screen was deliberately **removed**, delete its PNGs in the same PR that
removed the screen, along with its entry in `e2e/visual/support/matrix.ts`. An
orphaned baseline is a file nobody reviews and nobody can explain.

### 4.2 The quarantine registry, reviewed weekly

**There is no suppression flag anywhere in these pipelines.** A gate that can be
suppressed is a gate that gets suppressed, and the `docs/coverage.md` policy
already forbids it for coverage.

What exists instead is **`docs/ci-quarantine.md`**: a registry of named, owned,
expiry-dated records of gates that are known-red for a reason that is not this
repository's fault *yet*. It exists so a genuine, already-known defect does not
block every other change while it is fixed — and it is built so it cannot quietly
become permanent.

Every entry carries all five of:

| Field | Rule |
| --- | --- |
| **Gate** | The exact check name, so the entry cannot quietly cover a *different* failure later |
| **Reason** | The exact failure reason the gate emits |
| **Owner** | A team, from §3. Not a person; a person leaves |
| **Issue** | A real tracking issue. An entry without one is a suppression with extra steps |
| **EXPIRY** | A date, at most 14 days out. An entry without an expiry is a permanent skip wearing a temporary label |

Enforced mechanically, not by convention:

- **`scripts/quarantine-check.sh`** — fails when an entry is missing any field, has
  a `CHANGE_ME` owner, has an `EXPIRY` in the past, or spans more than 14 days.
  It also cross-checks the workflows: every `continue-on-error: true` must have a
  `# QUARANTINE reason=<R> EXPIRY: <date>` marker in the step above it, that date
  must not have passed, and `reason=<R>` must be a row in the registry. It runs in
  `backend.yml`'s `build` job, **before** anything it could suppress.
- A `continue-on-error: true` on a gate step is a **quarantine**, not a style
  choice. The `coverage-gap` report steps deliberately have **no**
  `continue-on-error` at all: `docs/coverage.md` states that script exits 0 with
  gaps present, so a non-zero exit there means the report could not be produced
  — which is a real failure a suppression would have hidden.

**Review: weekly**, by the platform team with the quality team, in the PR that
triages it — so the decision has a commit. The review asks one question per entry:
*has the underlying defect been fixed, or has the quarantine simply outlived the
problem?* An entry whose issue is still open at its expiry is **escalated, not
renewed**, and the check fails until then.

The same requirements apply to a `.trivyignore` entry (`EXPIRY:` +
justification, already enforced by that file's own header), to a
`--allow-category` exemption in `tools/trx-assert.mjs`, and to a coverage
threshold change.

#### The one entry that exists today

`API_CONTRACT_DIVERGENCE` — `scripts/contract-snapshot.sh` boots the real API and
compares the committed bundle against the server’s own emitted document. **It
fails, and it is pre-existing**: 041A found that the committed
`src/DubbingPlatform.Api/OpenApi/openapi.v1.json` cannot drive this API, and
deliberately did not fix it, because the bundle is a hand-checked, frozen document
whose regeneration is a 65-endpoint contract revision that also changes the
generated client and the coverage tests. That belongs to the task that owns the
contract, not to the task that adds the gate.

The step **still runs and still fails**; the entry only stops its failure from
blocking every other change. It covers that one reason and nothing else — an
`API_START_FAILED` in the same step is a new regression and is still red, which is
why the workflow’s following step exists and says so.

---

## 5. Admin bypass

**An admin may bypass, and every bypass is recorded.** The mechanism is not a
setting; it is a rule, because GitHub's "do not allow bypassing" cannot be
enforced against a repository admin and pretending otherwise is how the rule gets
ignored.

1. **Write the reason in the PR before bypassing.** Not afterwards. The reason
   names which gate is being bypassed and why it is safe.
2. **One bypass, one gate.** Bypassing `CI / contract` does not carry permission
   to bypass `CI / backend` in the same PR. A contract break and a broken build
   are different conversations and get different reviewers.
3. **An owner from §3 must approve the bypass** in the PR, in a review, not by
   reaction.
4. **The reason is posted to the release channel** by whoever performs it. An
   unannounced bypass is treated as an incident.
5. **Two bypasses of the same gate in one week** goes to the next sprint's
   retrospective. A rule that is used twice in a week is the wrong rule, or the
   wrong gate.

What may **never** be bypassed, by anyone, for any reason:

- `CONTRACT_DIFF_UNREADABLE`, `TOOL_VERSION_DRIFT`, `CHECKSUM_MISMATCH` and every
  other exit-3 reason. Those are not "the gate is red", they are "the gate did
  not run". Bypassing them is indistinguishable from not having the gate.
- `TEST_SKIPPED` / `NO_TEST_RESULTS`. A green job that ran no tests is not a
  result.
- `TESTCONTAINERS_REQUIRED_BUT_UNAVAILABLE` / `UNIT_TIER_EMPTY` /
  `UNIT_TIER_UNREADABLE`. Bypassing these is indistinguishable from not running
  the unit tier at all — which is the failure mode the early gate exists to
  prevent.
- `BASELINE_NEEDED`. A baseline is approved by a human, in a PR, with the PNGs
  in the diff.
- A missing SBOM or an unverified cosign signature.
- `ENV_FILE_UNREADABLE` / `NO_BUNDLE` from `scripts/vite-env-audit.sh`. A gate
  that did not read the file it was auditing has not cleared it, and
  "no bundle found" means the build output is not what was verified.
- `CONFIG_MISSING` / `HOSTING_INPUT_MISSING` from
  `deploy/tests/hosting.test.sh`. A hosting gate that skipped its own policy
  files reports success having read nothing.

`HOSTING_IMAGE_UNVERIFIED` is the one bypassable result, and only in the sense
that a **CI** run cannot produce it: Docker is present on `ubuntu-latest`, so
the header matrix is verified there. A developer's local `SKIP` is not a release
signal and must not be quoted as one.

---

## 6. The red-build protocol

`main` is the branch every release is cut from, so a red `main` is a release
block, not an inconvenience. **Revert first, diagnose after.** The reasoning is
that the failure is already recorded in CI, the artifacts are attached, and the
commit that broke it is in the history — a forward-fix that also has to
re-establish what is broken costs more time than a revert and lands the same
fix later.

### 6.1 Within 1 hour

| Elapsed | Action | Owner |
| --- | --- | --- |
| 0–15 min | Revert the offending commit on `main`. Open an issue with the CI run URL, the failing check name, and the `*_GATE_RESULT` line. | Whoever is on call |
| 15–60 min | Assign the issue to a team from §3. Post the run link in the release channel. | Platform on call |
| 1 h | A named owner is on the issue and the red build has a diagnosis or a bounded next step. | Platform on call |

The 1-hour bound is for **ownership**, not for a fix. A revert is complete in
minutes; the diagnosis is what the clock is measuring.

### 6.2 After the revert

The fix is a **new forward PR**, reviewed normally, with the original issue
linked. The exception is a revert that would itself break a released contract:
if the reverted commit is a breaking API change that already reached a deployed
environment, the forward fix is a new API line, and the rollback is handled by
`deploy/verify.sh`'s `OPENAPI_VERSION_MISMATCH` path and the 043 runbook.

### 6.3 The one case that is not a revert

A failed **post-deploy verification** is not a red `main`; it is a failed
release. `deploy/verify.sh` has already triggered the rollback and recorded the
reason. The protocol there is 043's: confirm the rollback restored the previous
version, confirm `VERIFY_RESULT reason=OK` on the restored version, and only then
diagnose. The reason code tells you which half failed:

- `HEALTH_FAILED` — the rollout or the migration. Check the migration Job; a
  failed migration blocks the rollout by design and must be forward-fixed, never
  rolled back (migrations are additive-only).
- `OPENAPI_VERSION_MISMATCH` — the wrong artefact is deployed, or the rollout is
  partial. Compare the served `info.version` with the bundle at `DEPLOY_TAG`.
- `SMOKE_FAILED` — health and version both passed, so the deployment is up and is
  the expected build, and the *behaviour* is wrong. Do not roll forward on the
  strength of a green health check; the rollback has already happened.

---

## 7. Tool pins

Every external tool whose behaviour a gate depends on is pinned by version **and**
by content digest, and a mismatch is a failure. The rule is uniform because the
failure mode is uniform: a tool can change what a gate *sees* without changing
what the gate *reports*.

| Tool | Pinned where | Version | Verified how |
| --- | --- | --- | --- |
| oasdiff | `scripts/openapi-diff.sh` (`OASDIFF_VERSION` + five `OASDIFF_SHA256_*`) | `v1.11.7` | Archive sha256 checked **before** extraction; `--version` asserted on every run, including for a binary found on `PATH` |
| .NET SDK | `global.json` | `10.0.100`, `rollForward: latestFeature` | `rollForward` is a deliberate choice: a patch moves, a feature band does not. `basic-ci.yml` passes `global-json-file: global.json` to `setup-dotnet`, so there is no second copy of this number to drift |
| Node | `.nvmrc` | `24` | Read by `setup-node` via `node-version-file`. The other workflows declare the same value as `NODE_VERSION: 24`; **a Node bump must change both**, and `.nvmrc` is the file to change first |
| Node for tooling | `package-lock.json` | locked | `npm ci` fails when the manifest and lockfile disagree; `npm install` is never used in CI |
| Node for the app | `frontend/package-lock.json` | locked | Same |
| Playwright | `package.json` | `1.63.0` | Exact, no caret, because the visual baselines are Chromium-version-specific |
| Trivy | `aquasecurity/trivy-action` | `0.28.0` | Action tag |
| Syft | `anchore/sbom-action` | `v0` | Action tag |
| cosign | `sigstore/cosign-installer` | `v3` | Action tag; signing is keyless via OIDC, so there is no key to pin |
| Python + PyYAML | the runner image | preinstalled | Used by `deploy/verify.sh` and `scripts/workflow-lint.sh`; a missing interpreter is a failure, not a skip |

### 7.1 Changing a pin

A pin change is a reviewed diff that carries **the value it replaced** in the
commit message, and for oasdiff the version and every digest move in the same
commit. A digest for a different version is worse than no digest: it installs a
binary nobody audited and calls it verified.

Before changing the oasdiff pin, run the canary:

```bash
bash scripts/contract-canary.sh
```

All 15 classes must still behave as specified. A new oasdiff version that adds a
breaking-change rule will legitimately turn some classes red — that is the tool
telling you it found more, and it is a *reason to read the new findings*, not a
reason to revert the pin without reading them.

The 15 classes are:

| # | Class | Expected |
| --- | --- | --- |
| 1 | the base document against itself | pass, 0 |
| 2 | no change at all | pass, 0 |
| 3 | a documentation-only edit | pass, 0 (no version bump owed) |
| 4 | an additive change **with** a version bump | pass, 0 |
| 5 | an additive change with **no** version bump | fail, 2 |
| 6 | a removed operation | fail, 1 |
| 7 | a request field that became required | fail, 1 |
| 8 | a request field that stopped being required | fail, 2 — no client breaks, so it is not a *breaking* change, but a bump is still owed |
| 9 | a tightened request enum | fail, 1 |
| 10 | a route that now demands a scope it did not | fail, 1 |
| 11 | a new major version with no new route prefix | fail, 2 |
| 12 | a version that moved backwards | fail, 2 |
| 13 | the diff tool at the wrong version | fail, 3 `TOOL_VERSION_DRIFT` |
| 14 | the diff tool unobtainable | fail, 3 `TOOL_UNAVAILABLE` |
| 15 | the aggregator calling the three pipelines | the three are reusable, and none of them also subscribes to `pull_request` |

Class 8 is the one most likely to be "fixed" into expecting 1. That would be
wrong: dropping a required field from a *request* makes the server more
permissive, so no existing client breaks. It is still a contract change and still
owes a version bump, which is why it is exit 2 and not exit 1.

Class 10 is checked by the gate itself rather than by oasdiff, because the pinned
tool does not report it — verified: adding `security: [{Bearer: ["projects.read"]}]`
to an operation that previously inherited the document-level requirement produces
an empty diff from oasdiff. The task names the case explicitly, so the gate does
not depend on the tool noticing.

### 7.2 `OASDIFF_DOWNLOAD_BASE_URL`

An air-gapped runner can point at a mirror that serves the **same** archives:

```bash
OASDIFF_DOWNLOAD_BASE_URL=https://mirror.internal/oasdiff bash scripts/openapi-diff.sh ...
```

The sha256 pin still applies, so a mirror cannot substitute a different binary.
It is not a way to skip verification.

---

## 8. Secrets

**CI uses OIDC only.** There is no cloud credential, no registry PAT, no signing
key and no long-lived token in this repository.

| Secret | What it is | Who uses it |
| --- | --- | --- |
| `GITHUB_TOKEN` | GitHub-issued, per job, scoped by the job's `permissions:` block | artifact upload, SARIF upload, GHCR push, the gitleaks SARIF |

Keyless cosign uses the Actions OIDC token (`id-token: write`) and nothing else.
The `image` jobs in `backend.yml` and `frontend.yml` are the only ones granted
`id-token`, `packages` and `security-events`, and each declares them itself
rather than inheriting them from a top-level block — a job added later without
thinking about it cannot inherit registry write access.

`permissions: write-all` is rejected by `scripts/workflow-lint.sh`, and so is a
workflow with no top-level `permissions:` block at all, because "whatever the
repository default is" is not a decision.

`deploy/verify.sh` needs **no credential to read a health endpoint or the OpenAPI
document** — the API serves both anonymously (`app.MapOpenApi().AllowAnonymous()`).
A release gate that needed a token to check whether the service is up would be a
gate whose failure modes include the token's.

---

## 9. Running the gates locally

```bash
# The always-on early gate (Task 042A). docs/ci.md §3 has the full, exact set.
node tools/unit-tier-containers.mjs       # or: npm run check:unit-containers
npm run test:tools                        # 28 unit tests over the container rule

# Hosting (Task 043): the policy, and then the real image.
npm run test:tools                            # hosting-policy + runbooks-index among the rest
bash deploy/tests/hosting.test.sh              # static tier + header matrix against a running image

# The public configuration (Task 043).
VITE_API_BASE_URL=https://api.example.com VITE_CDN_ORIGIN=https://cdn.example.com \
  bash deploy/config-inject.sh --env staging --release v1.0.0 --check
bash scripts/vite-env-audit.sh --allowlist-sync --bundle frontend/dist

# Contract: the gate's own rules, hermetically.
bash scripts/openapi-diff.sh --self-test

# Contract: one deliberate break per class, each of which must be REJECTED.
# Takes about a minute; downloads the pinned oasdiff if it is not on PATH.
bash scripts/contract-canary.sh

# Contract: the real comparison against main.
bash scripts/openapi-diff.sh --base main --head HEAD

# Workflow definitions: actionlint when installed, the structural pass always.
bash scripts/workflow-lint.sh

# The quarantine registry: an expired suppression fails the build.
bash scripts/quarantine-check.sh
# To prove the expiry rule works, run it as if it were later:
QUARANTINE_TODAY=2030-01-01 bash scripts/quarantine-check.sh

# Structural deploy checks, and the post-deploy release gate.
bash deploy/verify.sh
bash deploy/verify.sh --post-deploy --url https://staging.example.com --tag v1.2.3 \
  --rollback-command 'kubectl -n dubbing-staging rollout undo deploy/api'

# The skipped-test gate, against any recent test run.
node tools/trx-assert.mjs tests/DubbingPlatform.UnitTests/TestResults --forbid-skipped
```

`scripts/verify-stub.mjs` starts a throwaway stand-in for a deployed API
(`/health/live`, `/health/ready`, `/openapi.json`) so the post-deploy gate can be
exercised on a machine with no cluster. It is not part of any suite and is not
wired into CI; it exists so the release gate's failure branches can be tested
before they are needed for real.
