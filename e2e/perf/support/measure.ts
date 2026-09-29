// Task 041D: measurement primitives, the median-of-three verdict, and the
// quarantine registry.
//
// Three things live here because they are one decision, not three:
//
//   * how a number is taken (so two budgets are not measured two ways);
//   * how samples become a verdict (the shared-runner rule);
//   * what happens to a sample that is slow while the verdict is not.
//
// The shared-runner rule, from the task: "breach verdict uses 3-run median
// before fail; single-run spike quarantines per 046 instead of failing release."
// That is implemented literally and is deliberately asymmetric:
//
//   median over budget          -> FAIL, with a trace, and a perf issue.
//   median under, one run over  -> PASS, plus a quarantine record naming an
//                                  owner and an issue. Never a silent pass.
//
// The asymmetry is the point. A slow runner must not block a release, and a
// real regression must not be explained away by "the runner was slow". Median
// separates those two cases only if the slow-but-not-median case leaves a
// record, which is what the quarantine registry is for.

import { BUDGET_VERSION, MEDIAN_OF_THREE, type Budget, type BudgetId } from '../budgets.js';

export interface PerfSample {
  readonly index: number;
  readonly valueMs: number;
  /** Free-form per-run evidence recorded in the log and the perf issue. */
  readonly detail?: Readonly<Record<string, string | number>>;
}

export type Verdict = 'pass' | 'spike-quarantined' | 'breach';

export interface VerdictResult {
  readonly verdict: Verdict;
  /** The statistic of the samples that the budget is compared against. */
  readonly comparedMs: number;
  readonly samples: readonly PerfSample[];
  /** Rendered explanation, used verbatim in the assertion message. */
  readonly explanation: string;
}

export function median(values: readonly number[]): number {
  if (values.length === 0) {
    throw new Error('median() of an empty sample set is not a measurement.');
  }
  const sorted = [...values].sort((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  if (sorted.length % 2 === 1) {
    return sorted[middle] ?? 0;
  }
  return ((sorted[middle - 1] ?? 0) + (sorted[middle] ?? 0)) / 2;
}

/**
 * Nearest-rank p95.
 *
 * Nearest-rank rather than interpolated, so the reported number is a sample
 * somebody actually observed. An interpolated percentile is a number no
 * interaction produced, which is a bad property for a number that fails a
 * release.
 */
export function percentile(values: readonly number[], fraction: number): number {
  if (values.length === 0) {
    throw new Error('percentile() of an empty sample set is not a measurement.');
  }
  const sorted = [...values].sort((a, b) => a - b);
  const rank = Math.ceil(fraction * sorted.length);
  const index = Math.min(sorted.length - 1, Math.max(0, rank - 1));
  return sorted[index] ?? 0;
}

export function summarize(samples: readonly PerfSample[], statistic: Budget['statistic']): number {
  const values = samples.map((sample) => sample.valueMs);
  return statistic === 'p95' ? percentile(values, 0.95) : median(values);
}

function describe(samples: readonly PerfSample[]): string {
  return samples.map((sample) => `#${String(sample.index + 1)}=${String(Math.round(sample.valueMs))}ms`).join(' ');
}

/**
 * Turns N runs into a verdict against a budget.
 *
 * `samples.length` must be at least `MEDIAN_OF_THREE`; a verdict computed from
 * one or two samples is the exact thing the rule exists to prevent, so the
 * helper refuses rather than quietly using what it was given.
 */
export function evaluateBudget(budget: Budget, samples: readonly PerfSample[]): VerdictResult {
  if (samples.length < MEDIAN_OF_THREE) {
    throw new Error(
      `Budget '${budget.id}' was given ${String(samples.length)} run(s). The shared-runner rule ` +
        `requires ${String(MEDIAN_OF_THREE)} before a verdict; a verdict from fewer samples is ` +
        'a single-run spike wearing a pass.',
    );
  }
  const comparedMs = summarize(samples, budget.statistic);
  const over = samples.filter((sample) => sample.valueMs > budget.limitMs);
  const rendered = `${describe(samples)} (${budget.statistic} of ${String(samples.length)} = ${String(
    Math.round(comparedMs),
  )}ms, budget ${String(budget.limitMs)}ms)`;

  if (comparedMs > budget.limitMs) {
    return {
      verdict: 'breach',
      comparedMs,
      samples,
      explanation:
        `BUDGET BREACH: ${budget.requirement}. ${rendered}. Over-budget runs: ${String(over.length)} ` +
        `of ${String(samples.length)}. Owner: ${budget.owner}. Fixture: ${budget.fixture}.`,
    };
  }

  if (over.length > 0) {
    return {
      verdict: 'spike-quarantined',
      comparedMs,
      samples,
      explanation:
        `${String(over.length)} of ${String(samples.length)} runs exceeded ${String(budget.limitMs)}ms ` +
        `but the ${budget.statistic} did not, so this is quarantined rather than failed per the ` +
        `shared-runner rule. ${rendered}. Quarantine record: ${quarantinePath(budget.id)} ` +
        `(owner ${budget.owner}).`,
    };
  }

  return { verdict: 'pass', comparedMs, samples, explanation: `within budget: ${rendered}.` };
}

// ---------------------------------------------------------------------------
// Quarantine registry
// ---------------------------------------------------------------------------

/**
 * A quarantined spike, as written to disk.
 *
 * A spike that is not written down is a spike that will be re-investigated from
 * scratch the next time it happens, and the second occurrence will be assumed
 * to be the same thing. `owner` and `issue` are required, not optional: a
 * quarantine with nobody responsible for clearing it is a suppression with extra
 * steps.
 */
export interface QuarantineRecord {
  readonly budgetId: BudgetId;
  readonly budgetVersion: string;
  readonly limitMs: number;
  readonly samples: readonly PerfSample[];
  readonly comparedMs: number;
  readonly owner: string;
  /** A trackable reference. The run's issue URL, or `local:<file>`. */
  readonly issue: string;
  readonly recordedAt: string;
  readonly note: string;
}

/** Directory for run artifacts. Git-ignored; see `.gitignore` (`tests/cross-layer/.artifacts/`). */
export const ARTIFACT_ROOT = 'tests/cross-layer/.artifacts/perf';

export function quarantinePath(id: BudgetId): string {
  return `${ARTIFACT_ROOT}/quarantine/${id}.json`;
}

export function reportPath(id: BudgetId): string {
  return `${ARTIFACT_ROOT}/reports/${id}.json`;
}

export function tracePath(id: BudgetId, run: number): string {
  return `${ARTIFACT_ROOT}/traces/${id}-run${String(run)}.zip`;
}

export function issueDraftPath(id: BudgetId): string {
  return `${ARTIFACT_ROOT}/issues/${id}.md`;
}

export function formatSampleTable(samples: readonly PerfSample[]): string {
  const rows = samples.map((sample) => {
    const detail =
      sample.detail === undefined
        ? ''
        : ` (${Object.entries(sample.detail)
            .map(([key, value]) => `${key}=${String(value)}`)
            .join(', ')})`;
    return `| ${String(sample.index + 1)} | ${String(Math.round(sample.valueMs))} |${detail} |`;
  });
  return ['| run | median-of-run ms | detail |', '| --- | --- | --- |', ...rows].join('\n');
}
