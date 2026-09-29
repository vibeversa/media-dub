// Task 041D: the perf issue sink.
//
// The task requires that a breach "opens (or updates) a perf issue
// automatically per repo policy". There is no such policy in the repository
// yet (042B owns CI, and the report skill's `gh` usage is the only precedent),
// so this module *is* the policy, stated in one place:
//
//   * one open issue per budget id, found by a machine-readable marker in the
//     body rather than by title matching, so a retitled issue is still found;
//   * label `perf-budget`;
//   * a breach comments the samples onto the existing issue instead of opening
//     a new one, because a budget that breaches every week should produce a
//     weekly comment, not 40 issues;
//   * the body always carries the fixture, the budget version, the host profile
//     and the sample table, so the issue is diagnosable without the run log.
//
// Two sinks, chosen at run time, and never a hard failure: `gh` when it is on
// PATH and a token is in the environment, otherwise a markdown draft in the
// git-ignored artifact directory whose path is used as the issue reference. A
// missing sink downgrades "opened an issue" to "wrote a draft an operator can
// file"; it never downgrades the verdict, which is still a failing run.

import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';

import { BUDGET_VERSION, type Budget } from '../budgets.js';
import { formatSampleTable, type PerfSample } from './measure.js';

export const PERF_ISSUE_LABEL = 'perf-budget';

export function issueMarker(budgetId: string): string {
  return `<!-- perf-budget:${budgetId} -->`;
}

export interface IssueRequest {
  readonly budget: Budget;
  readonly samples: readonly PerfSample[];
  readonly comparedMs: number;
  readonly limitMs: number;
  readonly hostProfile: string;
  /** `run:<id>` for the CI run, or the local run marker. */
  readonly runReference: string;
  /** Set when a trace was attached; `''` when it was not. */
  readonly traceReference: string;
  readonly traceWarning: string;
  readonly verdict: string;
}

export interface IssueOutcome {
  readonly filed: boolean;
  readonly updated: boolean;
  /** A trackable reference: an issue URL, or `local:<path>`. */
  readonly reference: string;
  readonly detail: string;
}

function runGh(args: readonly string[]): { readonly ok: boolean; readonly out: string; readonly err: string } {
  // Argument array, never a shell string: a repository name or a body is not
  // allowed to become syntax.
  const result = spawnSync('gh', [...args], {
    shell: false,
    encoding: 'utf8',
    windowsHide: true,
    env: { ...process.env },
  });
  if (result.error !== undefined) {
    return { ok: false, out: '', err: result.error.message };
  }
  return { ok: result.status === 0, out: result.stdout ?? '', err: result.stderr ?? '' };
}

export function ghAvailable(): boolean {
  if ((process.env['GH_TOKEN'] ?? process.env['GITHUB_TOKEN'] ?? '') === '') {
    return false;
  }
  const probe = runGh(['--version']);
  return probe.ok;
}

export function renderIssueBody(request: IssueRequest): string {
  return [
    issueMarker(request.budget.id),
    '',
    `A run of the Task 041D performance gate measured **${request.budget.title}** at or over its budget.`,
    '',
    '| | |',
    '| --- | --- |',
    `| Budget | \`${request.budget.limitMs}ms\` (${request.budget.statistic}) |`,
    `| Measured | \`${String(Math.round(request.comparedMs))}ms\` |`,
    `| Verdict | ${request.verdict} |`,
    `| Budget set version | \`${BUDGET_VERSION}\` |`,
    `| Fixture | ${request.budget.fixture} |`,
    `| Requirement | ${request.budget.requirement} |`,
    `| Owning task | ${request.budget.owner} |`,
    `| Run | ${request.runReference} |`,
    `| Trace | ${request.traceReference === '' ? '_none attached_' : request.traceReference} |`,
    `| Trace warning | ${request.traceWarning === '' ? '_none_' : request.traceWarning} |`,
    '',
    '## Samples',
    '',
    formatSampleTable(request.samples),
    '',
    '## Host profile',
    '',
    '```',
    request.hostProfile,
    '```',
    '',
    '## What the budget measures',
    '',
    request.budget.measured,
    '',
    '## Declared limitations',
    '',
    ...request.budget.limitations.map((limitation) => `- ${limitation}`),
    '',
    '## How to re-baseline',
    '',
    'A budget number moves by editing `e2e/perf/budgets.ts` and adding an entry to',
    '`BUDGET_CHANGELOG` with the reason. `e2e/perf/budgets.spec.ts` fails a change that',
    'moves a number without a recorded reason, so this issue closing is the diff that',
    'does it.',
    '',
  ].join('\n');
}

/** Files the issue as a draft when no GitHub sink is configured. */
export function writeIssueDraft(path: string, request: IssueRequest): string {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, renderIssueBody(request), 'utf8');
  return `local:${path}`;
}

/**
 * Opens the perf issue, or comments on the open one for this budget.
 *
 * Returns what happened. Never throws: a breach that cannot reach GitHub is
 * still a breach, and the caller reports both facts.
 */
export function filePerfIssue(path: string, request: IssueRequest): IssueOutcome {
  const body = renderIssueBody(request);
  const title = `[perf] ${request.budget.title} over budget (${String(request.budget.limitMs)}ms)`;

  if (!ghAvailable()) {
    const reference = writeIssueDraft(path, request);
    return {
      filed: false,
      updated: false,
      reference,
      detail:
        'no GitHub sink (gh not on PATH or no GH_TOKEN/GITHUB_TOKEN in the environment); ' +
        `wrote a fileable draft at ${reference}. The run still fails; filing is a manual step.`,
    };
  }

  const search = runGh([
    'issue',
    'list',
    '--search',
    `${issueMarker(request.budget.id)} in:body state:open`,
    '--json',
    'number',
    '--limit',
    '5',
  ]);
  if (!search.ok) {
    const reference = writeIssueDraft(path, request);
    return {
      filed: false,
      updated: false,
      reference,
      detail: `gh issue list failed (${search.err.trim()}); wrote a draft at ${reference}.`,
    };
  }

  let existing: number | undefined;
  try {
    const parsed = JSON.parse(search.out.trim() === '' ? '[]' : search.out) as { number?: number }[];
    existing = parsed[0]?.number;
  } catch {
    existing = undefined;
  }

  if (existing !== undefined) {
    const comment = runGh(['issue', 'comment', String(existing), '--body-file', '-']);
    if (comment.ok) {
      return {
        filed: true,
        updated: true,
        reference: `https://github.com/${String(process.env['GITHUB_REPOSITORY'] ?? 'CHANGE_ME')}/issues/${String(existing)}`,
        detail: `updated the open perf issue #${String(existing)} for '${request.budget.id}'.`,
      };
    }
    const reference = writeIssueDraft(path, request);
    return {
      filed: false,
      updated: false,
      reference,
      detail: `gh issue comment on #${String(existing)} failed (${comment.err.trim()}); wrote a draft at ${reference}.`,
    };
  }

  const create = spawnSync('gh', ['issue', 'create', '--title', title, '--label', PERF_ISSUE_LABEL, '--body-file', '-'], {
    shell: false,
    encoding: 'utf8',
    windowsHide: true,
    input: body,
    env: { ...process.env },
  });
  if (create.error === undefined && create.status === 0) {
    return {
      filed: true,
      updated: false,
      reference: (create.stdout ?? '').trim(),
      detail: `opened a new perf issue for '${request.budget.id}'.`,
    };
  }

  const reference = writeIssueDraft(path, request);
  return {
    filed: false,
    updated: false,
    reference,
    detail: `gh issue create failed (${(create.stderr ?? create.error?.message ?? '').trim()}); wrote a draft at ${reference}.`,
  };
}
