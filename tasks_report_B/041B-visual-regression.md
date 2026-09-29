# Task 041B - Visual Regression

## Status

**BLOCKED** - the matrix is built and 145/155 cells verify green, but 10 cells on
one screen still differ from their committed baseline and I could not eliminate the
cause inside this task. Details, with the exact region, are below.

Everything the task asks for exists and runs: 12 screens x 3 breakpoints x 2 themes
x 2 directions, 144 committed baselines, deterministic fixtures, dynamic progress
asserted structurally rather than pixel-compared, and the responsive gap proven
rather than papered over. One screen's cells are not yet stable.

## Summary

Built the visual matrix in `e2e/visual/`, running against the real 040A stack with
deterministic fixtures: one sign-in for the whole run (tokens are in-memory only and
login is rate limited), client-side navigation for the same reason, a content-stability
gate so a capture never lands mid-load, per-screen readiness signals, and a clock
pinned to the seeder's instant. Committing the run surfaced **one production defect**:
`ProgressService.BuildSnapshot` stamped its snapshot with `DateTimeOffset.UtcNow`, so
every workspace read reported "Updated *now*" even when nothing had changed, and the
response was unreproducible for any consumer that diffs it. Fixed with three tests.
Also measured that the application has **no responsive layout at all** (zero breakpoint
classes across 71 feature components), so R4's mobile-list-vs-desktop-editor rule has
nothing to assert - recorded as a pinned finding that fails when the app improves.

## Files Created/Modified

### New - the deliverable

| File | Purpose |
| --- | --- |
| `e2e/visual/support/matrix.ts` | The matrix as data: 12 screens, 3 breakpoints, 2 themes, 2 directions, per-screen readiness signal and dynamic surfaces. `buildMatrix()` expands it and carries N/A reasons. |
| `e2e/visual/support/session.ts` | One sign-in (retrying through the rate limit), in-app navigation, theme/locale switching via the shell's own controls, `waitForContentStable`, and the shell-less path for the login screen. |
| `e2e/visual/screens.spec.ts` | The 144 matrix cells plus 6 axis/coverage guards. |
| `e2e/visual/progress.structural.spec.ts` | R3: progress surfaces asserted structurally - role, accessible name, `aria-valuenow` within range, and state as text. |
| `e2e/visual/responsive-layout.spec.ts` | R4: pins the finding that no responsive layout exists, and fails when one appears. |
| `e2e/visual/README.md` | Matrix, determinism rules, R3/R4 handling, font policy, re-baseline procedure, known gaps. |
| `e2e/visual/__screenshots__/` | 144 committed PNGs, named `<screen>-<breakpoint>-<theme>-<direction>.png`. |

### Production fix

| File | Change |
| --- | --- |
| `src/DubbingPlatform.Application/Services/ProgressService.cs` | `BuildSnapshot` passes `run.UpdatedAt` as `GeneratedAt` instead of `DateTimeOffset.UtcNow`. |
| `tests/DubbingPlatform.UnitTests/Processing/ProgressSnapshotGeneratedAtTests.cs` | **New.** 3 tests: it is the run's `UpdatedAt`; two reads agree; it moves when the run moves. |

### Rig determinism

| File | Change |
| --- | --- |
| `tests/cross-layer/seed/Program.cs` | `DeterministicId(options, label)` - SHA-256 of tenant+project+label - replaces `Guid.NewGuid()` for every seam fixture id, including a per-project overload. The exports screen renders `exp_<32 hex>` as visible text, so those baselines could never match otherwise. |
| `playwright.config.ts` | `testDir` widened to the repo root with explicit `testMatch` for `tests/cross-layer/**` and `e2e/visual/**`; `snapshotPathTemplate` pins baselines to `e2e/visual/__screenshots__/{arg}{ext}`. |
| `package.json` | `test:visual` and `test:visual:update`. Also scoped `test:cross-layer` to `--grep=@cross-layer` so the two suites cannot be run into each other. |

## Decisions Made

**1. The residual is reported, not hidden, and the status is BLOCKED.** `npm run
test:visual:update` passes 155/155 repeatedly, so baseline *generation* is
deterministic. `npm run test:visual` passes 145/155 with 10 failures, all on the
`projects` screen. A 41-pixel and then a 2576-pixel difference confined to one
project-table row is not something to re-baseline away: that is the exact failure
mode this gate exists to catch, and blessing it would destroy its value.

**2. `/projects/{id}/workspace` does not exist.** The workspace is the Overview at
`/projects/{id}` (`ProjectDetailsPage`). The path I first wrote matches no child
route and rendered a bare tab bar - the first baseline set was twelve empty screens.
Found by probing rendered text per screen, not by trusting the label.

**3. The review screen is the cross-project queue at `/review`, not the project
tab.** `/projects/{id}/review` is registered but paints no panel on a seeded
project. Pinning it would have committed twelve baselines of an empty region, which
is worse than no gate because it looks green.

**4. `app-shell` is not a valid readiness signal, and I guarded against it.** The
shell stays mounted across route changes, so waiting for it returned immediately and
the capture could happen before the panel fetched - the workspace assertion read an
empty page as a result. Every screen now waits for its own root, and a test fails if
any screen regresses to `app-shell`.

**5. A capture waits for the content to *stop changing*, and fails loudly if it
never does.** `waitForContentStable` polls a fingerprint of visible testids and their
text lengths until stable for 600 ms. The workspace mounts its root and then fills in
cost, config and activity panels, so capturing on mount was a coin flip. A baseline of
a still-changing page is worse than no baseline, because it fails later at a random
moment and gets blamed on something else.

**6. Error blocks are masked, generally.** Error states render the request's
correlation id (`Ref: <uuid>`), which differs every request by design, so a screen
*correctly* showing an error could never match a baseline. Masking
`[data-testid$="-error"]` keeps the error state's wording and layout comparable
without freezing a random uuid into a committed image. This is the same trade R3
makes for progress, and it is why the transcript screen is pinnable at all.

**7. The sign-in retries through the rate limit.** Playwright restarts the worker
after a test failure and re-runs `beforeAll`, so one early failure burned the 5/min
login budget and the run reported "117 did not run" - a single flaky cell presented
as a suite that was 70% dead. With backoff the full 155 now run every time.

**8. The rig's fixture ids are derived, not random.** This is the determinism Task
046 was meant to own; deriving ids from stable labels means the rig no longer waits
on 046 to be reproducible. The 040A/040B seams read ids from the seed snapshot, so
they are unaffected - verified, 24/24.

## Build/Test Results

### The production defect, before and after

Before - the workspace rendered live wall-clock time while the database held the
seeded instant:

```
DB     dubbing_projects.updated_at = 2026-01-15 12:00:00+00
API    GET /workspace -> progress.updatedAt = 2026-09-29T07:38:13.1883243+00:00
UI     "Updated Sep 29, 2026, 7:36 AM"
```

`src/.../ProgressService.cs:140` passed `DateTimeOffset.UtcNow` into the snapshot, and
`WorkspaceService.cs:195` surfaces it as `progress.updatedAt`, which the UI renders as
"Updated ...". Two reads of unchanged data disagreed.

After:

```
$ (two consecutive reads of GET /workspace)
progress.updatedAt = 2026-01-15T12:00:00+00:00
second read      = 2026-01-15T12:00:00+00:00
stable = True

$ dotnet test --filter FullyQualifiedName~ProgressSnapshotGeneratedAtTests
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3
```

### The matrix

```
$ npm run test:visual:update        # baseline generation, run 4 times
155 passed (6.9m)
155 passed (6.2m)

$ npm run test:visual               # verification against committed baselines
10 failed
145 passed (7.7m)
```

The 10 failures are all `projects`: desktop (4), tablet (4), mobile light-ltr and
mobile dark-ltr. The two `projects` mobile RTL cells pass.

Located precisely, by differencing the committed baseline against the failing
capture in a canvas and asking the page what is at those coordinates:

```
$ node imgdiff.mjs <baseline> <actual>
{ "width": 1440, "height": 900,
  "diffPixels": 2576,
  "box": { "x": 377, "y": 494, "w": 116, "h": 89 },
  "rowsTouched": 78 }

$ (document.elementFromPoint(430, 535))
td  -> "Cross Layer Pipeline"
tr  -> "Cross Layer Pipeline" + a status badge
tbody > table > div, inside the project list
```

One project-table row. The strongest hypothesis, which I did **not** have budget to
confirm: the two seeded projects ("Cross Layer Pilot" and "Cross Layer Pipeline", both
from 040B) share the seeder's single `created_at`, so whichever row the database
returns first is not stable, and the row's badge/progress cell moves. Giving the
pipeline project a distinct `created_at` is the obvious next experiment.

### Regression on the existing gates

```
$ dotnet test tests/DubbingPlatform.UnitTests
Passed!  - Failed: 0, Passed: 3001, Skipped: 0, Total: 3001     (2998 + 3 new)

$ npx playwright test --grep="@cross-layer"
  24 passed (50.4s)

$ npm run test:tools
13 pass / 0 fail
```

The 040A/040B seams are unaffected by the seeder's deterministic ids - they read ids
from the seed snapshot, so they follow the change rather than depending on it.

## Recommendations for Next Agent (041C)

### One bounded task stands between this and COMPLETED

**Make the `projects` screen's row stable, then re-baseline that screen only.**

1. Give the pipeline project a distinct `created_at` in
   `tests/cross-layer/seed/Program.cs` (it currently takes the same `now` as the
   pilot project), so a tie in the list ordering cannot resolve differently between
   runs. Both projects also share `updated_at`; change both.
2. Re-verify with `npx playwright test --grep="@visual projects"`. If it is still
   red, capture two `projects-desktop-light-ltr` screenshots in one run and diff them
   the same way - the `imgdiff` approach in this report localises it in seconds and
   is far cheaper than reading the spec.
3. Only once it is stable: `npx playwright test --grep="@visual projects"
   --update-snapshots`, and commit the 12 files with a stated reason.
4. Then run `npm run test:visual` twice. A green matrix must be green twice -
   the whole class of bug here was "passes when generated, differs when verified".

Do **not** re-baseline `projects` without fixing the cause. That is the one action
this gate exists to prevent.

### Current repo state

- `main` is this task's commit on top of 041A's. `master-prompt.md` is the only
  uncommitted file (a pre-existing scratch file, deliberately left).
- `e2e/visual/` is complete: 144 baselines, 155 tests, `npm run test:visual`.
- 145/155 verify green. Generation is deterministic across four runs.
- 040A/040B cross-layer 24/24; backend unit 3001/3001.
- The rig cannot run the pipeline (no `ffmpeg`/`ffprobe` in the workers), so project
  screens pin seeded/empty states. That is a true baseline of what a user sees, not
  of a completed pipeline - do not read it as one.

### Gotchas that will cost you a run each

1. **Never `localhost`** - always `127.0.0.1`. Docker Desktop resets IPv6 to
   published ports.
2. **Run the seeder before hand-probing the API.** `compose up -d` alone gives a
   0-table database and `500 INTERNAL_ERROR` with `42P01: relation "tenant_users"
   does not exist`. Looks alarming, is not a defect.
3. **Never query the DOM at `domcontentloaded`** on this SPA - React has not
   mounted.
4. **`process.exit()` after a `fetch` crashes Node on Windows** (`UV_HANDLE_CLOSING`,
   `0xC0000409`) *after* printing. Set `process.exitCode` and return.
5. **The frontend container bind-mounts `frontend/dist` read-only**, so
   `npm --prefix frontend run build -- --mode cross-layer` is enough - no image
   rebuild. The API image *does* need `up -d --build api`.
6. **`page.goto` is a logout.** Tokens are in-memory only; use
   `navigateInApp` (`history.pushState` + `popstate`, which React Router resolves).
7. **Playwright slugs baseline names** to hyphens, so the filename is
   `projects-desktop-light-ltr.png`, not space-separated.
8. **Playwright writes `test-failed-1.png` at 1280x720** (its own failure shot) next
   to `*-actual.png` at the test's viewport. Diffing the wrong one looks like a
   catastrophic size mismatch. Use `*-actual.png` vs `*-expected.png`.

### Findings other tasks should pick up

- **The transcript screen renders an error state** while both endpoints it depends
  on return 200: `GET /segments` -> 200 with the seeded segment, and
  `GET /segments/{id}` -> 200 with `selectedTranscriptVersionId` populated. The page
  shows "Transcript unavailable / An unexpected error occurred / Ref: <correlation
  id>", which maps to `INTERNAL_ERROR` in `normalizeError.ts`. This is a real
  client-side defect, not a rig problem, and it is undiagnosed. It is currently
  masked by the general error-block mask, so it does not make the matrix red - which
  is exactly why it needs reporting rather than a green tick.
- **No responsive layout exists** (proven in `responsive-layout.spec.ts`): zero
  breakpoint utilities across 71 feature components. R4 could not be implemented, and
  041C's accessibility work should assume a single fixed layout rather than three
  breakpoints.
- **`ar` translations are partial** - `ar/nav.json` has only `dashboard` and
  `projects`, so RTL baselines pin a mirrored layout with mostly English text. The
  mirroring, which is the point of the axis, is fully covered.
- **Login is fixed and working** (041A), so 041C/041D can drive authenticated
  screens.
- **Task 023 still owns the upload P0** (041A's blocker): the bundle models a
  one-shot upload; the API implements two-phase multipart. Task 046 is still
  unowned; per-**worker** isolation is outstanding (`workers: 1` serialises rather
  than isolates). Integration suite: 38 failed / 14 passed, pre-existing.
- No log scrubbing and no CI wiring (042B). `--update-snapshots` must never run in
  CI; the script exists for local use only.

### Naming and config conventions

- Specs: `e2e/visual/screens.spec.ts` (matrix), `progress.structural.spec.ts` (R3),
  `responsive-layout.spec.ts` (R4). Tag `@visual`.
- `npm run test:visual` verifies; `npm run test:visual:update` re-baselines (local
  only, and a review event - see the README).
- A new screen needs a `SCREENS` entry in `support/matrix.ts`; the coverage test then
  enforces the full 12-cell matrix for it.
- N/A cells live in `SCREENS[].notApplicable` with a reason and are skipped at
  runtime with that reason - never deleted.
