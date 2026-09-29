// Task 041D: the gate.
//
// One function every budget spec calls, so the six budgets cannot drift apart in
// how they are measured, judged, evidenced or reported. It is deliberately the
// only place that knows about traces, quarantine records, perf issues and the
// run report: a spec supplies a budget and a way to take one sample, and the
// gate does the rest identically for all six.
//
// The evidence policy, from the task:
//
//   * a breach attaches a scrubbed trace and fails the run;
//   * a single-run spike is quarantined with a record and does not fail;
//   * a trace that cannot be captured or scrubbed never replaces the verdict -
//     the run still fails on timing, with a missing-trace warning attached.

import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import type { BrowserContext, TestInfo } from '@playwright/test';

import { BUDGET_VERSION, type Budget } from '../budgets.js';
import { filePerfIssue, type IssueOutcome } from './issues.js';
import {
  ARTIFACT_ROOT,
  evaluateBudget,
  formatSampleTable,
  issueDraftPath,
  quarantinePath,
  reportPath,
  tracePath,
  type PerfSample,
  type VerdictResult,
} from './measure.js';
import { hostProfile } from './session.js';
import { stopScrubbedTrace, type TraceOutcome } from './trace.js';

export interface RunReport {
  readonly budgetId: string;
  readonly budgetVersion: string;
  readonly limitMs: number;
  readonly statistic: string;
  readonly fixture: string;
  readonly requirement: string;
  readonly owner: string;
  readonly verdict: string;
  readonly comparedMs: number;
  readonly samples: readonly PerfSample[];
  readonly trace: { readonly captured: boolean; readonly path: string; readonly summary: string; readonly problems: readonly string[] };
  readonly issue: { readonly reference: string; readonly detail: string };
  readonly quarantine: string | null;
  readonly host: string;
  readonly recordedAt: string;
}

export interface GateOptions {
  readonly testInfo: TestInfo;
  readonly context: BrowserContext;
  readonly budget: Budget;
  /** Takes one measurement. Called `MEDIAN_OF_THREE` times with a 0-based index. */
  readonly measure: (run: number) => Promise<Omit<PerfSample, 'index'>>;
  /** Trace capture is opt-out for the registry spec, which needs no browser. */
  readonly captureTrace?: boolean;
}

export interface GateResult {
  readonly verdict: VerdictResult;
  readonly trace: TraceOutcome | null;
  readonly issue: IssueOutcome | null;
  readonly report: RunReport;
}

/**
 * Measures a budget `MEDIAN_OF_THREE` times and produces everything the run
 * needs to act on the result.
 *
 * The trace is captured around all three runs rather than per run: three traces
 * per budget is six times the artifact volume for the same evidence, and the
 * interesting question after a breach is what the page was doing across the
 * whole measurement, not during one of its samples.
 */
export async function runBudget(options: GateOptions): Promise<GateResult> {
  const { testInfo, context, budget } = options;
  const capture = options.captureTrace !== false;

  if (capture) {
    await context.tracing.start({
      // No colon. Playwright writes the trace name into a temporary filename, so
      // `perf:<id>` is EINVAL on Windows; it surfaces as a `copyfile` failure
      // inside `tracing.stop` and a trace that simply never appears. A missing
      // trace that looks like a missing trace is exactly the failure this module
      // exists to prevent, so the name is a filename and stays one.
      name: `perf-${budget.id}`,
      screenshots: false,
      snapshots: true,
      sources: false,
    });
  }

  const samples: PerfSample[] = [];
  for (let run = 0; run < 3; run += 1) {
    const measured = await options.measure(run);
    samples.push({ index: run, valueMs: measured.valueMs, detail: measured.detail });
  }

  let trace: TraceOutcome | null = null;
  if (capture) {
    const scrubbedPath = tracePath(budget.id, 1);
    const rawPath = `${ARTIFACT_ROOT}/raw/${budget.id}-raw.zip`;
    trace = await stopScrubbedTrace(context, scrubbedPath, rawPath);
    if (trace.captured && !trace.verified) {
      // A trace that exists but is not provably clean is not attachable.
      testInfo.annotations.push({ type: 'trace-warning', description: trace.summary });
    }
    for (const problem of trace.problems) {
      testInfo.annotations.push({ type: 'trace-warning', description: problem });
    }
  }

  const verdict = evaluateBudget(budget, samples);

  let issue: IssueOutcome | null = null;
  let quarantine: string | null = null;

  if (verdict.verdict === 'breach') {
    issue = filePerfIssue(issueDraftPath(budget.id), {
      budget,
      samples,
      comparedMs: verdict.comparedMs,
      limitMs: budget.limitMs,
      hostProfile: hostProfile(),
      runReference: testInfo === undefined ? 'local run' : `run:${String(testInfo.titlePath.join(' > '))}`,
      traceReference: trace?.captured === true ? trace.path : '',
      traceWarning: trace === null ? 'tracing not captured for this run' : trace.summary,
      verdict: verdict.verdict,
    });
    testInfo.annotations.push({ type: 'perf-issue', description: `${issue.reference} - ${issue.detail}` });
  }

  if (verdict.verdict === 'spike-quarantined') {
    quarantine = quarantinePath(budget.id);
    mkdirSync(dirname(quarantine), { recursive: true });
    writeFileSync(
      quarantine,
      `${JSON.stringify(
        {
          budgetId: budget.id,
          budgetVersion: BUDGET_VERSION,
          limitMs: budget.limitMs,
          samples,
          comparedMs: verdict.comparedMs,
          owner: budget.owner,
          issue: issue?.reference ?? 'local:no-issue-sink',
          recordedAt: new Date().toISOString(),
          note:
            'Quarantined by the shared-runner rule: at least one run exceeded the budget while the ' +
            'median did not. This is a record, not a pass - the next breach of the same budget fails.',
        },
        null,
        2,
      )}\n`,
      'utf8',
    );
    testInfo.annotations.push({ type: 'quarantine', description: quarantine });
  }

  const report: RunReport = {
    budgetId: budget.id,
    budgetVersion: BUDGET_VERSION,
    limitMs: budget.limitMs,
    statistic: budget.statistic,
    fixture: budget.fixture,
    requirement: budget.requirement,
    owner: budget.owner,
    verdict: verdict.verdict,
    comparedMs: Math.round(verdict.comparedMs),
    samples,
    trace:
      trace === null
        ? { captured: false, path: '', summary: 'tracing not captured for this run', problems: [] }
        : { captured: trace.captured, path: trace.path, summary: trace.summary, problems: trace.problems },
    issue: issue === null ? { reference: '', detail: 'no issue filed (run did not breach)' } : { reference: issue.reference, detail: issue.detail },
    quarantine,
    host: hostProfile(),
    recordedAt: new Date().toISOString(),
  };

  const reportFile = reportPath(budget.id);
  mkdirSync(dirname(reportFile), { recursive: true });
  writeFileSync(reportFile, `${JSON.stringify(report, null, 2)}\n`, 'utf8');

  console.log(
    `[perf] ${budget.id}: ${verdict.explanation}\n` +
      `${formatSampleTable(samples)}\n` +
      `[perf] trace: ${report.trace.summary}` +
      (report.trace.problems.length === 0
        ? ''
        : `\n[perf] trace problems:\n  - ${report.trace.problems.join('\n  - ')}`) +
      `\n[perf] report: ${reportFile}` +
      (issue === null ? '' : `\n[perf] issue: ${issue.reference} (${issue.detail})`) +
      (quarantine === null ? '' : `\n[perf] quarantine: ${quarantine}`),
  );

  return { verdict, trace, issue, report };
}
