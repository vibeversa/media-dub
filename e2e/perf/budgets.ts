// Task 041D: the performance budgets.
//
// One place that states what "fast enough" means for this application, what
// each number was measured against, and what would have to be recorded before
// any of them moves. Three rules are enforced by `budgets.spec.ts`, not by
// convention:
//
//   1. Every budget carries a `fixture` string naming the data it was measured
//      against. A budget without one is a number on a laptop, not a budget.
//   2. The budget set is **versioned**. `BUDGET_VERSION` moves only with an
//      entry in `BUDGET_CHANGELOG` that names the reason, so "we raised the
//      number" is always a reviewable diff and never a quiet edit.
//   3. A budget change without a reason, or a change that moves the number
//      *and* the fixture, fails the registry check.
//
// The budgets themselves are the task's, unmodified:
//
//   app-interactive      <= 3s    desktop broadband
//   project-list render  <= 1.5s  at 200 rows
//   workspace open       <= 2s
//   timeline interaction <= 100ms p95
//   segment search       <= 300ms at 5k segments
//   media seek           <= 500ms
//
// What *is* recorded here, because the task did not fix it, is the measurement
// contract each number is read against. Those decisions are the ones a reviewer
// needs in order to argue with a number: what "interactive" means, which
// interactions the 100ms p95 covers, and what happens to a 5k-segment fixture
// when the application can only hold 2k.

/** Budget set version. Bump ONLY with a matching `BUDGET_CHANGELOG` entry. */
export const BUDGET_VERSION = '2026-09-29.1';

export type BudgetId =
  | 'app-interactive'
  | 'project-list-render'
  | 'workspace-open'
  | 'timeline-interaction'
  | 'segment-search'
  | 'media-seek';

/** How a budget's verdict is computed from its samples. */
export type Statistic = 'median' | 'p95';

export interface Budget {
  readonly id: BudgetId;
  /** Title prefix; part of the Playwright test name. */
  readonly title: string;
  /** The number, in milliseconds. */
  readonly limitMs: number;
  /** Which statistic of the per-run samples is compared against `limitMs`. */
  readonly statistic: Statistic;
  /**
   * What the budget was measured against, in one line. Carried into the test
   * name and the failure message, because "3.1s > 3s" without the fixture is
   * not an actionable report.
   */
  readonly fixture: string;
  /**
   * The task clause this budget implements, quoted. Keeps the gate traceable
   * back to the requirement rather than to a number somebody liked.
   */
  readonly requirement: string;
  /** The feature task that owns the code being measured; a failure points here. */
  readonly owner: string;
  /** What the measurement starts and stops on. */
  readonly measured: string;
  /**
   * Declared limitations of this budget, recorded so they cannot be rediscovered
   * as "surprises" later. A limitation is not a waiver: it does not permit the
   * number to be exceeded.
   */
  readonly limitations: readonly string[];
}

export const BUDGETS: readonly Budget[] = [
  {
    id: 'app-interactive',
    title: 'app-interactive',
    limitMs: 3000,
    statistic: 'median',
    fixture: 'cold document load of `/`, HTTP cache disabled, no fixture rows',
    requirement: 'app-interactive <= 3s (desktop broadband)',
    owner: '015 (scaffolding/splitting), 019 (auth), 018 (shell)',
    measured:
      'Navigation start to the moment the app is interactive: the route root is painted, ' +
      'fonts are ready, and the main thread has been idle for one macrotask. Measured ' +
      'in-page against `performance.timeOrigin`, so it includes document, chunk, parse, ' +
      'render and paint under the throttled profile.',
    limitations: [
      'The measured route is the sign-in screen, not the authenticated shell. The auth ' +
        'store keeps access and refresh tokens in memory only and never persists them ' +
        "(`features/auth/authStore.ts`), so a full document load is a logout and the " +
        'pre-shell `/me` resolution can only ever resolve to `anonymous`. A cold ' +
        'authenticated boot is not reachable without changing that, which is a session ' +
        'decision (019), not a measurement decision.',
      'CPU is NOT throttled: the profile emulates desktop broadband network conditions ' +
        'only. The host profile is recorded with every sample so the number can be read ' +
        'against the machine that produced it (R3).',
      'The median-of-three rule means a single cold start slower than the budget ' +
        'quarantines rather than blocks; see `support/measure.ts`.',
      'The three runs share one browser context, so Chromium\'s in-process code cache is warm ' +
        'after the first. The HTTP cache is disabled for every run, which is the part that ' +
        'dominates a first-load budget; three browser launches would have measured process ' +
        'start-up instead of the application.',
    ],
  },
  {
    id: 'project-list-render',
    title: 'project-list render',
    limitMs: 1500,
    statistic: 'median',
    fixture: 'synthetic tenant of 200 projects; the app renders the largest page it can request (100 rows)',
    requirement: 'project-list render <= 1.5s at 200 rows',
    owner: '021 (project list), 016 (DataGrid)',
    measured:
      'Start: the projects query is issued (the `GET /projects` request is observed). ' +
      'Stop: the 100th row of the requested page is in the DOM with its text. The ' +
      '200-project tenant is a two-page result, so the budget is measured on a page ' +
      'inside a 200-row dataset rather than on a 200-row page.',
    limitations: [
      '"At 200 rows" is read as a 200-row *tenant*, not a 200-row page: ' +
        '`features/projects/api.ts` `parsePageSize` clamps the requested page size to ' +
        'the API maximum of 100, so the grid can never hold 200 rows. Both readings are ' +
        'asserted separately - the structural check proves the DOM never exceeds the ' +
        'page the server sent.',
      'The list response is served from a synthetic in-process fixture, so the number is ' +
        'a *client render* budget. Server time is reported alongside it, never folded in.',
      'Pagination, not windowing, is what bounds this list. See the structural ' +
        'assertions in `practices.spec.ts` for the 200+-row list that does window.',
    ],
  },
  {
    id: 'workspace-open',
    title: 'workspace open',
    limitMs: 2000,
    statistic: 'median',
    fixture: 'the real rig: seeded project `Cross Layer Pilot`, one aggregate `/workspace` call',
    requirement: 'workspace open <= 2s',
    owner: '025 (workspace), 026 (progress), 017 (query efficiency)',
    measured:
      'Start: in-app navigation to the project workspace is dispatched. Stop: the ' +
      'workspace aggregate has rendered every section (`workspace-page` present, header, ' +
      'review, warnings, output, media, config, run history, activity) and the main thread ' +
      'is idle. Real stack, real seed, no interception.',
    limitations: [
      'Measured on one seeded project with no active processing run, so the progress ' +
        'stepper and the SSE stream are at rest. A workspace with a live run renders more ' +
        'DOM and re-renders on every progress frame; that case is not covered here.',
      'In-app navigation, not a document load - see `app-interactive` for why a document ' +
        'load is a logout.',
    ],
  },
  {
    id: 'timeline-interaction',
    title: 'timeline interaction latency',
    limitMs: 100,
    statistic: 'p95',
    fixture: 'synthetic transcript of 1,200 segments on the project timeline tab',
    requirement: 'timeline interaction latency <= 100ms p95',
    owner: '030 (timeline, player, waveform)',
    measured:
      'Input-to-paint latency for the timeline\'s *direct* interactions, measured in-page ' +
      'from the dispatched event to the first animation frame after the DOM reflects the ' +
      'change. Twenty-five interactions per run, three runs, p95 of each run\'s samples, ' +
      'median of the three p95s. Interactions: dialogue-segment select, loop-region set, ' +
      'loop-region clear, play-region loop toggle.',
    limitations: [
      'Debounced transport interactions are NOT in this budget. `features/timeline/types.ts` ' +
        '`TIMELINE_DEBOUNCE_MS` is 120ms, so ruler seeks, waveform scrubs, zoom and pan ' +
        'cannot be inside a 100ms budget without contradicting a deliberate product ' +
        'decision. They are measured by the `media-seek` budget (500ms), which the debounce ' +
        'fits inside. The debounced latencies are still recorded on every run as ' +
        'evidence, so a debounce regression is visible even though it does not fail here.',
      '1,200 segments is a long-form episode (~1.2k utterances). The tenant quota ceiling ' +
        'is 2,000 (`Quota:MaxSegmentCount`) and the dialogue lane is NOT windowed, so this ' +
        'budget is measured below the ceiling. That gap is recorded as a finding for 030 ' +
        'rather than hidden by choosing a small number.',
    ],
  },
  {
    id: 'segment-search',
    title: 'segment search',
    limitMs: 300,
    statistic: 'median',
    fixture: 'synthetic transcript of 5,000 segments held in the client',
    requirement: 'segment search <= 300ms at 5k segments',
    owner: '027 (transcript editor, virtualized list, debounce)',
    measured:
      'Start: the search input receives one input event. Stop: the rendered window ' +
      'reflects the filtered set (`transcript-list` `data-total` matches the expected ' +
      'match count and the rows re-rendered). The 200ms search debounce is inside the ' +
      'measurement, because a user waits for it.',
    limitations: [
      '5,000 segments is NOT reachable through the application\'s own data path, and the ' +
        'synthetic endpoint overshoots the requested page size to get there. Two ceilings ' +
        'sit below the budget: `Quota:MaxSegmentCount` defaults to 2,000 and rejects more, ' +
        'and `features/transcript/useTranscript.ts` fetches at most `TRANSCRIPT_MAX_PAGES` ' +
        '(10) pages of `TRANSCRIPT_PAGE_SIZE` (200) = 2,000. Recorded as a finding for ' +
        '027/032; it is not a reason to lower the budget.',
      'The fixture carries transcript text on the list rows. The real list summary omits ' +
        'text, which sends the client into a per-segment detail hydration loop; that cost ' +
        'is a load-path problem, not a search-path problem, and is reported separately.',
    ],
  },
  {
    id: 'media-seek',
    title: 'media seek',
    limitMs: 500,
    statistic: 'median',
    fixture: 'synthetic 60s Matroska preview, 100,031 bytes, served same-origin with Range support',
    requirement: 'media seek <= 500ms',
    owner: '030 (MediaPlayer, Waveform), 033 (output descriptor)',
    measured:
      'Start: a seek input (the player scrubber or the waveform scrub) is dispatched through the ' +
      'application\'s own event path. Stop: the media element has fired `seeked` AND the position the ' +
      'player renders matches the request. Both halves are required, so a stub that answers `seeked` ' +
      'without the UI moving cannot pass. Eight seeks per run, three runs; a run\'s value is its ' +
      '*worst* seek and the verdict is the median of the three worsts, so a run cannot hide a slow ' +
      'seek behind a fast one.',
    limitations: [
      'The preview is a synthetic 100KB file held in the test process and fulfilled ' +
        'same-origin (the app\'s CSP `default-src \'self\'` forbids cross-origin media), so ' +
        'this is a *decode-and-paint* budget with no media network transfer. The transfer ' +
        'cost of a real signed preview is out of scope for a frontend gate.',
      'The 120ms interaction debounce is inside this budget, deliberately: 500ms is the ' +
        'budget that transport-level interactions belong to.',
    ],
  },
];

/** One recorded change to the budget set. `budgets.spec.ts` requires the tail to match `BUDGET_VERSION`. */
export interface BudgetChange {
  readonly version: string;
  readonly date: string;
  readonly reason: string;
}

export const BUDGET_CHANGELOG: readonly BudgetChange[] = [
  {
    version: BUDGET_VERSION,
    date: '2026-09-29',
    reason:
      'Initial budget set (Task 041D). The six values are the task\'s, unmodified. What is ' +
      'recorded here for the first time is the measurement contract each value is read ' +
      'against: the "interactive" definition, the 100ms/direct-manipulation vs ' +
      '500ms/debounced-transport split, the 200-row reading for the project list, and the ' +
      'two ceilings that put 5,000 segments out of reach of the real data path.',
  },
];

/** The bandwidth profile `app-interactive` is measured under. */
export interface BandwidthProfile {
  readonly id: string;
  readonly description: string;
  /** Round-trip time in milliseconds. */
  readonly latencyMs: number;
  /** Download throughput in bytes per second. */
  readonly downloadBytesPerSecond: number;
  /** Upload throughput in bytes per second. */
  readonly uploadBytesPerSecond: number;
}

/**
 * "Desktop broadband", stated numerically so the number is reproducible.
 *
 * 10 Mbps down / 5 Mbps up / 40 ms RTT. That is Lighthouse's desktop ("cable")
 * network profile to the nearest round number, chosen over Chrome's DevTools
 * presets because those are mobile presets and naming one of them would be a
 * silent decision to measure a phone.
 */
export const DESKTOP_BROADBAND: BandwidthProfile = {
  id: 'desktop-broadband',
  description: '10 Mbps down, 5 Mbps up, 40 ms RTT (Lighthouse desktop "cable", rounded)',
  latencyMs: 40,
  downloadBytesPerSecond: (10 * 1000 * 1000) / 8,
  uploadBytesPerSecond: (5 * 1000 * 1000) / 8,
};

/**
 * Runs per budget, and the rule that turns runs into a verdict.
 *
 * The task is explicit: a shared CI runner is slow sometimes, and a single slow
 * sample must not block a release. Three runs, verdict on the median (or the p95
 * for the one budget that is specified as a p95), and a single-run spike
 * quarantines with a record instead of failing. See `support/measure.ts`.
 */
export const MEDIAN_OF_THREE = 3;

export function budgetById(id: BudgetId): Budget {
  const found = BUDGETS.find((budget) => budget.id === id);
  if (found === undefined) {
    throw new Error(`No budget '${id}'. Add it to BUDGETS in e2e/perf/budgets.ts.`);
  }
  return found;
}

/** `1500ms <= 1.5s (project-list render @ v2026-09-29.1; fixture: ...)` - the failure headline. */
export function formatVerdictLine(budget: Budget, valueMs: number): string {
  return `${String(Math.round(valueMs))}ms ${budget.statistic} vs ${String(budget.limitMs)}ms budget ` +
    `(${budget.title} @ v${BUDGET_VERSION}; fixture: ${budget.fixture})`;
}
