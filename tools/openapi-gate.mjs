#!/usr/bin/env node
// Contract-gate CLI (Task 042) - the decision half of `scripts/openapi-diff.sh`.
//
// The shell script does the I/O (materialize two documents from git, install and
// checksum-verify a pinned oasdiff, run it) and hands everything here. This file
// decides, and everything it decides is a pure function of its inputs, tested in
// `openapi-compat.test.mjs`.
//
// Precedence, and the reasoning behind it:
//
//   1. BREAKING over everything. A breaking change outranks every other
//      complaint because it is the only one that breaks a client that did
//      nothing wrong. A PR can be both breaking and unbumped; the reviewer needs
//      to hear "this breaks v1 clients" first.
//   2. UNREADABLE over BREAKING. If the tool's own output cannot be parsed, the
//      gate does not know whether the change was breaking, and a gate that
//      guesses is a gate that is eventually wrong in the expensive direction.
//   3. Then the version policy, whose failures are all about the change being
//      unreleasable rather than unmergeable.
//
//   4. `spurious` (a bump with no change) and `warn` never fail. Both are almost
//      always accidents, and neither breaks a client. A gate that blocks on them
//      teaches people to reach for `--no-verify`, which costs far more than the
//      mistake it prevents.
//
// USAGE
//   node tools/openapi-gate.mjs --base base.json --head head.json \
//        [--breaking-json breaking.json] [--base-ref main --head-ref HEAD] \
//        [--summary-file summary.md]
//
// Prints GitHub workflow annotations, a markdown report, and a final
// `CONTRACT_GATE_RESULT reason=<REASON> status=<PASS|FAIL> exit=<n>` line.
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  REASON,
  authScopeChanges,
  contractChanged,
  digest,
  evaluateVersionPolicy,
  isBlockingLevel,
  parseOasdiffOutput,
  renderBreakingReport,
  renderChangeAnnotations,
} from './openapi-compat.mjs';

const BUNDLE_REL = 'src/DubbingPlatform.Api/OpenApi/openapi.v1.json';

/** Documented, so the workflow's required-check names stay predictable. */
export const EXIT = {
  OK: 0,
  BREAKING: 1,
  BUMP_REQUIRED: 2,
  GATE_ERROR: 3,
};

function parseArgs(argv) {
  const args = {
    base: null,
    head: null,
    breakingJson: null,
    baseRef: 'main',
    headRef: 'HEAD',
    summaryFile: null,
  };
  for (let i = 0; i < argv.length; i += 1) {
    const flag = argv[i];
    const inline = flag.includes('=') ? flag.slice(flag.indexOf('=') + 1) : null;
    const take = () => {
      if (inline !== null) {
        return inline;
      }
      i += 1;
      return argv[i];
    };
    if (flag === '--base' || flag.startsWith('--base=')) {
      args.base = take();
    } else if (flag === '--head' || flag.startsWith('--head=')) {
      args.head = take();
    } else if (flag === '--breaking-json' || flag.startsWith('--breaking-json=')) {
      args.breakingJson = take();
    } else if (flag === '--base-ref' || flag.startsWith('--base-ref=')) {
      args.baseRef = take();
    } else if (flag === '--head-ref' || flag.startsWith('--head-ref=')) {
      args.headRef = take();
    } else if (flag === '--summary-file' || flag.startsWith('--summary-file=')) {
      args.summaryFile = take();
    } else {
      throw new Error(`unknown argument: ${flag}`);
    }
  }
  if (args.base === null || args.head === null) {
    throw new Error('--base and --head are required');
  }
  return args;
}

function readJson(file, label) {
  let text;
  try {
    text = readFileSync(file, 'utf8');
  } catch (error) {
    throw new Error(`${label} could not be read (${file}): ${error instanceof Error ? error.message : String(error)}`);
  }
  try {
    return JSON.parse(text);
  } catch (error) {
    throw new Error(`${label} is not valid JSON (${file}): ${error instanceof Error ? error.message : String(error)}`);
  }
}

/**
 * Decides the gate outcome from the two documents and oasdiff's own output.
 *
 * Exported so it can be driven directly by a test with synthetic tool output -
 * including the cases a real oasdiff will not produce on demand, which is
 * exactly the set of cases that matter (unparseable tool output, a tool that
 * found nothing, a tool that found something).
 *
 * @param {object} base the base document
 * @param {object} head the head document
 * @param {{ changes: Array<object>, format: string }} diff oasdiff's parsed output
 * @param {{ baseRef?: string, headRef?: string }} [labels]
 */
export function decide(base, head, diff, labels = {}) {
  const baseRef = labels.baseRef ?? 'main';
  const headRef = labels.headRef ?? 'HEAD';
  const changes = diff.changes ?? [];
  const version = evaluateVersionPolicy(base, head, { breaking: changes.length > 0 });

  // The auth-scope check runs here rather than being left to oasdiff, because the
  // pinned tool does not report it (verified: adding `security: [{Bearer:
  // ["projects.read"]}]` to an operation that inherited the document default
  // yields `[]`). It contributes changes to the same verdict, so a PR that both
  // removes a path and tightens auth is one failure with one set of annotations
  // rather than two verdicts a reviewer has to reconcile.
  const auth = authScopeChanges(base, head);
  const allChanges = [
    ...changes,
    ...auth.breaking.map((entry) => ({
      id: 'operation-auth-scope-tightened',
      level: 'ERR',
      text: entry.detail,
      operation: entry.operation,
      component: null,
      path: null,
    })),
  ];

  const annotations = [];
  let reason = REASON.OK;
  let exitCode = EXIT.OK;
  const lines = [];

  // A change the tool reported that this gate cannot classify as severe is
  // counted as blocking. `oasdiff breaking` emits only breaking changes today,
  // so a non-blocking level means either the tool changed what it emits or the
  // parser is looking at a shape it does not understand - and both are failures
  // that must not read as green.
  const blocking = allChanges.filter((change) => isBlockingLevel(change.level));
  const unclassified = allChanges.filter((change) => !isBlockingLevel(change.level));

  if (diff.format === 'text') {
    // oasdiff fell back to plain text, which means the JSON envelope it was asked
    // for did not parse. The changes are probably still right, but the tool is
    // not behaving as pinned, and that is a gate failure rather than a warning:
    // a version that silently changed its output format would otherwise change
    // what this gate can see without changing what it reports.
    reason = REASON.DIFF_UNREADABLE;
    exitCode = EXIT.GATE_ERROR;
    annotations.push(
      `::error file=${BUNDLE_REL}::oasdiff did not return the pinned --format json output; it returned ${diff.format}. Re-pin the tool or fix the parser before trusting this run.`,
    );
  } else if (allChanges.length > 0) {
    reason = REASON.BREAKING;
    exitCode = EXIT.BREAKING;
    annotations.push(
      `::error file=${BUNDLE_REL}::${allChanges.length} breaking change(s) between ${baseRef} and ${headRef} (${changes.length} from oasdiff, ${auth.breaking.length} auth-scope). Additive changes do not need a new API line; a breaking one does.`,
    );
    annotations.push(...renderChangeAnnotations(allChanges, { file: BUNDLE_REL }));
  } else if (version.status === REASON.VERSION_INVALID) {
    reason = version.status;
    exitCode = EXIT.GATE_ERROR;
    annotations.push(`::error file=${BUNDLE_REL}::${version.annotations[0]}`);
  } else if (version.status === REASON.VERSION_REGRESSED) {
    reason = version.status;
    exitCode = EXIT.BUMP_REQUIRED;
    annotations.push(`::error file=${BUNDLE_REL}::${version.annotations[0]}`);
  } else if (version.status === REASON.VERSION_BUMP_REQUIRED) {
    reason = version.status;
    exitCode = EXIT.BUMP_REQUIRED;
    annotations.push(`::error file=${BUNDLE_REL}::${version.annotations[0]}`);
  } else if (version.status === REASON.MAJOR_WITHOUT_ROUTE || version.status === REASON.ROUTE_WITHOUT_MAJOR) {
    reason = version.status;
    exitCode = EXIT.BUMP_REQUIRED;
    annotations.push(`::error file=${BUNDLE_REL}::${version.annotations[0]}`);
  }

  for (const note of version.annotations) {
    if (version.status === 'spurious' || (reason === REASON.OK && version.status !== REASON.OK)) {
      annotations.push(`::warning file=${BUNDLE_REL}::${note}`);
    }
  }
  for (const change of unclassified) {
    annotations.push(
      `::warning file=${BUNDLE_REL}::oasdiff reported '${change.id}' at level ${change.level}, which this gate does not classify as blocking. It is counted as breaking anyway; re-pin the tool if this is unexpected.`,
    );
  }
  for (const entry of auth.relaxed) {
    // Surfaced as a warning, not silence. Relaxing auth does not break a client,
    // but it is exactly the change a reviewer should be told about rather than
    // discovering in the diff, and the gate is the one place already reading
    // both documents.
    annotations.push(
      `::warning file=${BUNDLE_REL}::${entry.operation} ${entry.detail}. This does not break an existing client, but it is a security-relevant change - confirm it is intended.`,
    );
  }

  if (!contractChanged(base, head)) {
    lines.push('The contract is unchanged: no document diff, no version bump owed.');
  } else {
    lines.push(`The contract changed. Version ${version.baseVersion} -> ${version.headVersion}.`);
  }
  lines.push(`Breaking changes found: ${allChanges.length} (${blocking.length} blocking; ${changes.length} from oasdiff, ${auth.breaking.length} auth-scope).`);
  if (allChanges.length > 0) {
    lines.push('');
    lines.push(renderBreakingReport(allChanges));
  }
  if (auth.relaxed.length > 0) {
    lines.push('');
    lines.push('### Auth loosened (not a client break)');
    lines.push('');
    for (const entry of auth.relaxed) {
      lines.push(`- \`${entry.operation}\` ${entry.detail}`);
    }
  }

  return {
    reason,
    exitCode,
    annotations,
    report: lines.join('\n'),
    changes: allChanges,
    oasdiffChanges: changes,
    auth,
    version,
    summary: {
      baseRef,
      headRef,
      breaking: allChanges.length,
      blocking: blocking.length,
      oasdiffBreaking: changes.length,
      authBreaking: auth.breaking.length,
      authRelaxed: auth.relaxed.length,
      contractChanged: version.changed,
      baseVersion: version.baseVersion,
      headVersion: version.headVersion,
      reason,
      // The fingerprint covers the tool's findings AND this gate's own auth
      // findings, so a change in either changes the fingerprint. A fingerprint
      // that ignored the auth check would let two materially different verdicts
      // share an identity, which defeats the point of having one.
      fingerprint: digest(
        `${version.baseVersion}|${version.headVersion}|${allChanges.map((c) => `${c.id}:${c.text}`).join('|')}|relaxed:${auth.relaxed.map((r) => r.detail).join('|')}`,
      ),
    },
  };
}

function main(argv) {
  let args;
  try {
    args = parseArgs(argv);
  } catch (error) {
    process.stderr.write(`openapi-gate: ${error instanceof Error ? error.message : String(error)}\n`);
    process.stderr.write('CONTRACT_GATE_RESULT reason=BAD_ARGUMENT status=FAIL exit=3\n');
    return EXIT.GATE_ERROR;
  }

  let base;
  let head;
  try {
    base = readJson(args.base, 'the base document');
    head = readJson(args.head, 'the head document');
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    process.stderr.write(`openapi-gate: ${message}\n`);
    process.stdout.write(`::error file=${BUNDLE_REL}::${message}\n`);
    process.stdout.write('CONTRACT_GATE_RESULT reason=DOCUMENT_UNREADABLE status=FAIL exit=3\n');
    return EXIT.GATE_ERROR;
  }

  let diff = { changes: [], format: 'json' };
  if (args.breakingJson !== null) {
    let raw;
    try {
      raw = readFileSync(args.breakingJson, 'utf8');
    } catch (error) {
      process.stderr.write(`openapi-gate: oasdiff output could not be read: ${error instanceof Error ? error.message : String(error)}\n`);
      process.stdout.write('CONTRACT_GATE_RESULT reason=TOOL_FAILED status=FAIL exit=3\n');
      return EXIT.GATE_ERROR;
    }
    diff = parseOasdiffOutput(raw);
  }

  const result = decide(base, head, diff, { baseRef: args.baseRef, headRef: args.headRef });

  process.stdout.write(`${result.report}\n\n`);
  for (const annotation of result.annotations) {
    process.stdout.write(`${annotation}\n`);
  }
  process.stdout.write('\n');
  process.stdout.write(
    `CONTRACT_GATE_RESULT reason=${result.reason} status=${result.exitCode === 0 ? 'PASS' : 'FAIL'} exit=${result.exitCode} breaking=${result.summary.breaking} oasdiff=${result.summary.oasdiffBreaking} auth=${result.summary.authBreaking} base=${result.summary.baseVersion} head=${result.summary.headVersion}\n`,
  );

  if (args.summaryFile !== null) {
    const frontMatter = [
      '<!-- Generated by tools/openapi-gate.mjs. Do not edit. -->',
      '',
      '| field | value |',
      '| --- | --- |',
      `| base | \`${args.baseRef}\` |`,
      `| head | \`${args.headRef}\` |`,
      `| base version | ${result.summary.baseVersion} |`,
      `| head version | ${result.summary.headVersion} |`,
      `| contract changed | ${result.summary.contractChanged} |`,
      `| breaking changes | ${result.summary.breaking} (${result.summary.oasdiffBreaking} from oasdiff, ${result.summary.authBreaking} auth-scope) |`,
      `| auth loosened | ${result.summary.authRelaxed} |`,
      `| verdict | ${result.reason} |`,
      `| fingerprint | \`sha256:${result.summary.fingerprint.slice(0, 16)}\` |`,
      '',
    ].join('\n');
    try {
      writeFileSync(args.summaryFile, frontMatter + result.report + '\n', 'utf8');
    } catch (error) {
      process.stderr.write(`openapi-gate: could not write the summary file: ${error instanceof Error ? error.message : String(error)}\n`);
    }
  }

  return result.exitCode;
}

if (process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
