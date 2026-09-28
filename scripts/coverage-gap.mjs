// Task 039A: frontend coverage-gap reporter.
//
// Reads `frontend/coverage/coverage-summary.json` (emitted by
// `npm run test --prefix frontend -- --coverage` via the `json-summary`
// reporter in `frontend/vite.config.ts`) and lists every measured file below
// the 80% per-file target owned by gap-closure tasks 039B/039C.
//
// Contract:
// - One `COVERAGE_GAP:<path> <metric>=<pct>...` line per below-target file
//   (paths + counts only; never source excerpts, per the security policy).
// - Empty stdout + exit 0 means the 80% target is met (039B/039C are done).
// - This script is a report, not a gate: it exits 0 even with gaps. The CI
//   gate is the vitest `thresholds` floor in `vite.config.ts` plus
//   `scripts/presence-gate.mjs`.
// - Missing summary or tool version drift fails loudly (exit 2) with a version
//   message, never a silent zero-coverage pass.

import { readFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const TARGET_PCT = 80;
const METRICS = ['lines', 'branches', 'functions', 'statements'];

const scriptsDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptsDir, '..');
const frontendDir = join(repoRoot, 'frontend');

function fail(message) {
  process.stderr.write(`${message}\n`);
  process.exit(2);
}

function majorOf(version) {
  const cleaned = version.trim().replace(/^[~^>=< ]+/, '');
  const match = /^(\d+)\./.exec(cleaned);
  return match ? match[1] : undefined;
}

// Coverage tool version drift: vitest and @vitest/coverage-v8 must share a
// major (both lockfile-pinned in frontend/package.json).
let pkg;
try {
  pkg = JSON.parse(readFileSync(join(frontendDir, 'package.json'), 'utf8'));
} catch {
  fail('COVERAGE_TOOL_VERSION_MISMATCH: cannot read frontend/package.json.');
}
const devDeps = pkg.devDependencies ?? {};
const vitestMajor = devDeps['vitest'] === undefined ? undefined : majorOf(String(devDeps['vitest']));
const coverageMajor =
  devDeps['@vitest/coverage-v8'] === undefined ? undefined : majorOf(String(devDeps['@vitest/coverage-v8']));
if (vitestMajor === undefined || coverageMajor === undefined) {
  fail(
    `COVERAGE_TOOL_VERSION_MISMATCH: vitest (${devDeps['vitest'] ?? 'missing'}) and ` +
      `@vitest/coverage-v8 (${devDeps['@vitest/coverage-v8'] ?? 'missing'}) must both be pinned in frontend/package.json.`,
  );
}
if (vitestMajor !== coverageMajor) {
  fail(
    `COVERAGE_TOOL_VERSION_MISMATCH: vitest major ${vitestMajor} != @vitest/coverage-v8 major ${coverageMajor}; ` +
      `align the lockfile-pinned versions (see docs/coverage.md).`,
  );
}
if (devDeps['msw'] === undefined) {
  fail('COVERAGE_TOOL_VERSION_MISMATCH: msw must be pinned in frontend/package.json (see frontend/src/mocks/README.md).');
}

let summary;
try {
  summary = JSON.parse(readFileSync(join(frontendDir, 'coverage', 'coverage-summary.json'), 'utf8'));
} catch {
  fail(
    'COVERAGE_SUMMARY_MISSING: frontend/coverage/coverage-summary.json not found; ' +
      "run 'npm run test --prefix frontend -- --coverage' first.",
  );
}

const lines = [];
for (const key of Object.keys(summary).sort()) {
  if (key === 'total') {
    continue;
  }
  const entry = summary[key];
  const rel = relative(frontendDir, key).replace(/\\/g, '/');
  const display = rel.startsWith('..') ? key : rel;
  const below = [];
  for (const metric of METRICS) {
    const pct = entry?.[metric]?.pct;
    if (typeof pct !== 'number') {
      fail(`COVERAGE_SUMMARY_MALFORMED: ${display} is missing metric ${metric}.`);
    }
    if (pct < TARGET_PCT) {
      below.push(`${metric}=${pct.toFixed(1)}`);
    }
  }
  if (below.length > 0) {
    lines.push(`COVERAGE_GAP:${display} ${below.join(' ')}`);
  }
}

for (const line of lines) {
  process.stdout.write(`${line}\n`);
}
