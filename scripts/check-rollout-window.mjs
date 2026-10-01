#!/usr/bin/env node
// The rollout-window gate (Task 043B). Thin CLI over tools/rollout-window.mjs.
//
// WHY A SEPARATE GATE AND NOT A BLOCK IN deploy/verify.sh's PYTHON
// -----------------------------------------------------------------
// `deploy/verify.sh`'s structural tier answers questions about YAML shapes, and
// it has a parsed view of every manifest. This gate answers questions about
// (a) C# migration source, (b) three JSON records, and (c) the declared value
// of a config key in three different file formats. One gate per question, in
// the language that can read the question, beats one gate that half-reads
// everything. Both run in `deploy/verify.sh` and both run in CI.
//
// FAIL-CLOSED, AND NOT OPTIONALLY
// -------------------------------
// No migrations directory, an unreadable record, a missing declaration file: each
// is a FAILURE with a named reason. None of them is a skip. A window gate that
// skips is worse than no window gate, because it reports that a rule holds when
// nothing checked it - and the rule is the one that says a dropped column is a
// data-loss event.
//
// The clock is injectable (`--today`) so the staleness rules are testable and so
// a CI run on the day a record expires fails for that reason and not because
// somebody's date library has an opinion.
//
// MACHINE-READABLE OUTPUT
// -----------------------
//   ROLLOUT_GATE_RESULT reason=<REASON> status=<PASS|FAIL> migrations=<n> findings=<n>
// with REASONS in docs/ci-branch-protection.md §2.

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  AUTHORIZATION_SOURCE_GLOBS,
  FLAG_DECLARATION_FILES,
  MIGRATIONS_DIR,
  auditCompatMatrix,
  auditFlagRegister,
  auditMigrations,
  auditRehearsalRecord,
  readMigrationSources,
} from '../tools/rollout-window.mjs';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

/** Closed set, documented in docs/ci-branch-protection.md §2. */
const REASONS = Object.freeze({
  OK: 'OK',
  MIGRATION_NOT_ADDITIVE: 'MIGRATION_NOT_ADDITIVE',
  LEDGER_INVALID: 'LEDGER_INVALID',
  MATRIX_INCOMPLETE: 'MATRIX_INCOMPLETE',
  FLAGS_UNDECLARED: 'FLAGS_UNDECLARED',
  REHEARSAL_STALE: 'REHEARSAL_STALE',
  INPUT_INVALID: 'INPUT_INVALID',
});

/**
 * How stale a record may be.
 *
 * 90 days is a quarter. The reason it is not longer is that the two records age
 * differently and neither ages gracefully: a flag nobody has reviewed in a year
 * is a permanent untested code path, and a rollback path nobody has exercised in
 * a year is a path nobody has exercised. The reason it is not shorter is that
 * the compatibility matrix is expensive - it needs a previous image and a
 * previous schema - and a staleness window shorter than the cost of refreshing
 * it is a gate that gets disabled.
 */
const MAX_RECORD_AGE_DAYS = 90;

function parseArgs(argv) {
  const options = { today: new Date().toISOString().slice(0, 10) };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--today') {
      options.today = argv[i + 1];
      i += 1;
    } else if (arg.startsWith('--today=')) {
      options.today = arg.slice('--today='.length);
    } else if (arg === '--help' || arg === '-h') {
      process.stdout.write('usage: check-rollout-window.mjs [--today YYYY-MM-DD]\n');
      process.exit(0);
    } else {
      process.stderr.write(`::error::check-rollout-window: unknown argument ${arg}\n`);
      process.exit(2);
    }
  }
  if (!/^\d{4}-\d{2}-\d{2}$/.test(options.today)) {
    process.stderr.write(`::error::--today must be YYYY-MM-DD, got '${options.today}'\n`);
    process.exit(2);
  }
  return options;
}

function readJson(relativePath, errors) {
  try {
    return JSON.parse(readFileSync(join(REPO_ROOT, relativePath), 'utf8'));
  } catch (error) {
    errors.push(`${relativePath}: ${error.message}`);
    return null;
  }
}

/** Every file under a glob prefix, as `{path, text}` relative to the repo root. */
function readSourcesUnder(prefix) {
  const absolute = join(REPO_ROOT, prefix);
  const out = [];
  let entries;
  try {
    entries = readdirSync(absolute, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const entry of entries.sort((a, b) => a.name.localeCompare(b.name))) {
    const path = join(absolute, entry.name);
    if (entry.isDirectory()) {
      for (const nested of readSourcesUnder(`${prefix}/${entry.name}`)) out.push(nested);
    } else if (entry.isFile() && entry.name.endsWith('.cs')) {
      out.push({ path: `${prefix}/${entry.name}`, text: readFileSync(path, 'utf8') });
    }
  }
  return out;
}

function main() {
  const options = parseArgs(process.argv.slice(2));
  const errors = [];
  const sections = [];

  // --- R2: the migration window ------------------------------------------------
  const ledger = readJson('deploy/rollout/contract-ledger.json', errors);
  const migrationsDir = join(REPO_ROOT, MIGRATIONS_DIR);
  let migrationResult = { ok: false, findings: [], ledgerErrors: [], stats: {} };
  try {
    if (!statSync(migrationsDir).isDirectory()) throw new Error('not a directory');
    migrationResult = auditMigrations({
      sources: readMigrationSources(migrationsDir),
      ledger,
      today: options.today,
    });
  } catch (error) {
    errors.push(`${MIGRATIONS_DIR}: ${error.message}`);
  }
  if (ledger !== null) {
    for (const message of migrationResult.ledgerErrors) {
      sections.push({ id: 'R2-ledger', reason: REASONS.LEDGER_INVALID, detail: message });
    }
  }
  for (const item of migrationResult.findings) {
    sections.push({
      id: 'R2-migrations',
      reason: REASONS.MIGRATION_NOT_ADDITIVE,
      detail: `${item.migration}${item.line > 0 ? `:${item.line}` : ''} [${item.verdict}] ${item.subject}: ${item.reason}`,
    });
  }
  process.stdout.write(
    `migration window: ${migrationResult.stats.migrations ?? 0} migration(s), ` +
      `${migrationResult.stats.expand ?? 0} expand op(s), ${migrationResult.stats.contract ?? 0} contract op(s), ` +
      `${migrationResult.stats.ledgered ?? 0} ledgered\n`,
  );

  // --- R3: the compatibility matrix --------------------------------------------
  const matrix = readJson('deploy/rollout/compat-matrix.json', errors);
  let openapi = null;
  try {
    openapi = JSON.parse(readFileSync(join(REPO_ROOT, 'src/DubbingPlatform.Api/OpenApi/openapi.v1.json'), 'utf8'));
  } catch (error) {
    errors.push(`src/DubbingPlatform.Api/OpenApi/openapi.v1.json: ${error.message}`);
  }
  if (matrix !== null && openapi !== null) {
    const matrixResult = auditCompatMatrix({ matrix, openapi, today: options.today, maxAgeDays: MAX_RECORD_AGE_DAYS });
    // Printed, always, whether or not the gate is green. An exemption that is
    // invisible in a passing run is a suppression with extra steps, and the one
    // reader who needs to see it is the one reading a green build.
    for (const exemption of matrixResult.exemptions ?? []) {
      process.stdout.write(
        `EXEMPTED: compatibility cell '${exemption.cell}' is not run. Reason: ${exemption.reason} ` +
          `Unblocked when: ${exemption.unblockedWhen} Review by: ${exemption.reviewBy}\n`,
      );
    }
    for (const item of matrixResult.findings) {
      sections.push({ id: 'R3-matrix', reason: REASONS.MATRIX_INCOMPLETE, detail: `[${item.verdict}] ${item.subject}: ${item.reason}` });
    }
  }

  // --- R4: the flag register ---------------------------------------------------
  const register = readJson('deploy/rollout/flags.json', errors);
  if (register !== null) {
    const files = {};
    for (const relativePath of FLAG_DECLARATION_FILES) {
      try {
        files[relativePath] = readFileSync(join(REPO_ROOT, relativePath), 'utf8');
      } catch (error) {
        errors.push(`${relativePath}: ${error.message}`);
      }
    }
    const authorizationSources = AUTHORIZATION_SOURCE_GLOBS.flatMap((prefix) => readSourcesUnder(prefix));
    const flagResult = auditFlagRegister({ register, files, today: options.today, authorizationSources });
    for (const item of flagResult.findings) {
      sections.push({ id: 'R4-flags', reason: REASONS.FLAGS_UNDECLARED, detail: `[${item.verdict}] ${item.key}: ${item.reason}` });
    }
    process.stdout.write(`flag register: ${(register.flags ?? []).length} flag(s), ${authorizationSources.length} authorization source file(s) scanned\n`);
  }

  // --- R5: the rollback rehearsal ----------------------------------------------
  const rehearsal = readJson('deploy/rollout/rollback-rehearsal.json', errors);
  if (rehearsal !== null) {
    const rehearsalResult = auditRehearsalRecord({ record: rehearsal, today: options.today, maxAgeDays: MAX_RECORD_AGE_DAYS });
    for (const item of rehearsalResult.findings) {
      sections.push({ id: 'R5-rehearsal', reason: REASONS.REHEARSAL_STALE, detail: `[${item.verdict}] ${item.subject}: ${item.reason}` });
    }
  }

  // --- verdict -----------------------------------------------------------------
  if (errors.length > 0) {
    sections.push({ id: 'input', reason: REASONS.INPUT_INVALID, detail: errors.join('; ') });
  }

  for (const section of sections) {
    process.stdout.write(`FAIL: ${section.id} ${section.reason}: ${section.detail}\n`);
  }
  const status = sections.length === 0 ? 'PASS' : 'FAIL';
  const reason = sections.length === 0
    ? REASONS.OK
    : // A missing or unreadable INPUT is reported as itself, not as the rule it
      // prevented from being checked. "No migrations were scanned" and "no
      // migration is non-additive" are different findings and an operator acts
      // on them differently.
      (sections.some((section) => section.reason === REASONS.INPUT_INVALID)
        ? REASONS.INPUT_INVALID
        : sections[0].reason);

  process.stdout.write(`\n== result: ${status === 'PASS' ? 'all rollout-window checks passed' : `${sections.length} finding(s)`} ==\n`);
  process.stdout.write(
    `ROLLOUT_GATE_RESULT reason=${reason} status=${status} migrations=${migrationResult.stats.migrations ?? 0} findings=${sections.length}\n`,
  );
  process.exit(status === 'PASS' ? 0 : 1);
}

main();
