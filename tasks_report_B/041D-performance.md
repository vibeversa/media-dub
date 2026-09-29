# Task 041D - Performance Budgets

## Status

**COMPLETED.** `npx playwright test --grep="@perf"` passes **27/27** against the
real rig with the real bundle, and **all six budgets are green** with real
headroom: **1,021 / 149 / 119 / 34 / 242 / 164 ms** against limits of
**3,000 / 1,500 / 2,000 / 100 / 300 / 500 ms**.

No budget was lowered, no fixture was shrunk to make a number pass, and there are
**no quarantines**: no run came within 19% of its limit on more than one sample,
so the quarantine path was never exercised by the application. It *is* exercised -
by `budgets.spec.ts`, against a synthetic spike, so the shared-runner rule is
tested code rather than code that only matters on a bad day.

Building the gate found four real measurement failures, each of which would have
produced a green number that meant nothing. They are in "Findings" below with the
evidence.

## Summary

Task 041D is a measurement gate, and almost all of the work is in making the
numbers mean what they claim to. `e2e/perf/budgets.ts` holds the six budgets with
their fixtures, owners, measurement contracts and declared limitations, versioned
with a changelog that `budgets.spec.ts` enforces. Six specs measure them on the
real rig: five serve synthetic corpora at the transport layer (the task's own
security requirement is "perf specs use synthetic fixtures only") and
`workspace-open` runs against the real API. Every number is taken inside the page
against `performance.now()`, verdicts are the median of three, and a breach
attaches a **scrubbed** Playwright trace and opens or updates a perf issue -
which required turning the runner's tracing off for a new, disjoint `perf-chromium`
project so the specs can own the trace lifecycle. A fifth of the file count is the
scrubber: a minimal ZIP reader/writer and an allowlist scrubber that drops every
response body, screencast frame and source blob, reduces headers to seven names,
strips every query string, and then **re-reads its own output** and fails the run
if a bearer token, JWT, presigned parameter or `Authorization` header survived.

## Files Created/Modified

### The deliverable

| File | Purpose |
| --- | --- |
| `e2e/perf/budgets.ts` | **New.** The six budgets, their fixtures, owners, measurement contracts, declared limitations, the numeric bandwidth profile, `BUDGET_VERSION` and `BUDGET_CHANGELOG` |
| `e2e/perf/app-interactive.spec.ts` | **New.** Cold-load budget over a CDP desktop-broadband profile with the HTTP cache disabled |
| `e2e/perf/project-list.spec.ts` | **New.** 200-project tenant render budget |
| `e2e/perf/workspace-open.spec.ts` | **New.** The one budget on the real API |
| `e2e/perf/timeline-interaction.spec.ts` | **New.** 1,200-segment p95, plus the debounced-latency evidence |
| `e2e/perf/segment-search.spec.ts` | **New.** 5,000-segment search budget |
| `e2e/perf/media-seek.spec.ts` | **New.** Seek budget with the seekable-range non-vacuity guard |
| `e2e/perf/practices.spec.ts` | **New.** R4: route-level splitting, list bounds, preview-peaks-not-archival, search debounce, the data-path tripwire, and the recorded dialogue-lane gap |
| `e2e/perf/budgets.spec.ts` | **New.** R5: the registry's own rules, plus tests for the verdict, the ZIP codec, the scrubber and the issue renderer |
| `e2e/perf/README.md` | **New.** The gate, the measurement contract, the scrub policy, the issue policy, findings, known gaps |
| `e2e/perf/fixtures/preview-60s.mkv` | **New.** 100,031-byte synthetic Matroska preview, 60.000 s, VP8 |
| `e2e/perf/fixtures/generate-media.mjs` | **New.** Regenerates it; FFmpeg via an argument array, never a shell |

### Support modules

| File | Purpose |
| --- | --- |
| `e2e/perf/support/session.ts` | **New.** The shared signed-in session (one sign-in per worker), the cold context, the bandwidth profile, the host profile, `openTranscriptTab`/`openTimelineTab` |
| `e2e/perf/support/fixtures.ts` | **New.** Synthetic corpora, the route layer, the request log, per-spec project ids, Range-correct media |
| `e2e/perf/support/probes.ts` | **New.** In-page probes: input-to-paint, open, seek, search settle, count, attribute, next-paint |
| `e2e/perf/support/measure.ts` | **New.** `median`, nearest-rank `percentile`, `evaluateBudget`, the quarantine record shape, artifact paths |
| `e2e/perf/support/trace.ts` | **New.** Trace capture, the allowlist scrubber, the post-write verification |
| `e2e/perf/support/zip.ts` | **New.** Minimal ZIP reader/writer (store + deflate) for the scrubber |
| `e2e/perf/support/issues.ts` | **New.** The perf issue policy and its two sinks (`gh`, or a fileable draft) |
| `e2e/perf/support/gate.ts` | **New.** `runBudget` - the one function every budget calls |

### Wiring

| File | Change |
| --- | --- |
| `playwright.config.ts` | `e2e/perf/**/*.spec.ts` added to `testMatch`; a second, **disjoint** `perf-chromium` project with `trace: 'off'` so the specs own the trace lifecycle; `@perf` documented; comments updated |
| `package.json` | `test:perf` |
| `tsconfig.json` | `e2e/perf/**/*.ts` added to `include` (the 041C scope widened, not replaced) |
| `frontend/src/styles/globals.css` | **Pre-existing gate repair.** 041C's comment carried two hex literals, and `check:no-hex` reads the file comments included, so `npm run check:no-hex` was red on 041C's tree. The comment now names the tokens instead. No styling changed. |

## Decisions Made

1. **Synthetic fixtures at the transport layer, not a seeded database.** The rig
   seeds two projects and one segment; "at 200 rows" and "at 5k segments" cannot
   be measured against that, and seeding 5,000 segments would change what the
   041B visual baselines and the 041C audit render. `page.route` over the real
   bundle preserves every millisecond the application spends and replaces only
   the API responses, so **server time is in no budget** - stated in each
   budget's `limitations`, not buried.

2. **`app-interactive` measures the sign-in screen, not the authenticated
   shell.** `features/auth/authStore.ts` keeps tokens in memory and never
   persists them, so a document load is a logout and a pre-shell `/me` can only
   resolve to `anonymous`. A cold authenticated boot is unreachable without
   changing that, which is a session decision (019), not a measurement decision.

3. **The 100 ms p95 covers direct manipulation; the 500 ms budget covers the
   debounced transport.** `TIMELINE_DEBOUNCE_MS` is 120 ms, so ruler seeks,
   waveform scrubs, zoom and pan cannot be inside a 100 ms budget without
   contradicting a deliberate product decision. Both sets are measured; the
   debounced set is gated by `media-seek`, and its latencies are recorded on
   every timeline run as evidence.

4. **"At 200 rows" is a 200-row tenant.** `features/projects/api.ts`
   `parsePageSize` clamps the page size to the API maximum of 100, so the grid
   can never be handed 200 rows. Both readings are asserted separately: the
   fixture really has 200 projects (a two-page result), and the DOM really never
   exceeds the page the server sent.

5. **5,000 segments is overshot on purpose, and the ceiling is pinned.** Two
   ceilings sit below the budget: `Quota:MaxSegmentCount` (2,000) and
   `useTranscript`'s 10 pages x 200. The synthetic endpoint returns the whole
   corpus on page 1 regardless of the page size asked for, and
   `practices.spec.ts` asserts the real ceiling on the **real** endpoint as a
   tripwire, so raising it later is a deliberate diff. Measuring a budget the
   application cannot reach would be worse than a documented overshoot.

6. **Search expectations are computed from the corpus, not hardcoded.** The first
   version searched `perfneedlea`, matched nothing, and spent thirty seconds
   timing out against a budget that was never under test. `countMatches` now
   counts the way `filterTranscriptSegments` counts, and a term that matches
   zero rows fails the run.

7. **A new, disjoint `perf-chromium` project with `trace: 'off'`.** The task
   requires scrubbed traces; Playwright's `retain-on-failure` produces
   unscrubbed ones and owns the lifecycle for the whole context, so a spec
   cannot start its own. The projects are disjoint, so nothing runs twice
   (350 tests: 323 in `cross-layer-chromium`, 27 in `perf-chromium`).

8. **One shared authenticated page for the whole perf suite.** Tokens live in
   the page's JS heap, so a new page is a logout and `POST /auth/login` is
   limited to 5/min. Six specs x one sign-in each is a suite that spends its time
   waiting for 429s. Sharing is safe because each spec takes its own synthetic
   project id, so no react-query key is shared between two fixtures of different
   sizes.

9. **The trace scrubber is an allowlist, and it verifies its own output.** It
   keeps `trace.trace`, `trace.network` and `trace.stacks`; drops
   `resources/**` (every response body, including the media bytes),
   `screencast/**` and `src/**` whole; drops `frame-snapshot` and
   `screencast-frame` records; reduces headers to seven names; strips cookies,
   postData and every query string. Then it re-reads the written archive and
   fails the run on any surviving token, JWT, presigned parameter,
   `Authorization`/`Cookie` header or `CHANGE_ME`. The raw archive is deleted
   unconditionally.

10. **Issue filing degrades, the verdict does not.** With `gh` and a token it
    opens or comments; without either it writes a fileable markdown draft and
    says so. Either way the run still fails on timing.

## Build/Test Results

```
$ npm run test:perf
  npx playwright test --grep=@perf
  27 passed (56.6s)

  [perf] app-interactive:     #1=1021ms #2=989ms #3=1055ms (median 1021ms, budget 3000ms)
  [perf] project-list-render: #1=115ms  #2=200ms #3=149ms  (median  149ms, budget 1500ms)
  [perf] workspace-open:      #1=118ms  #2=119ms #3=129ms  (median  119ms, budget 2000ms)
  [perf] timeline-interaction:#1=33ms   #2=34ms  #3=33ms   (p95     34ms, budget  100ms)
  [perf] segment-search:      #1=246ms  #2=241ms #3=242ms  (median  242ms, budget  300ms)
  [perf] media-seek:          #1=164ms  #2=159ms #3=165ms  (median  164ms, budget  500ms)
```

```
$ npm run typecheck:e2e
  > tsc --noEmit -p tsconfig.json
  (no output)
```

```
$ npx playwright test --grep="@visual"      -> 155 passed (5.4m)
$ npx playwright test --grep="@a11y"        -> 144 passed (4.9m)
$ npx playwright test --grep="@cross-layer" ->  24 passed (1.3m)
$ npm --prefix frontend test                -> Test Files 144 passed (144) | Tests 1587 passed (1587)
$ npm --prefix frontend run typecheck       -> (no output)
$ npm --prefix frontend run lint            -> (no output)
$ npm --prefix frontend run check:no-hex    -> check-no-hex: no hardcoded hex outside tokens.css.
$ dotnet test tests/DubbingPlatform.UnitTests
  Passed!  - Failed: 0, Passed: 3001, Skipped: 0, Total: 3001
```

### The six budgets, on the reference host

`Windows_NT, 6 cores, Intel(R) Core(TM) i5-9500T CPU @ 2.20GHz`, no CPU
throttling, 10 Mbps / 40 ms RTT network emulation, 1440x900:

| Budget | Limit | Median of 3 | Headroom |
| --- | --- | --- | --- |
| app-interactive | 3,000 ms | 1,021 ms | 66% |
| project-list render | 1,500 ms | 149 ms | 90% |
| workspace open | 2,000 ms | 119 ms | 94% |
| timeline interaction | 100 ms p95 | 34 ms | 66% |
| segment search | 300 ms | 242 ms | **19%** |
| media seek | 500 ms | 164 ms | 67% |

`segment-search` is the tight one, and deliberately: 200 ms of its 242 ms is the
`SEARCH_DEBOUNCE_MS` the budget includes on purpose, so the headroom the filter
actually has is ~40 ms. That is the budget doing its job, but it is the one most
likely to quarantine on a loaded runner.

## Findings

Four measurement failures, all found by running the thing and all of which would
have produced a green number that meant nothing. None is a production defect.

### 1. A 62 ms "app-interactive" against a real 990 ms load

`measureAppInteractive` returned the probe's own `ms` (elapsed since the probe
started, i.e. the fonts-and-idle wait) instead of its `at`
(`performance.now()` since navigation start). The budget passed by 47x on a
number that described nothing. Reading `at` gives 1,021 ms against a 3,000 ms
limit. `probes.ts` now says which is which and why, because the wrong field is
the obvious one.

### 2. A media budget that would have passed while measuring nothing

A media request fulfilled without `Range` support leaves the element reporting
`seekable = [0, 0]`: every seek is a silent no-op that **still fires `seeked`**.
The first assertion would have passed, six times over, against a video that never
moved. `fulfillRange` answers 206 with a `Content-Range`, and `media-seek.spec.ts`
asserts `video.seekable` spans the fixture before it times anything. Two
follow-on bugs from the same investigation: a route glob ending at the file name
does not match because Playwright globs match the query string too (the request
fell through to the SPA fallback and the element got `index.html`); and
`TimeRanges` are in **seconds**, so a real 60 s range compared against 50,000
failed as "too short".

### 3. A debounce measurement that stopped at the wrong paint

A plain click probe stops at the next paint, which for a debounced control is the
paint *before* the debounce fires - it reported 21 ms for a control whose
`TIMELINE_DEBOUNCE_MS` is 120 ms. `clickThenTextChange` waits for the watched
element's text to actually change, and the run asserts the result is >= 100 ms, so
removing the debounce cannot silently pass as a perf win.

### 4. A search budget timing out against a budget that was never under test

The first search term was `perfneedlea`, which matches nothing in the corpus: the
run spent thirty seconds in `probeWaitForAttribute` and reported a failure with
no indication that the term was the problem. Terms now carry an exact, corpus-
computed expected count and a zero-match term fails immediately with the reason.

### Also found and reported, not fixed

* **The timeline's dialogue lane is not windowed** (Task 030). All 1,200 fixture
  segments render as buttons - `Timeline` caps only the *markers* at 120.
  Interaction is still 34 ms p95 at that size, so nothing is slow today, but the
  tenant quota allows 2,000 and the lane is `O(segments)` per interaction.
  `practices.spec.ts` pins the current state (`data-rendered === data-total`) so
  closing the gap is a deliberate diff.
* **Two ceilings put 5,000 segments out of reach of the real data path** (027/032).
  Tripwire in `practices.spec.ts`, watching the real endpoint.
* **`check:no-hex` was red on 041C's tree** (see Files). Repaired; it is a CI gate.

## Recommendations for Next Agent

### Current repo state

- `main` carries this task on top of 041C's. `master-prompt.md` is modified and
  uncommitted (a pre-existing scratch file, deliberately not committed).
- `@perf` 27/27, `@visual` 155/155, `@a11y` 144/144, `@cross-layer` 24/24,
  frontend 1587, backend unit 3001. All green individually.
- **The Playwright config now has two projects.** `cross-layer-chromium`
  (`testIgnore: 'e2e/perf/**'`) runs the seams, the visual matrix and the a11y
  audit; `perf-chromium` (`testMatch: 'e2e/perf/**/*.spec.ts'`, `trace: 'off'`)
  runs only `e2e/perf`. If you add a suite, decide which project it belongs to -
  and if it needs to own its own traces, `trace: 'off'` is the requirement, not a
  preference.

### Gotcha that will bite you

**The perf suite shares one authenticated page for the whole worker.** Tokens live
in that page's JS heap, so `session.newPage()` is anonymous. Every perf spec takes
`session.page`, installs its own routes and removes them in a `finally`. If you
add a spec with a different fixture size, **give it its own `perfProjectId(n)`** -
otherwise its 1,200-segment corpus can be served to the 5,000-segment spec from
the shared react-query cache. Ids in use: 1 project-list, 2/6 transcript, 3
search, 4 timeline-interaction, 5 peaks, 7 media-seek, 8 dialogue-lane.

### The pre-existing failures, verified

`npx playwright test --grep="@visual|@a11y|@cross-layer"` in one process fails
the `workspace` visual baselines. **Verified pre-existing**: with this task's
config changes stashed, the same command gives an identical
**21 failed / 2 did not run / 300 passed**. `@a11y|@cross-layer` in one process is
likewise identical with and without this task: **10 failed / 11 did not run /
147 passed**. Each gate is green on its own. This is 046's outstanding
per-worker isolation; do not spend time on it as a perf problem.

### Naming and config conventions

- `e2e/perf/budgets.ts` is the only place a number lives. `budgets.spec.ts`
  fails a diff that moves one without a `BUDGET_CHANGELOG` entry.
- `e2e/perf/support/gate.ts` `runBudget({ testInfo, context, budget, measure })` is
  the only way a budget is measured, so the six cannot drift. `measure` is called
  three times with a 0-based index and returns `{ valueMs, detail }`.
- Everything a spec needs is in `support/probes.ts`; do not add a
  `Date.now()` around a Playwright call. A Node-side measurement around
  `page.goto` adds the driver's own scheduling and the poll round trip, which is
  hundreds of milliseconds of noise on a 300 ms budget.
- Reports, traces, quarantine records and issue drafts go to
  `tests/cross-layer/.artifacts/perf/`, which is already git-ignored. The gate
  prints the report path on every run.

### If a budget breaches

1. The run fails, a scrubbed trace is attached, and an issue is opened or updated.
   Without `gh` plus `GH_TOKEN`, a draft is at
   `.artifacts/perf/issues/<id>.md` - file it by hand.
2. A single-run spike **quarantines instead of failing**, and writes
   `.artifacts/perf/quarantine/<id>.json` with the samples, the owner and the
   issue. That is the shared-runner rule, not a suppression: the next breach of
   the same budget fails.
3. Raising a number is allowed. It goes through `BUDGET_CHANGELOG` with a reason,
   and the commit should carry the number it replaced.

### Open items 041D did not own

- **CI wiring (042B).** The exact step is in `e2e/perf/README.md`, including
  `if: always()` on the artifact upload and the rule that
  `--update-snapshots` must never appear in a perf job.
- **Per-worker isolation (046)** and the log scrubber, which the perf project
  partly pre-empts for its own traces but which nothing else does.
- **041C's blocking finding is still open**: `GET /segments?pageSize=200` returns
  500, so the transcript screen renders an error state against the real stack.
  The perf suite never sees it because it serves segments synthetically, and a
  green perf run is therefore not evidence that the transcript screen works.
- **`quarantine` and `issue` are the two features 041D could not exercise for
  real** - no budget came close, and there is no `gh` on this host. Both paths
  are unit-tested against synthetic inputs; the first real exercise of the
  `gh` sink will be in CI.
