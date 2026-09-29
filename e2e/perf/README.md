# Performance budgets (Task 041D)

Six frontend budgets, measured on the real rig with the real bundle, with traces
on breach and a median-of-three verdict so a shared runner does not block a
release.

```bash
docker compose -f tests/cross-layer/docker-compose.cross.yml up -d
npm run test:perf            # the gate
npx playwright test --grep="@perf"
npm run typecheck:e2e        # types for these specs (Playwright does not check them)
```

Read `tests/cross-layer/README.md` first - it is the rig runbook. The perf suite
signs in once and navigates in-app, for the same reasons 041B and 041C do.

## The budgets

The numbers are the task's, unmodified. `e2e/perf/budgets.ts` is the only place
they live, and `budgets.spec.ts` fails a diff that moves one without a
`BUDGET_CHANGELOG` entry saying why.

| Budget | Limit | Fixture | Verdict |
| --- | --- | --- | --- |
| app-interactive | 3,000 ms | cold load of `/`, HTTP cache disabled, 10 Mbps / 40 ms RTT | median of 3 |
| project-list render | 1,500 ms | synthetic 200-project tenant, largest legal page (100 rows) | median of 3 |
| workspace open | 2,000 ms | the **real** rig: seeded project, one `/workspace` aggregate | median of 3 |
| timeline interaction | 100 ms p95 | synthetic 1,200-segment transcript | p95 per run, median of 3 |
| segment search | 300 ms | synthetic 5,000-segment transcript | median of 3 |
| media seek | 500 ms | synthetic 60 s / 100,031-byte Matroska preview | worst seek per run, median of 3 |

Measured on the reference host (`Windows_NT, 6 cores, Intel i5-9500T @ 2.20GHz`,
no CPU throttling, desktop-broadband network emulation): **1,101 / 159 / 121 / 33 /
242 / 162 ms**. Every number is re-measured on every run and recorded in
`tests/cross-layer/.artifacts/perf/reports/<budget>.json` with the host profile,
so a regression is comparable and a green run is still evidence.

## What is real and what is synthetic

| | Real | Synthetic |
| --- | --- | --- |
| bundle, router, providers, React render, `DataGrid`, `VirtualizedSegmentList`, debounce, timeline, `<video>` | yes | |
| API responses | | yes (except `workspace-open`, which uses the real API) |
| media bytes | | yes (100 KB, generated, not tenant media) |

The consequence is stated in every budget: **server time is not in any of these
numbers.** This gate catches client regressions. A slow endpoint is not its job.

## The measurement contract

Four decisions the task left open, each recorded in `budgets.ts` next to the
budget it affects.

**"Interactive" is the sign-in form, not the shell.** The auth store keeps
tokens in memory only and never persists them, so a document load is a logout and
a pre-shell `/me` can only resolve to `anonymous`. A cold *authenticated* boot is
not reachable without changing that, which is a session decision (019). The
budget therefore measures the cold load of `/` to the point where the form is
usable - document, chunks, parse, render, paint, fonts, idle.

**The 100 ms p95 covers direct manipulation; debounced transport is the 500 ms
budget's.** `TIMELINE_DEBOUNCE_MS` is 120 ms, so ruler seeks, waveform scrubs,
zoom and pan cannot be inside a 100 ms budget without contradicting a deliberate
product decision. `timeline-interaction` measures segment select, region set,
region clear and the loop toggle; the debounced set is measured by `media-seek`,
and the debounced latencies are still recorded on every timeline run as evidence.

**"At 200 rows" is a 200-row tenant, not a 200-row page.** The client clamps the
requested page size to the API maximum of 100, so the grid can never be handed
200 rows. Both halves are asserted: the fixture really has 200 projects (so the
result set really is two pages), and the DOM really never exceeds the page the
server sent.

**5,000 segments is not reachable through the real data path, and the fixture
overshoots to get there.** `Quota:MaxSegmentCount` defaults to 2,000 and
`useTranscript` fetches at most 10 pages of 200 - a 2,000 ceiling. The synthetic
endpoint returns the whole corpus on page 1 regardless of the page size asked
for, because measuring a budget the application cannot reach would be worse.
`practices.spec.ts` pins the real ceiling on the real endpoint as a tripwire, so
the day 027/032 raises it, the diff says the fixture has to be revisited.

## The shared-runner rule

Implemented literally, and deliberately asymmetric:

| Verdict | Condition | What happens |
| --- | --- | --- |
| **pass** | every run under the limit | report written, nothing attached |
| **breach** | the median (or the p95) is over the limit | **run fails**, scrubbed trace attached, perf issue opened or updated |
| **spike-quarantined** | the median is under but a run is over | run passes, **quarantine record written** naming an owner and an issue |

A single slow sample must not block a release, and a real regression must not be
explained away by "the runner was slow". The median separates those only if the
slow-but-not-median case leaves a record, so it does: `evaluateBudget` refuses
fewer than three samples rather than quietly using what it was given, and the
quarantine is a file with an owner and an issue in it.

## Traces, and what is removed from them

The task requires traces to be scrubbed of URLs, tokens and media bytes before
any CI attach. Playwright's `trace: 'retain-on-failure'` cannot do that - it
produces an unscrubbed archive and owns the trace lifecycle for the whole
context - so the perf project turns it off and `support/trace.ts` owns the
lifecycle instead. That is the only reason the config has two projects, and they
are disjoint, so nothing runs twice.

What a real trace contains, measured rather than assumed:

| Entry | Contains | Policy |
| --- | --- | --- |
| `trace.trace` | library calls, console, **full DOM as JSON** per snapshot, screencast references | kept; `frame-snapshot` and `screencast-frame` records dropped whole |
| `trace.network` | HAR-shaped request/response **headers** (including `Authorization`, `Cookie`) and **URLs** (presigned queries) | kept; headers reduced to a 7-name allowlist, cookies/postData/query strings dropped, every URL reduced to origin + path |
| `resources/*` | response **bodies** - the media bytes, and every API body | dropped wholesale |
| `screencast/*` | jpeg frames | dropped wholesale |
| `src/*` | application source | dropped wholesale |

It is an allowlist, not a denylist, because a denylist over a format that grows
with the browser is a scrubber that leaks the next time Chromium adds a field.

After writing, the archive is **re-read and scanned** for bearer tokens, JWTs,
presigned query parameters, `Authorization`/`Cookie` headers, token fields and
the rig's `CHANGE_ME` placeholder. A hit fails the run and says so; the raw,
unscrubbed copy is deleted unconditionally, because leaving it beside the
scrubbed one is how it eventually gets uploaded.

A trace that cannot be captured or cannot be parsed never replaces the verdict:
the run still fails on timing, with a missing-trace warning attached.

## Perf issues

The policy is in `support/issues.ts`, because the repository had none (042B owns
CI). One open issue per budget, found by the `<!-- perf-budget:<id> -->` marker
in the body rather than by title, labelled `perf-budget`. A breach **comments**
on the existing issue instead of opening a new one, because a budget that
breaches weekly should produce weekly comments.

Two sinks, chosen at run time, and never a hard failure:

* `gh` on PATH plus `GH_TOKEN`/`GITHUB_TOKEN` -> open or comment;
* otherwise a markdown draft at
  `tests/cross-layer/.artifacts/perf/issues/<id>.md`, whose path is used as the
  issue reference.

The body always carries the limit, the measurement, the budget-set version, the
fixture, the owning task, the sample table, the host profile, the measurement
contract, the declared limitations, and how to re-baseline.

### Wiring it into CI

042B owns the workflow; the step is:

```yaml
- run: npm ci
- run: docker compose -f tests/cross-layer/docker-compose.cross.yml up -d
- run: npm run test:perf
  env:
    GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
- uses: actions/upload-artifact@v4
  if: always()
  with:
    name: perf-reports
    path: tests/cross-layer/.artifacts/perf/
```

`--update-snapshots` must never appear in a perf job, and the artifact upload is
`if: always()` so a breaching run's reports and scrubbed trace survive a red
build. The raw trace is already deleted by the gate.

## Structural practice assertions

`practices.spec.ts` (R4). A timing budget says a number got worse; it does not
say which practice stopped happening, and on a fast machine a removed debounce
may not move any of the six numbers. So each practice is asserted on its own
evidence, and each failure names the owning task:

| Practice | Asserted by | Owner |
| --- | --- | --- |
| route-level splitting | the real network log of a real cold load: an entry chunk **and** a separate `LoginPage` chunk | 015 |
| list bound at 200+ rows | two mechanisms, both checked: the grid is bounded by server pagination, the transcript list is windowed at 5,000 | 021, 027 |
| preview peaks, not archival media | the request log: peaks fetched, and **no** request for anything that could be a source original | 030 |
| search debounce | a nine-keystroke burst produces one filter pass, counted with a `MutationObserver` | 027 |
| the 2,000-segment data-path ceiling | the **real** endpoint's request parameters | 027/032 |

Every one of them has a positive control first, because "at most N" and "no
archival media" both pass on zero.

## Findings

Reported rather than waived, because each belongs to another task.

**The timeline's dialogue lane is not windowed.** All 1,200 fixture segments
render as buttons; `Timeline` caps only the *markers* at 120. Interaction is still
34 ms p95 at that size, so nothing is slow today - but the tenant quota allows
2,000 and the lane is `O(segments)` per interaction. `practices.spec.ts` pins the
current state (`data-rendered === data-total`) so closing the gap is a
deliberate diff. Owner: 030.

**Two ceilings put 5,000 segments out of reach** (quota 2,000; client 10 pages x
200). The tripwire is in `practices.spec.ts`. Owners: 027/032.

**`segment-search` has the least headroom of the six** (242 ms of 300 ms, of
which 200 ms is the `SEARCH_DEBOUNCE_MS` the budget deliberately includes). A
filter regression of ~60 ms fails it. That is the budget doing its job, but it is
the one most likely to quarantine on a loaded runner.

## Known gaps

- **No CI wiring.** 042B owns it; the step is above and `npm run test:perf`
  needs the rig up, so it cannot run on a bare checkout.
- **No CPU throttling.** The profile emulates desktop broadband only, and a
  desktop profile is a 1x CPU profile. The host is recorded with every sample
  instead, which is weaker than a fixed CPU throttle and honest about it.
- **A non-gate perf failure attaches no trace.** The perf project has
  `trace: 'off'` so the gate can own the lifecycle; the gate's own breach path
  attaches a scrubbed trace, and a structural-assertion failure does not. The
  run report and the error context are still attached.
- **Suites still interfere when run in one process.** `@visual` is 155/155 and
  `@a11y` 144/144 and `@cross-layer` 24/24 individually; running
  `--grep="@visual|@a11y|@cross-layer"` in one process fails the `workspace`
  baselines. Verified pre-existing on a clean tree (21 failed / 2 did not run /
  300 passed, identical with and without this task's changes). Per-worker
  isolation is 046's outstanding item.
- **Trace scrubbing is Chromium/Playwright-format specific.** The allowlist is
  keyed on entry names, so a format change shows up as "everything dropped"
  rather than "nothing dropped" - which fails the coverage expectation in
  `budgets.spec.ts` rather than silently passing.

## Layout

| File | Purpose |
| --- | --- |
| `budgets.ts` | The six budgets, their fixtures, owners, measurement contracts, limitations, the bandwidth profile and the versioned changelog |
| `app-interactive.spec.ts` | Cold-load budget, desktop-broadband CDP profile |
| `project-list.spec.ts` | 200-project tenant render budget |
| `workspace-open.spec.ts` | The one budget on the real API |
| `timeline-interaction.spec.ts` | 1,200-segment p95, plus the debounced evidence |
| `segment-search.spec.ts` | 5,000-segment search budget |
| `media-seek.spec.ts` | Seek budget, with the seekable-range non-vacuity guard |
| `practices.spec.ts` | R4 structural practice assertions and the data-path tripwires |
| `budgets.spec.ts` | R5 registry rules, plus tests for the verdict, the scrubber and the issue renderer |
| `support/session.ts` | The shared signed-in session, the cold context, the bandwidth profile, the host profile |
| `support/fixtures.ts` | Synthetic corpora, the route layer, the request log, Range-correct media |
| `support/probes.ts` | In-page probes: input-to-paint, open, seek, search, count, attribute |
| `support/measure.ts` | median / nearest-rank p95, the median-of-three verdict, the quarantine registry |
| `support/trace.ts` | Trace capture, the allowlist scrubber, the post-write verification |
| `support/zip.ts` | Minimal ZIP reader/writer (store + deflate) for the scrubber |
| `support/issues.ts` | The perf issue policy and its two sinks |
| `support/gate.ts` | The one function every budget calls, so six budgets cannot drift |
| `fixtures/preview-60s.mkv` | The synthetic preview |
| `fixtures/generate-media.mjs` | Regenerates it: FFmpeg, argument arrays, no shell |

## Changing a budget

1. Edit the number in `budgets.ts`.
2. Add a `BUDGET_CHANGELOG` entry and bump `BUDGET_VERSION`. The registry test
   fails the newest entry not matching the current version.
3. Re-run `npm run test:perf` and commit the new reports' numbers in the commit
   message, so the next reader can see what the number actually was.
