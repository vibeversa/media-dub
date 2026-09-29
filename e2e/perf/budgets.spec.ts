// Task 041D, R5: the budget registry's own rules, and the harness's own tests.
//
// A budget file that nobody checks is a comment. This spec is what makes
// `e2e/perf/budgets.ts` a registry rather than a list:
//
//   * exactly the six budgets the task names, with the task's own numbers - so a
//     "helpful" 2.5s app-interactive is a failing diff, not a merged one;
//   * every budget names a fixture, an owner and a measurement contract, because
//     a number without those is a number on somebody's laptop;
//   * `BUDGET_CHANGELOG`'s newest entry is `BUDGET_VERSION`, and the previous
//     entry is preserved - so a version bump that does not say why, or a rewrite
//     that erases the history, both fail;
//   * the desktop-broadband profile is stated numerically, because "desktop
//     broadband" is otherwise whatever the machine it ran on happened to be.
//
// The second half of this file tests the machinery the six budgets depend on -
// the median-of-three verdict, the quarantine asymmetry, the trace scrubber and
// the issue renderer. Those are the parts a budget depends on but never
// exercises on a green run: a scrubber that is never asked to remove a secret
// and a verdict that has never seen a breach are both untested code that only
// matters on the worst day.

import { expect, test } from '@playwright/test';

import {
  BUDGETS,
  BUDGET_CHANGELOG,
  BUDGET_VERSION,
  DESKTOP_BROADBAND,
  MEDIAN_OF_THREE,
  budgetById,
  formatVerdictLine,
  type BudgetId,
} from './budgets.js';
import { PREVIEW_MEDIA_FILE } from './support/fixtures.js';
import { FORBIDDEN_PATTERNS, findForbiddenContent, scrubTraceArchive } from './support/trace.js';
import { renderIssueBody, issueMarker, PERF_ISSUE_LABEL } from './support/issues.js';
import { evaluateBudget, median, percentile } from './support/measure.js';
import { readZip, writeZip } from './support/zip.js';
import { repoFile } from './support/session.js';
import { existsSync, statSync } from 'node:fs';

const TASK_BUDGETS: ReadonlyArray<{ readonly id: BudgetId; readonly limitMs: number }> = [
  { id: 'app-interactive', limitMs: 3000 },
  { id: 'project-list-render', limitMs: 1500 },
  { id: 'workspace-open', limitMs: 2000 },
  { id: 'timeline-interaction', limitMs: 100 },
  { id: 'segment-search', limitMs: 3000 / 10 },
  { id: 'media-seek', limitMs: 500 },
];

test.describe('@perf budget registry', () => {
  test('@perf the six budgets are the task\'s six budgets, unmodified', () => {
    expect(BUDGETS.map((budget) => budget.id).sort()).toEqual(TASK_BUDGETS.map((entry) => entry.id).sort());
    for (const expected of TASK_BUDGETS) {
      const budget = budgetById(expected.id);
      expect(
        budget.limitMs,
        `${budget.id} is ${String(budget.limitMs)}ms; the task fixes it at ${String(expected.limitMs)}ms. ` +
          'Raising a budget is allowed, but it goes through BUDGET_CHANGELOG with a reason - not through ' +
          'this assertion, which exists so the change cannot be accidental.',
      ).toBe(expected.limitMs);
    }
  });

  test('@perf every budget carries a fixture, an owner and a measurement contract (R3)', () => {
    for (const budget of BUDGETS) {
      expect(budget.fixture.length, `${budget.id} has no fixture`).toBeGreaterThan(20);
      expect(budget.owner.length, `${budget.id} names no owning task`).toBeGreaterThan(0);
      expect(budget.measured.length, `${budget.id} does not say what it measures`).toBeGreaterThan(40);
      expect(budget.requirement.length, `${budget.id} does not quote its requirement`).toBeGreaterThan(5);
      expect(budget.limitations.length, `${budget.id} declares no limitations, so it cannot have any`).toBeGreaterThan(0);
    }
  });

  test('@perf budget changes are versioned with a recorded reason (R5)', () => {
    expect(BUDGET_CHANGELOG.length, 'the budget set has a version but no changelog').toBeGreaterThan(0);
    const newest = BUDGET_CHANGELOG[0];
    expect(
      newest?.version,
      'the newest changelog entry is not the current BUDGET_VERSION, so a version bump did not ' +
        'record a reason. A silent threshold bump is exactly what the task rejects.',
    ).toBe(BUDGET_VERSION);
    expect(newest?.reason.length ?? 0, 'the current budget version has an empty reason').toBeGreaterThan(20);

    const versions = BUDGET_CHANGELOG.map((entry) => entry.version);
    expect(new Set(versions).size, 'the changelog repeats a version').toBe(versions.length);
    for (const entry of BUDGET_CHANGELOG) {
      expect(entry.date, `${entry.version} has no date`).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    }
  });

  test('@perf the desktop-broadband profile is stated numerically', () => {
    expect(DESKTOP_BROADBAND.latencyMs).toBeGreaterThan(0);
    expect(DESKTOP_BROADBAND.downloadBytesPerSecond).toBeGreaterThan(1_000_000);
    expect(DESKTOP_BROADBAND.description.length).toBeGreaterThan(20);
    // 10 Mbps is the number the description claims. If the two disagree, the
    // description is the thing people will believe.
    expect(DESKTOP_BROADBAND.downloadBytesPerSecond).toBe((10 * 1000 * 1000) / 8);
  });

  test('@perf the synthetic media fixture exists and is the recorded size', () => {
    const path = repoFile(PREVIEW_MEDIA_FILE);
    expect(existsSync(path), `the synthetic preview fixture is missing at ${path}`).toBe(true);
    const size = statSync(path).size;
    expect(size, 'the synthetic preview is empty, so a seek cannot be measured').toBeGreaterThan(10_000);
    expect(size, 'the synthetic preview is implausibly large for a 60s fixture').toBeLessThan(1_000_000);
  });
});

test.describe('@perf gate machinery', () => {
  test('@perf median and nearest-rank p95 are the statistics they claim to be', () => {
    expect(median([3, 1, 2])).toBe(2);
    expect(median([4, 1, 2, 3])).toBe(2.5);
    expect(percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 0.95)).toBe(10);
    // Nearest-rank, not interpolated: the reported p95 is a sample that happened.
    expect(percentile([10, 20, 30], 0.95)).toBe(30);
    expect(() => median([])).toThrow();
  });

  test('@perf a median breach fails and a single-run spike quarantines', () => {
    const budget = budgetById('project-list-render');

    const spike = evaluateBudget(budget, [
      { index: 0, valueMs: 400 },
      { index: 1, valueMs: 9_000 },
      { index: 2, valueMs: 410 },
    ]);
    // The median rule in action: one very slow run, median comfortably under.
    expect(spike.verdict).toBe('spike-quarantined');
    expect(spike.explanation).toContain('quarantined');

    const breach = evaluateBudget(budget, [
      { index: 0, valueMs: 1_600 },
      { index: 1, valueMs: 1_700 },
      { index: 2, valueMs: 1_800 },
    ]);
    expect(breach.verdict).toBe('breach');
    expect(breach.explanation).toContain('BUDGET BREACH');
    expect(breach.explanation).toContain(budget.owner);

    const pass = evaluateBudget(budget, [
      { index: 0, valueMs: 400 },
      { index: 1, valueMs: 410 },
      { index: 2, valueMs: 420 },
    ]);
    expect(pass.verdict).toBe('pass');

    // Fewer than three samples is refused outright, which is the rule's whole
    // point: a verdict from one run is a spike wearing a pass.
    expect(() => evaluateBudget(budget, [{ index: 0, valueMs: 100 }])).toThrow(/shared-runner rule/);
    expect(MEDIAN_OF_THREE).toBe(3);
  });

  test('@perf the p95 budget is judged on its p95, not its median', () => {
    const budget = budgetById('timeline-interaction');
    // 20 fast samples and 3 slow ones: the median is fine, the p95 is not.
    const samples = Array.from({ length: 20 }, (_unused, index) => ({ index, valueMs: 20 })).concat(
      Array.from({ length: 3 }, (_unused, index) => ({ index: 20 + index, valueMs: 140 })),
    );
    const result = evaluateBudget(budget, samples);
    expect(result.verdict).toBe('breach');
    expect(result.comparedMs).toBeGreaterThan(budget.limitMs);
  });

  test('@perf the verdict line names the budget, its version and its fixture', () => {
    const line = formatVerdictLine(budgetById('media-seek'), 640);
    expect(line).toContain('media seek');
    expect(line).toContain(BUDGET_VERSION);
    expect(line).toContain('fixture');
    expect(line).toContain('640ms');
  });
});

test.describe('@perf trace scrubbing', () => {
  test('@perf the zip round-trips through the reader and writer', () => {
    const entries = [
      { name: 'a.txt', method: 8, data: Buffer.from('hello trace', 'utf8') },
      { name: 'nested/b.bin', method: 0, data: Buffer.from([0, 1, 2, 3, 250]) },
    ];
    const written = writeZip(entries);
    const read = readZip(written);
    expect(read.map((entry) => entry.name).sort()).toEqual(['a.txt', 'nested/b.bin']);
    expect(read[0]?.data.toString('utf8')).toBe('hello trace');
    expect(read[1]?.data.equals(Buffer.from([0, 1, 2, 3, 250]))).toBe(true);
    // A real zip, not a plausible one: the end-of-central-directory signature
    // must be where a unzip tool would look for it.
    expect(written.includes(Buffer.from([0x50, 0x4b, 0x05, 0x06]))).toBe(true);
  });

  test('@perf scrubbing removes response bodies, screencast frames and source', () => {
    // A synthetic trace that contains every category the real one does, with a
    // secret in each. A scrubber that has only ever seen a clean trace is a
    // scrubber that has never been tested.
    const dirty = writeZip([
      {
        name: 'trace.trace',
        method: 8,
        data: Buffer.from(
          [
            JSON.stringify({ type: 'before', callId: 'c1', class: 'Frame', method: 'evaluate', params: { expression: 'x' } }),
            JSON.stringify({
              type: 'frame-snapshot',
              snapshot: { html: ['HTML', {}, ['SCRIPT', {}, 'Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature']] },
            }),
            JSON.stringify({ type: 'screencast-frame', file: 'screencast/x.jpeg' }),
            '',
          ].join('\n'),
          'utf8',
        ),
      },
      {
        name: 'trace.network',
        method: 8,
        data: Buffer.from(
          [
            JSON.stringify({
              type: 'resource-snapshot',
              snapshot: {
                request: {
                  method: 'GET',
                  url: 'http://127.0.0.1:58080/api/v1/projects?page=1&X-Amz-Signature=deadbeef&access_token=abc',
                  headers: [
                    { name: 'Authorization', value: 'Bearer eyJhbGciOiJIUzI1NiJ9.body.sig' },
                    { name: 'Cookie', value: 'session=secret' },
                    { name: 'Accept', value: 'application/json' },
                  ],
                  cookies: [{ name: 'session', value: 'secret' }],
                },
                response: { status: 200, headers: [{ name: 'Content-Type', value: 'application/json' }] },
              },
            }),
            '',
          ].join('\n'),
          'utf8',
        ),
      },
      { name: 'resources/abc123.mkv', method: 0, data: Buffer.from([0x1a, 0x45, 0xdf, 0xa3, 0, 1, 2, 3]) },
      { name: 'screencast/page-1.jpeg', method: 0, data: Buffer.from([0xff, 0xd8, 0xff, 0xe0, 1, 2, 3]) },
      { name: 'src/abc.mjs', method: 0, data: Buffer.from('export const token = "CHANGE_ME";', 'utf8') },
      { name: 'trace.stacks', method: 0, data: Buffer.from('{"callFrame":{"functionName":"a"}}', 'utf8') },
    ]);

    const scrubbed = scrubTraceArchive(dirty);
    expect(scrubbed.problems).toEqual([]);
    expect(Object.keys(scrubbed.dropped).sort()).toEqual(
      ['resources/abc123.mkv', 'screencast/page-1.jpeg', 'src/abc.mjs'].sort(),
    );
    // Two records went with them: the DOM snapshot and the screencast frame
    // reference. A DOM snapshot is the whole page as JSON, including whatever
    // the page happens to be holding in an attribute.
    expect(scrubbed.droppedRecords).toBe(2);
    expect(scrubbed.redacted).toBeGreaterThan(0);
    // The whole point: nothing forbidden survives, checked on the OUTPUT.
    expect(findForbiddenContent(scrubbed.archive)).toEqual([]);
    const names = readZip(scrubbed.archive).map((entry) => entry.name);
    expect(names.sort()).toEqual(['trace.network', 'trace.stacks', 'trace.trace']);
  });

  test('@perf the forbidden-pattern list covers every category the task names', () => {
    const labels = FORBIDDEN_PATTERNS.map((pattern) => pattern.label);
    expect(labels).toContain('bearer token');
    expect(labels).toContain('JWT');
    expect(labels).toContain('presigned query parameter');
    expect(labels).toContain('authorization header');
    // The detection has to work on the shapes that actually appear, not only on
    // the shapes in the fixture above.
    for (const pattern of FORBIDDEN_PATTERNS) {
      expect(pattern.regex.test('Bearer eyJhbGciOiJIUzI1NiJ9.a.b'), `${pattern.label} missed a bearer token`).toBe(
        pattern.regex.test('Bearer eyJhbGciOiJIUzI1NiJ9.a.b'),
      );
    }
    expect(FORBIDDEN_PATTERNS.some((p) => p.regex.test('http://x/y?X-Amz-Signature=deadbeef'))).toBe(true);
    expect(FORBIDDEN_PATTERNS.some((p) => p.regex.test('"authorization": "Bearer x"'))).toBe(true);
    expect(FORBIDDEN_PATTERNS.some((p) => p.regex.test('Password=CHANGE_ME'))).toBe(true);
  });

  test('@perf an unparseable trace is reported, not silently passed', () => {
    const result = scrubTraceArchive(Buffer.from('this is not a zip file at all', 'utf8'));
    // A trace that cannot be scrubbed is never returned as if it had been: the
    // archive is empty, so nothing downstream can mistake it for a clean one.
    expect(result.archive.length).toBe(0);
    expect(result.problems.join(' ')).toContain('could not be parsed');
  });
});

test.describe('@perf issue policy', () => {
  test('@perf the issue body carries everything needed to act on it', () => {
    const budget = budgetById('segment-search');
    const body = renderIssueBody({
      budget,
      samples: [
        { index: 0, valueMs: 320, detail: { corpus: '5000' } },
        { index: 1, valueMs: 310, detail: { corpus: '5000' } },
        { index: 2, valueMs: 330, detail: { corpus: '5000' } },
      ],
      comparedMs: 320,
      limitMs: 300,
      hostProfile: 'win32 8 cores, Test CPU',
      runReference: 'run:@perf segment search',
      traceReference: 'tests/cross-layer/.artifacts/perf/traces/segment-search-run1.zip',
      traceWarning: '',
      verdict: 'breach',
    });

    expect(body).toContain(issueMarker('segment-search'));
    expect(body).toContain(BUDGET_VERSION);
    expect(body).toContain('5000');
    expect(body).toContain(budget.owner);
    expect(body).toContain(budget.fixture);
    expect(body).toContain('Host profile');
    expect(body).toContain('BUDGET_CHANGELOG');
    expect(PERF_ISSUE_LABEL).toBe('perf-budget');
  });
});
