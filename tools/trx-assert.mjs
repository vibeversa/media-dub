#!/usr/bin/env node
// Skipped-test gate (Task 042).
//
// THE PROBLEM THIS EXISTS FOR
// ---------------------------
// The repository's container-backed tests are `[SkippableFact]`. A runner without
// Docker, or a Testcontainers image that cannot be pulled, produces a *green*
// `dotnet test` in which nothing ran: xunit reports `Skipped`, the exit code is
// 0, and the job is green having proved nothing.
//
// That is not a hypothetical for a `SkippableFact` suite - it is the documented,
// intended behaviour of the attribute, and the only way to know it happened is to
// read the results. So the results are read.
//
// WHAT IT READS
// -------------
// VSTest TRX (`*.trx`), which is the only format `dotnet test` can emit that
// records an outcome per test. It is XML; this parses it with no dependency,
// because adding an XML library to a gate that runs before anything else is
// installed would make the gate the thing that breaks.
//
// EXIT CODES
//   0  every executed test passed and the forbidden set is empty
//   1  at least one test failed, errored, or was skipped
//   2  no TRX was found, or none of them parsed
//
// Exit 2 is distinct from exit 1 on purpose. "I could not read the results" and
// "the results contain a skip" call for different responses from whoever is
// looking, and a gate that reports both as the same thing trains people to
// ignore it.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { basename } from 'node:path';

import { parseTrx, countOutcomes, FORBIDDEN_OUTCOMES } from './trx-parse.mjs';

function usage(message) {
  process.stderr.write(`trx-assert: ${message}\n`);
  process.stderr.write('usage: node tools/trx-assert.mjs <dir-or-file> [--forbid-skipped] [--allow-category <name>] [--max <n>]\n');
  process.exit(2);
}

const argv = process.argv.slice(2);
if (argv.length === 0) {
  usage('a TRX file or results directory is required');
}

let target = null;
let forbidSkipped = false;
const allowedCategories = new Set();
let maxReported = 20;

for (let i = 0; i < argv.length; i += 1) {
  const flag = argv[i];
  if (flag === '--forbid-skipped') {
    forbidSkipped = true;
  } else if (flag === '--allow-category') {
    i += 1;
    if (argv[i] === undefined) {
      usage('--allow-category needs a category name');
    }
    allowedCategories.add(argv[i]);
  } else if (flag === '--max') {
    i += 1;
    const parsed = Number(argv[i]);
    if (!Number.isInteger(parsed) || parsed <= 0) {
      usage('--max needs a positive integer');
    }
    maxReported = parsed;
  } else if (flag.startsWith('--')) {
    usage(`unknown flag ${flag}`);
  } else if (target === null) {
    target = flag;
  } else {
    usage(`unexpected extra argument ${flag}`);
  }
}

if (target === null) {
  usage('a TRX file or results directory is required');
}

const resolved = resolve(target);
let stats;
try {
  stats = statSync(resolved);
} catch {
  usage(`no such file or directory: ${target}`);
}

function collectTrx(path, out) {
  if (stats.isFile()) {
    if (path.toLowerCase().endsWith('.trx')) {
      out.push(path);
    }
    return;
  }
  for (const entry of readdirSync(path, { withFileTypes: true })) {
    const full = join(path, entry.name);
    if (entry.isDirectory()) {
      collectTrx(full, out);
    } else if (entry.name.toLowerCase().endsWith('.trx')) {
      out.push(full);
    }
  }
}

const files = [];
collectTrx(resolved, files);
if (files.length === 0) {
  process.stderr.write(`trx-assert: no .trx files under ${target}\n`);
  process.stderr.write('A test run that produced no results file has not been verified. Failing rather than assuming success.\n');
  process.stderr.write('CI_GATE_RESULT reason=NO_TEST_RESULTS status=FAIL\n');
  process.exit(2);
}

const totals = { passed: 0, failed: 0, skipped: 0, other: 0 };
const skipped = [];
const failed = [];
let parsedFiles = 0;
let unparsed = [];

for (const file of files) {
  let document;
  try {
    document = parseTrx(readFileSync(file, 'utf8'));
  } catch (error) {
    unparsed.push(`${basename(file)}: ${error instanceof Error ? error.message : String(error)}`);
    continue;
  }
  parsedFiles += 1;
  for (const result of document) {
    const counts = countOutcomes([result]);
    totals.passed += counts.passed;
    totals.failed += counts.failed;
    totals.skipped += counts.skipped;
    totals.other += counts.other;
    if (result.outcome === 'Skipped' || result.outcome === 'NotExecuted') {
      const categories = result.categories ?? [];
      const exempt = categories.some((category) => allowedCategories.has(category));
      if (!exempt) {
        skipped.push({
          name: result.name,
          outcome: result.outcome,
          message: result.message ?? '',
          categories,
          file: basename(file),
        });
      }
    }
    if (result.outcome === 'Failed' || result.outcome === 'Error') {
      failed.push({ name: result.name, message: result.message ?? '', file: basename(file) });
    }
  }
}

if (parsedFiles === 0) {
  process.stderr.write(`trx-assert: found ${files.length} .trx file(s) but parsed none.\n`);
  for (const line of unparsed) {
    process.stderr.write(`  ${line}\n`);
  }
  process.stderr.write('CI_GATE_RESULT reason=TEST_RESULTS_UNREADABLE status=FAIL\n');
  process.exit(2);
}

const summary = `${files.length} TRX file(s), ${totals.passed} passed, ${totals.failed} failed, ${totals.skipped} skipped, ${totals.other} other`;
console.log(`trx-assert: ${summary}`);

if (allowedCategories.size > 0) {
  console.log(`trx-assert: category exemptions ${[...allowedCategories].join(', ')} (a soak test on a PR run is expected, not a missing check)`);
}

for (const entry of failed.slice(0, maxReported)) {
  console.error(`  FAILED  ${entry.file}: ${entry.name}`);
  if (entry.message !== '') {
    console.error(`          ${entry.message.split('\n')[0].slice(0, 200)}`);
  }
}
if (failed.length > maxReported) {
  console.error(`  ... and ${failed.length - maxReported} more failed test(s).`);
}

for (const entry of skipped.slice(0, maxReported)) {
  console.error(`  ::error title=SKIPPED_TEST::${entry.name} reported ${entry.outcome} instead of running.`);
  if (entry.message !== '') {
    console.error(`          ${entry.message.split('\n')[0].slice(0, 300)}`);
  }
  console.error(`          (${entry.file}${entry.categories.length > 0 ? `, categories: ${entry.categories.join(',')}` : ''})`);
}
if (skipped.length > maxReported) {
  console.error(`  ::error title=SKIPPED_TESTS::and ${skipped.length - maxReported} more skipped test(s).`);
}

if (failed.length > 0) {
  console.error(`CI_GATE_RESULT reason=TEST_FAILED status=FAIL failed=${failed.length}`);
  process.exit(1);
}
if (forbidSkipped && skipped.length > 0) {
  console.error('');
  console.error(`${skipped.length} test(s) were SKIPPED rather than executed.`);
  console.error('  This is what a runner without a usable container runtime looks like: xunit reports');
  console.error('  Skipped, `dotnet test` exits 0, and the job is green having run nothing. A skipped');
  console.error('  integration test is a missing check, so this gate fails.');
  console.error('  If a skip is genuinely expected for this job, add it to --allow-category with a');
  console.error('  reason in the workflow, rather than removing the flag.');
  console.error('CI_GATE_RESULT reason=TEST_SKIPPED status=FAIL skipped=' + skipped.length);
  process.exit(1);
}

console.log('CI_GATE_RESULT reason=OK status=PASS');
