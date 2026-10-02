// Tests for the backup POLICY gate (Task 047).
//
// WHY A SEPARATE FILE FROM tools/backup-coverage.test.mjs
// ------------------------------------------------------
// `backup-coverage.test.mjs` owns what is COVERED: the scope file, the seed, the
// drill. This file owns what is PROMISED: the job, the tiers, the RPO/RTO, the
// escalation ladder, the drill record, and the release gate. They read different
// files and answer different questions, and the 043C report's D2/D6 finding was
// caused by two questions being answered from one source.
//
// THE TWO-HAND STRUCTURE, APPLIED HERE
// ------------------------------------
// Every test below does one of two things and never both:
//
//   1. break a REAL file, run the gate, assert the exact reason, put it back; or
//   2. assert the real repository passes, which is what stops test 1 from being
//      a test of a broken copy.
//
// The second kind is not decoration. A gate test that only ever runs against
// mutated inputs passes just as happily against a gate that checks nothing,
// because every mutation it performs lands in the failure path it already
// expected. This repository has now produced four variants of "a rule whose
// evidence for no-violations was a rule that had never matched anything" (043C
// Finding 1 and D2, 045 Finding 1, 046 Finding 3) and the two-hand structure is
// the structural answer to it.
import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, copyFileSync, unlinkSync, existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const GATE = join(REPO_ROOT, 'scripts/check-backup-policy.mjs');

const POLICY_PATH = join(REPO_ROOT, 'deploy/backup/policy.json');
const SCOPE_PATH = join(REPO_ROOT, 'deploy/backup/scope.json');
const CRONJOB_PATH = join(REPO_ROOT, 'deploy/k8s/backup-cronjob.yaml');
const DUMP_PATH = join(REPO_ROOT, 'deploy/backup/dump.sh');
const DOC_PATH = join(REPO_ROOT, 'docs/backup.md');
const MIGRATIONS = join(REPO_ROOT, 'src/DubbingPlatform.Infrastructure/Persistence/Migrations');

/**
 * The seven groups, fifth declaration. See the gate's header: a check whose
 * subject list comes from the same file as the data it checks always agrees with
 * itself. Four sources plus this one means a group cannot be removed without a
 * diff that looks like a deletion in four places.
 */
const REQUIRED_GROUPS = [
  { id: 'tenant-users', table: 'tenant_users' },
  { id: 'memberships', table: 'project_memberships' },
  { id: 'project-metadata', table: 'dubbing_projects' },
  { id: 'preferences', table: 'user_preferences' },
  { id: 'notifications', table: 'notifications' },
  { id: 'activity-events', table: 'activity_events' },
  { id: 'voice-preview-jobs', table: 'voice_preview_jobs' },
];

const policy = JSON.parse(readFileSync(POLICY_PATH, 'utf8'));
const scope = JSON.parse(readFileSync(SCOPE_PATH, 'utf8'));
const cronjob = readFileSync(CRONJOB_PATH, 'utf8');
const dumpScript = readFileSync(DUMP_PATH, 'utf8');
const doc = readFileSync(DOC_PATH, 'utf8');

/** Run the gate, returning its result line and its exit code. Never throws. */
function runGate(env = {}) {
  try {
    const stdout = execFileSync('node', [GATE], {
      cwd: REPO_ROOT,
      encoding: 'utf8',
      env: { ...process.env, ...env },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    return { code: 0, stdout };
  } catch (error) {
    return { code: error.status ?? 1, stdout: `${error.stdout ?? ''}${error.stderr ?? ''}` };
  }
}

const resultOf = (stdout) => stdout.match(/BACKUP_GATE_RESULT reason=(\S+) status=(\S+) groups=(\d+) tables=(\d+) gaps=(\d+) open=(\d+) blocking=(\d+)/);

/**
 * Whether the gate reported a finding carrying a specific reason.
 *
 * NOT `resultOf(...)[1] === reason`. The result line carries ONE reason - the
 * first failure, so it names the earliest problem in the file rather than
 * whichever check ran last - and this repository has open blocking gaps, so the
 * first reason is `GAP_BLOCKS_RELEASE` for every run and asserting on it would
 * make every injection test below assert nothing at all. That is precisely the
 * "rule that has never matched anything" defect, in a test file written to
 * prevent it: the first version of this helper compared against the result line
 * and every injection test passed for the wrong reason.
 *
 * The gate therefore prefixes every finding line with its own reason, and this
 * matches on that.
 */
const reported = (stdout, wanted) =>
  new RegExp(`\\[${wanted}\\]`).test(stdout) || new RegExp(`reason=${wanted}\\b`).test(stdout);

/**
 * Mutate one real file, run the gate, assert, restore. The restore runs in a
 * `finally` so a failing assertion cannot leave the repository mutated for the
 * next test - a test suite that corrupts the tree on failure is a debugging
 * session disguised as a test run.
 */
function withMutation(path, mutate, assertions) {
  const backup = `${path}.bak-test`;
  copyFileSync(path, backup);
  try {
    const original = readFileSync(path, 'utf8');
    writeFileSync(path, mutate(original), 'utf8');
    assertions(runGate());
  } finally {
    copyFileSync(backup, path);
    unlinkSync(backup);
  }
}

// ============================ the real tree, green =============================

test('the coverage and policy checks pass on the real tree; the gate is red ONLY for open blocking gaps', () => {
  // The repository is NOT expected to pass this gate, and a test that asserted
  // it did would be asserting something false. Two gaps are genuinely open and
  // genuinely blocking (D1a the media half, F1 no full-system drill has ever
  // run) and the whole point of instruction 5 is that they make the gate red.
  //
  // So this test asserts the *narrow* claim instead, and the narrow claim is the
  // one that matters: the red is caused by the registered gaps and by nothing
  // else. A gate that is red for a reason nobody registered is a broken gate,
  // and this is what distinguishes the two.
  const { code, stdout } = runGate();
  const result = resultOf(stdout);
  assert.ok(result, `the gate emitted no BACKUP_GATE_RESULT line:\n${stdout}`);
  assert.equal(result[2], 'FAIL', 'the gate reports PASS while two blocking gaps are open. That is the failure mode this gate exists to prevent, and it is the one bug that would matter most.');
  assert.equal(code, 1, 'the gate exited zero while reporting FAIL');

  // `NOTE` is excluded: a note is reported and never counted, and the summary
  // lists both. Filtering it here keeps the assertion about FAILING reasons,
  // which is what "red ONLY for open blocking gaps" means.
  const reasons = [...stdout.matchAll(/^ {2}- \[([A-Z_]+)\]/gm)]
    .map((m) => m[1])
    .filter((r) => r !== 'NOTE');
  const unique = [...new Set(reasons)].sort();
  assert.deepEqual(
    unique,
    ['GAP_BLOCKS_RELEASE'],
    `the gate is red for reasons other than the registered open gaps: ${unique.join(', ')}\n${stdout}`,
  );
  // And the notes are not empty: the unrun full-system drill has to be visible
  // in the output, because an overdue drill that nobody can see is waived.
  assert.ok(
    /\[NOTE\].*fullSystem.*never run/.test(stdout),
    `the unrun full-system drill is not reported as a note:\n${stdout}`,
  );
  // The count has to be exactly the number of open blocking gaps in the file, so
  // "red for the right reason" cannot quietly become "red for the right reason
  // plus one more".
  assert.equal(
    Number(result[7]),
    policy.gaps.entries.filter((entry) => entry.status === 'open' && entry.blocking === true).length,
    'the result line does not report the number of open blocking gaps in the register',
  );
  assert.ok(Number(result[7]) >= 1, 'the register has no open blocking gap, so this test proves nothing about the rule it exists to prove');

  // And the coverage half is green, which is the half this task added.
  for (const line of [
    'scope.json declares all 7 required entity groups',
    'policy.json lists all 7 required entity groups',
    'the CronJob declares a coverage line for each of the 7 groups',
    'dump.sh dumps 10 table(s)',
    'migration-created tables have a declared tier',
    'escalation ladder has 3 levels',
  ]) {
    assert.ok(stdout.includes(line), `the coverage/policy half of the gate is not green: "${line}" is missing\n${stdout}`);
  }
});

test('the gate would pass if the open blocking gaps were closed, and this is proved, not assumed', () => {
  // The other half of the previous test. Closing the two blocking gaps in a
  // mutation must turn the gate green: if it does not, then the gate is red for
  // a THIRD reason that the previous test's "only GAP_BLOCKS_RELEASE" assertion
  // would have missed, because that assertion looks at reason names and a
  // non-gap failure is not one of them.
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      for (const entry of data.gaps.entries) {
        if (entry.status === 'open' && entry.blocking === true) {
          entry.status = 'fixed';
          entry.blocking = false;
        }
      }
      return JSON.stringify(data, null, 2);
    },
    ({ code, stdout }) => {
      const result = resultOf(stdout);
      assert.equal(result?.[2], 'PASS', `closing the open blocking gaps did not turn the gate green:\n${stdout}`);
      assert.equal(code, 0, 'the gate exited non-zero while reporting PASS');
    },
  );
});

test('the gate reads a non-trivial number of tables, so its checks are not vacuous', () => {
  // The vacuity guard for the whole file. If a CreateTable regex or a JSON key
  // stopped matching, the gate would find nothing to complain about and PASS -
  // so the count of what it read is asserted against the migrations directly,
  // here, outside the gate.
  const created = new Set();
  for (const file of readdirSync(MIGRATIONS)) {
    if (!file.endsWith('.cs') || file.endsWith('.Designer.cs')) continue;
    const source = readFileSync(join(MIGRATIONS, file), 'utf8');
    for (const match of source.matchAll(/migrationBuilder\.CreateTable\(\s*\n\s*name: "([a-z_]+)"/g)) {
      created.add(match[1]);
    }
  }
  assert.ok(created.size > 20, `only ${created.size} tables read from the migrations; the regex is probably wrong`);

  const { stdout } = runGate();
  assert.match(
    stdout,
    new RegExp(`all ${created.size} migration-created tables have a declared tier`),
    `the gate did not report classifying all ${created.size} tables:\n${stdout}`,
  );
});

test('the result line reports the counts a release decision is made on', () => {
  const { stdout } = runGate();
  const result = resultOf(stdout);
  assert.equal(Number(result[3]), REQUIRED_GROUPS.length, 'groups= does not report seven');
  assert.ok(Number(result[4]) >= REQUIRED_GROUPS.length, 'tables= is smaller than the seven groups, so the count is not what it claims to be');
  assert.equal(Number(result[5]), policy.gaps.entries.length, 'gaps= does not match the register');
  assert.equal(
    Number(result[6]),
    policy.gaps.entries.filter((entry) => entry.status === 'open').length,
    'open= does not match the register',
  );
});

// ============================ R1: the coverage =================================

test('R1: the backup JOB declares a coverage line per group, naming its table', () => {
  // This is the check that did not exist before Task 047, and its absence is gap
  // D7: scope.json's comment and docs/backup.md's table both described
  // deploy/k8s/backup-cronjob.yaml, which was never written, and every coverage
  // check passed because the sources agreed with each other.
  for (const required of REQUIRED_GROUPS) {
    const match = cronjob.match(new RegExp(`dubbing\\.io/backup-group-\\d+-${required.id}:\\s*"([^"]+)"`));
    assert.ok(match, `the CronJob has no coverage line for the group "${required.id}"`);
    assert.ok(
      match[1].includes(`table=${required.table}`),
      `the coverage line for "${required.id}" does not name table=${required.table}: "${match[1]}"`,
    );
  }
  const lines = [...cronjob.matchAll(/dubbing\.io\/backup-group-\d+-([a-z-]+):/g)];
  assert.equal(
    lines.length,
    REQUIRED_GROUPS.length,
    `the CronJob declares ${lines.length} coverage lines and the task names ${REQUIRED_GROUPS.length}`,
  );
});

test('removing a group from the CronJob alone fails the gate (D7, proved by injection)', () => {
  withMutation(CRONJOB_PATH, (text) => text.replace(/^\s*dubbing\.io\/backup-group-5-notifications:.*\n/m, ''), ({ code, stdout }) => {
    assert.ok(reported(stdout, 'COVERAGE_GROUPS_MISSING'), `expected a COVERAGE_GROUPS_MISSING finding:\n${stdout}`);
    assert.equal(code, 1);
    assert.match(stdout, /notifications/, 'the finding does not name the group that was removed');
  });
});

test('a coverage line that names the group but not the table fails (D7 again, one level down)', () => {
  // The group is present, the annotation is present, and the job still does not
  // say which table it dumps. A checklist line with no table in it is a claim.
  withMutation(
    CRONJOB_PATH,
    (text) => text.replace(/(dubbing\.io\/backup-group-5-notifications:\s*")[^"]+(")/, '$1notifications, priority=4$2'),
    ({ stdout }) => {
      assert.ok(reported(stdout, 'COVERAGE_DRIFT'), `expected a COVERAGE_DRIFT finding:\n${stdout}`);
      assert.match(stdout, /notifications/, 'the finding does not name the group');
    },
  );
});

test('dropping a table from dump.sh fails even though the annotation still claims it', () => {
  // The two can only be changed together to go unnoticed, which is the property
  // that makes the duplication worth having.
  withMutation(
    DUMP_PATH,
    (text) => text.replace(/^\s*"notifications:notifications"\n/m, ''),
    ({ stdout }) => {
      assert.ok(reported(stdout, 'COVERAGE_DRIFT'), `expected a COVERAGE_DRIFT finding:\n${stdout}`);
      assert.match(stdout, /notifications/, 'the finding does not name the table that is no longer dumped');
    },
  );
});

test('dropping a PARENT from dump.sh fails: a dump without its parents restores incoherently', () => {
  withMutation(
    DUMP_PATH,
    (text) => text.replace(/^\s*"speakers"\n/m, ''),
    ({ stdout }) => {
      assert.ok(reported(stdout, 'COVERAGE_DRIFT'), `expected a COVERAGE_DRIFT finding:\n${stdout}`);
      assert.match(stdout, /speakers/, 'the finding does not name the parent');
    },
  );
});

test('removing a group from scope.json alone fails, as it did for 043C (D2)', () => {
  withMutation(
    SCOPE_PATH,
    (text) => text.replace(/\s*\{\s*\n\s*"id": "notifications",[\s\S]*?\n\s*\},?\n/, '\n'),
    ({ stdout }) => {
      assert.ok(
        ['COVERAGE_GROUPS_MISSING', 'DURABLE_TABLE_UNCLASSIFIED'].includes(resultOf(stdout)?.[1]),
        `expected a coverage or classification failure:\n${stdout}`,
      );
      assert.match(stdout, /notifications/, 'the finding does not name the group');
    },
  );
});

// ===================== the "new table, no backup entry" edge ===================

test('a new table with no tier assignment fails the gate', () => {
  // The edge case in the task, and the one worth proving: adding a migration is
  // a normal thing to do, and nothing about it should make the release
  // un-shippable by accident. It SHOULD make the release un-shippable until the
  // policy says where the table is backed up.
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      // Stand in for a future migration's table: remove a classification without
      // removing the table, which is exactly the state a new table is in.
      data.tableTiers.primaryBaseBackup = data.tableTiers.primaryBaseBackup.filter((t) => t !== 'quality_results');
      return JSON.stringify(data, null, 2);
    },
    ({ code, stdout }) => {
      assert.ok(reported(stdout, 'DURABLE_TABLE_UNCLASSIFIED'), `expected a DURABLE_TABLE_UNCLASSIFIED finding:\n${stdout}`);
      assert.equal(code, 1);
      assert.match(stdout, /quality_results/, 'the finding does not name the unclassified table');
    },
  );
});

test('a tier for a table no migration creates fails: a coverage entry describing nothing', () => {
  // The other direction, which is easier to miss. A table is dropped from a
  // migration and its tier entry is left behind, and the register then describes
  // a table that does not exist as if it were covered.
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      data.tableTiers.primaryBaseBackup.push('a_table_that_was_never_created');
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.ok(reported(stdout, 'DURABLE_TABLE_UNCLASSIFIED'), `expected a DURABLE_TABLE_UNCLASSIFIED finding:\n${stdout}`);
      assert.match(stdout, /a_table_that_was_never_created/, 'the finding does not name the stale entry');
    },
  );
});

test('a table in the selectable archive but not in the coverage scope fails (no retention, no priority)', () => {
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      data.tableTiers.selectableArchive.push('review_items');
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.ok(reported(stdout, 'COVERAGE_DRIFT'), `expected a COVERAGE_DRIFT finding:\n${stdout}`);
      assert.match(stdout, /review_items/, 'the finding does not name the table');
    },
  );
});

test('adding a queue table to the selectable archive fails: it must never be dumped', () => {
  // The failure mode in the other direction. The outbox redrives from itself, so
  // an archived outbox is a redelivery-ordering hazard sitting in an archive a
  // responder would restore on a restore day.
  withMutation(
    DUMP_PATH,
    (text) => text.replace(/^(\s*)"preferences:user_preferences"$/m, '$1"outbox_message:outbox_message"'),
    ({ stdout }) => {
      assert.ok(reported(stdout, 'COVERAGE_DRIFT'), `expected a COVERAGE_DRIFT finding:\n${stdout}`);
      assert.match(stdout, /outbox_message/, 'the finding does not name the prohibited table');
    },
  );
});

// ============================== R3: RPO and RTO =================================

test('R3: every RPO target has a number, a mechanism and a source', () => {
  const targets = policy.rpo?.targets ?? [];
  assert.ok(targets.length >= 2, `only ${targets.length} RPO target(s); the plan requires that an expectation exists`);
  for (const target of targets) {
    assert.ok(target.minutes >= 1, `the RPO for ${target.scope} has no numeric minutes`);
    assert.ok(target.mechanism.length > 10, `the RPO for ${target.scope} names no mechanism`);
    assert.match(target.source, /docs\//, `the RPO for ${target.scope} does not link a document`);
  }
});

test('R3: the RTO exists, and a target is not presented as a measurement', () => {
  assert.ok(policy.rto.targetMinutes >= 1, 'no numeric RTO target');
  assert.ok(policy.rto.consequence.length > 20, 'the RTO declares no consequence for missing it');
  // The distinction the whole section turns on: a target with measured=false is
  // honest, and a target claiming measured=true with no date is a measurement
  // claim with nothing behind it.
  assert.equal(policy.rto.measured, false, 'the RTO claims to be measured');
  assert.equal(policy.rto.measuredOn, null, 'the RTO claims a measurement date while reporting measured=false');
});

test('an RTO marked measured with no date fails (R3, proved by injection)', () => {
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      data.rto.measured = true;
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.ok(reported(stdout, 'RTO_UNDECLARED'), `expected an RTO_UNDECLARED finding:\n${stdout}`);
      assert.match(stdout, /measuredOn/, 'the finding does not explain what is missing');
    },
  );
});

test('an RPO target with a number but no mechanism fails: an RPO without a mechanism is a wish', () => {
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      data.rpo.targets[0].mechanism = '';
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.ok(reported(stdout, 'RPO_UNDECLARED'), `expected an RPO_UNDECLARED finding:\n${stdout}`);
    },
  );
});

// ============================= R3: escalation ==================================

test('R3: the escalation ladder is callable - role, SLA, trigger and contact on every level', () => {
  const ladder = policy.escalation?.ladder ?? [];
  assert.ok(ladder.length >= 2, `only ${ladder.length} escalation level(s); a single contact is not a ladder`);
  for (const level of ladder) {
    assert.ok(level.level && level.role, 'an escalation level names no role');
    assert.ok(level.slaMinutes >= 1, `escalation level ${level.level} has no SLA`);
    assert.ok(level.triggers.length > 0, `escalation level ${level.level} lists no trigger`);
    assert.ok(level.contact.length > 0, `escalation level ${level.level} has no contact field at all`);
  }
  // CHANGE_ME contacts are PERMITTED and asserted to be present, not absent.
  // This repository ships placeholders and the rule is that they are visible;
  // a gate that failed on the placeholder would fail on the repository and pass
  // on a copy with a real phone number pasted in, which is backwards.
  const placeholders = ladder.filter((level) => level.contact === 'CHANGE_ME');
  assert.ok(
    placeholders.length > 0,
    'no escalation contact is a CHANGE_ME placeholder; if these have been resolved, the rule about replacing them belongs in the runbook, not in this assertion',
  );
});

test('removing every escalation trigger fails: a phone tree nobody calls', () => {
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      data.escalation.ladder[0].triggers = [];
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.ok(reported(stdout, 'ESCALATION_INCOMPLETE'), `expected an ESCALATION_INCOMPLETE finding:\n${stdout}`);
      assert.match(stdout, /no trigger/, 'the finding does not say what is missing');
    },
  );
});

test('dropping the "overdue, not waived" rule fails: it is the rule, and it has to be written down', () => {
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      delete data.escalation.drillUnavailable;
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.ok(reported(stdout, 'ESCALATION_INCOMPLETE'), `expected an ESCALATION_INCOMPLETE finding:\n${stdout}`);
    },
  );
});

// ============================ R2: the drill record =============================

test('R2: the drill is RECORDED - date, scope, result, gaps, owner', () => {
  const last = policy.drills?.newEntity?.lastRun;
  assert.ok(last, 'there is no lastRun record for the new-entity drill');
  for (const field of policy.restoreVerification.recordFields) {
    assert.ok(field in last, `the drill record has no "${field}"`);
  }
  assert.match(last.date, /^\d{4}-\d{2}-\d{2}$/, 'the drill record has no ISO date');
  assert.equal(last.result, 'PASS', 'the recorded drill is not a PASS');
  assert.match(last.resultLine, /RESTORE_DRILL_RESULT reason=OK status=PASS/, 'the recorded result line is not a PASS');
  // The doc must carry the same run. Two records of two different drills is
  // exactly what R2 exists to prevent, and it is invisible on the page.
  assert.ok(doc.includes(last.date), `docs/backup.md does not record the ${last.date} drill`);
  assert.ok(doc.includes(last.resultLine), 'docs/backup.md does not quote the result line policy.json records');
});

test('a failed drill in the record fails the gate: a failed drill blocks until re-run', () => {
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      data.drills.newEntity.lastRun.result = 'FAIL';
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.ok(reported(stdout, 'DRILL_FAILED'), `expected a DRILL_FAILED finding:\n${stdout}`);
      assert.match(stdout, /does not become a pass with a note/, 'the finding does not state the rule');
    },
  );
});

test('an overdue drill fails (R2 plus the task edge case: overdue, never waived)', () => {
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      // Ten years ago: unambiguously outside any cadence in the file.
      data.drills.newEntity.lastRun.date = '2016-01-01';
      return JSON.stringify(data, null, 2);
    },
    // A fixed "today" so the test does not rot: the gate reads
    // BACKUP_GATE_TODAY, which is also how CI keeps this deterministic.
    () => {
      const { stdout } = runGate({ BACKUP_GATE_TODAY: '2026-10-02' });
      assert.ok(reported(stdout, 'DRILL_OVERDUE'), `expected a DRILL_OVERDUE finding:\n${stdout}`);
      assert.match(stdout, /overdue/, 'the finding does not use the word the task uses');
    },
  );
});

test('a drill record missing a required field fails: a record missing a field is not evidence', () => {
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      delete data.drills.newEntity.lastRun.gaps;
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.ok(reported(stdout, 'DRILL_RECORD_MISSING'), `expected a DRILL_RECORD_MISSING finding:\n${stdout}`);
      assert.match(stdout, /gaps/, 'the finding does not name the missing field');
    },
  );
});

test('an unrun full-system drill that does not block promotion fails', () => {
  // The distinction that makes "not run" and "not required" separable. Without
  // blocksPromotion on an unrun track, a track nobody ever ran is
  // indistinguishable from a track nobody needed.
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      data.drills.fullSystem.blocksPromotion = false;
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.ok(reported(stdout, 'DRILL_RECORD_MISSING'), `expected a DRILL_RECORD_MISSING finding:\n${stdout}`);
      assert.match(stdout, /blocksPromotion/, 'the finding does not name what is missing');
    },
  );
});

test('the gate does not read a $comment as a drill track', () => {
  // A `$comment` key in a data file is documentation. Reading it as a track
  // produces "this drill has no lastRun record" for a key that is not a drill -
  // a false positive that trains people to ignore the gate.
  const { stdout } = runGate();
  assert.ok(
    !/drill track "\$comment"/.test(stdout),
    `the gate treated a $comment key as a drill track:\n${stdout}`,
  );
});

// ============================== R4: the release gate ===========================

test('R4: the release-gate rule is stated in data, and it says a gap blocks', () => {
  assert.ok(policy.releaseGate?.rule, 'no releaseGate.rule is declared');
  assert.match(policy.releaseGate.rule, /block/i, 'the release-gate rule does not say a gap blocks');
  assert.ok(policy.releaseGate.gate, 'the release gate names no gate');
  assert.match(policy.releaseGate.resultLine, /BACKUP_GATE_RESULT/, 'the recorded result line is not the gate result line');
});

test('an open blocking gap fails the gate (R4, proved by injection)', () => {
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      // A new HIGH gap, unowned work, blocking - the exact shape the rule exists for.
      data.gaps.entries.push({
        id: 'TEST1',
        severity: 'HIGH',
        status: 'open',
        blocking: true,
        summary: 'injected by tools/backup-policy.test.mjs',
        owner: 'test',
        reviewBy: '2026-12-31',
      });
      return JSON.stringify(data, null, 2);
    },
    ({ code, stdout }) => {
      assert.ok(reported(stdout, 'GAP_BLOCKS_RELEASE'), `expected a GAP_BLOCKS_RELEASE finding:\n${stdout}`);
      assert.equal(code, 1);
      assert.match(stdout, /TEST1/, 'the finding does not name the gap');
    },
  );
});

test("a durable entity with no backup at all fails the gate (scope.json's own rule)", () => {
  withMutation(
    SCOPE_PATH,
    (text) => {
      const data = JSON.parse(text);
      data.durableEntitiesNotYetBackedUp.entries.push({ id: 'widget_state', why: 'injected by the test' });
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.ok(reported(stdout, 'GAP_BLOCKS_RELEASE'), `expected a GAP_BLOCKS_RELEASE finding:\n${stdout}`);
      assert.match(stdout, /widget_state/, 'the finding does not name the entity');
    },
  );
});

test('a gap with no owner or no review date fails: a gap with no owner is a complaint', () => {
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      data.gaps.entries.push({
        id: 'TEST2',
        severity: 'LOW',
        status: 'open',
        blocking: false,
        summary: 'injected by tools/backup-policy.test.mjs',
      });
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.ok(reported(stdout, 'GAP_BLOCKS_RELEASE'), `expected a GAP_BLOCKS_RELEASE finding:\n${stdout}`);
      assert.match(stdout, /owner/, 'the finding does not say which field is missing');
      assert.match(stdout, /reviewBy/, 'the finding does not say which field is missing');
    },
  );
});

test('a gap marked fixed with no artefact named is reported, not silently accepted', () => {
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      data.gaps.entries.push({
        id: 'TEST3',
        severity: 'LOW',
        status: 'fixed',
        blocking: false,
        summary: 'injected by tools/backup-policy.test.mjs',
        owner: 'test',
        reviewBy: '2026-12-31',
      });
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.match(stdout, /TEST3 is marked fixed with no coveredBy/, 'a fix with no artefact named was not reported');
    },
  );
});

test('an empty gap register fails: no entries after six tasks of backup work is an absence of a register', () => {
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      data.gaps.entries = [];
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.ok(reported(stdout, 'GAP_BLOCKS_RELEASE'), `expected a GAP_BLOCKS_RELEASE finding:\n${stdout}`);
    },
  );
});

test('removing the release-gate rule itself fails (R4, one level up)', () => {
  withMutation(
    POLICY_PATH,
    (text) => {
      const data = JSON.parse(text);
      delete data.releaseGate;
      return JSON.stringify(data, null, 2);
    },
    ({ stdout }) => {
      assert.ok(reported(stdout, 'GAP_BLOCKS_RELEASE'), `expected a GAP_BLOCKS_RELEASE finding:\n${stdout}`);
      assert.match(stdout, /releaseGate\.rule/, 'the finding does not say that the rule itself is what is missing');
    },
  );
});

// ========================== input handling, and the drill =====================

test('a missing policy file is a failure, not a skip', () => {
  const backup = `${POLICY_PATH}.bak-test`;
  copyFileSync(POLICY_PATH, backup);
  try {
    unlinkSync(POLICY_PATH);
    const { code, stdout } = runGate();
    assert.equal(code, 1, 'the gate passed with its policy file deleted');
    assert.ok(reported(stdout, 'INPUT_INVALID'), `expected an INPUT_INVALID finding:\n${stdout}`);
    assert.match(stdout, /has cleared nothing/, 'the finding does not explain why an unread file is a failure');
  } finally {
    copyFileSync(backup, POLICY_PATH);
    unlinkSync(backup);
  }
});

test('a missing CronJob is COVERAGE_GROUPS_MISSING-adjacent: BACKUP_JOB_MISSING, not a pass', () => {
  // D7's own shape: the file the scope file names, absent. Before Task 047 this
  // state was indistinguishable from a working backup.
  const backup = `${CRONJOB_PATH}.bak-test`;
  copyFileSync(CRONJOB_PATH, backup);
  try {
    unlinkSync(CRONJOB_PATH);
    const { code, stdout } = runGate();
    assert.equal(code, 1, 'the gate passed with the backup job deleted');
    assert.ok(reported(stdout, 'BACKUP_JOB_MISSING'), `expected a BACKUP_JOB_MISSING finding:\n${stdout}`);
  } finally {
    copyFileSync(backup, CRONJOB_PATH);
    unlinkSync(backup);
  }
});

test('R1: the CronJob is a real scheduled job, not a manifest that describes one', () => {
  for (const [pattern, what] of [
    [/^kind:\s*CronJob$/m, 'kind: CronJob'],
    [/schedule:\s*"[^"]+"/, 'a schedule'],
    [/concurrencyPolicy:\s*Forbid/, 'concurrencyPolicy: Forbid'],
    [/startingDeadlineSeconds:\s*\d+/, 'startingDeadlineSeconds'],
    [/activeDeadlineSeconds:\s*\d+/, 'activeDeadlineSeconds'],
  ]) {
    assert.ok(pattern.test(cronjob), `the CronJob has no ${what}`);
  }
  // The app role produces an empty archive on every RLS table and pg_restore
  // reports success (gap D3). The script refuses a role without BYPASSRLS as the
  // backstop; asserting the secret key here catches it at review time.
  assert.ok(
    /key:\s*maintenance-connection/.test(cronjob),
    'the CronJob does not use the maintenance-role connection string',
  );
});

test('the job refuses to produce an archive a role without BYPASSRLS would empty', () => {
  // gap D3, pre-empted. The drill discovered it during a restore; the job must
  // not be able to produce the artefact at all.
  assert.match(dumpScript, /RESTORE_ROLE_UNSUITABLE/, 'dump.sh has no failure reason for an unsuitable role');
  assert.match(dumpScript, /rolbypassrls/, 'dump.sh does not check the role for BYPASSRLS');
  assert.match(dumpScript, /EMPTY_ARCHIVE/, 'dump.sh has no reason for a zero-byte archive');
  assert.match(dumpScript, /PGDMP/, 'dump.sh does not verify the archive is custom-format');
});

test('the job never builds a SQL string, and validates every table identifier', () => {
  // The absolute rule, asserted. dump.sh hands pg_dump `--table=` arguments as an
  // argv array; there is no query for a shell to reinterpret.
  //
  // `psql -c` is not forbidden outright, because the job does need exactly one
  // query - the BYPASSRLS preflight - and it is a literal with no interpolation.
  // What is forbidden is a `-c` whose text contains a shell variable or a
  // command substitution, because that is a statement assembled at runtime. The
  // first version of this test banned `psql -c` entirely and therefore could not
  // have passed with the preflight in place: a rule that the correct
  // implementation cannot satisfy gets deleted, and then it checks nothing.
  assert.ok(
    /rolbypassrls/.test(dumpScript),
    'dump.sh has no BYPASSRLS preflight query, so this test would be asserting against a file that does not exist',
  );
  const queries = [...dumpScript.matchAll(/psql\s[^\n]*?-c\s*\\\n?\s*"([^"]*)"/g)].map((m) => m[1]);
  assert.ok(queries.length >= 1, `no psql -c query found in dump.sh; the pattern is probably wrong, and a pattern that matches nothing asserts nothing`);
  for (const query of queries) {
    assert.equal(
      /\$\{|\$\(|\$ /.test(query),
      false,
      `a psql query in dump.sh interpolates a variable: ${query.slice(0, 100)}`,
    );
  }
  // No `psql ... -c` invocation takes its statement from a variable at all.
  assert.ok(
    !/psql[^\n]*-c\s*"?\$/.test(dumpScript),
    'dump.sh passes a shell variable as the SQL text to psql',
  );
  // And table names only ever reach pg_dump as `--table=` arguments on an argv
  // array. A `--table=$t` on a command line would work too; what would not is a
  // table list pasted into a command string, and that is what the array is for.
  assert.ok(
    /DUMP_ARGS=\(/.test(dumpScript) && /DUMP_ARGS\+=\(--table=/.test(dumpScript),
    'dump.sh does not build pg_dump arguments as an array with one --table= per table',
  );
  assert.ok(
    /pg_dump "\$\{DUMP_ARGS\[@\]\}"/.test(dumpScript),
    'dump.sh does not invoke pg_dump with the argument array expanded as an array; a "$*" would re-split it',
  );
  assert.ok(
    /table name is not a bare lowercase identifier/.test(dumpScript),
    'dump.sh does not validate table identifiers',
  );
});

test('the job parses the DSN itself and refuses anything it does not recognise', () => {
  // A silently misparsed DSN produces a dump of the wrong database, which is the
  // worst artefact this job can make. Refusal is the only safe default.
  assert.match(dumpScript, /DSN_UNPARSEABLE/, 'dump.sh has no reason for an unparseable connection string');
  assert.ok(
    /refusing to guess/.test(dumpScript),
    'dump.sh does not refuse to guess at a missing connection-string field',
  );
});

test('the job never logs the connection string or a credential', () => {
  for (const [pattern, what] of [
    [/echo[^\n]*PGPASSWORD/, 'the script echoes PGPASSWORD'],
    [/echo[^\n]*BACKUP_PG_DSN/, 'the script echoes the connection string'],
    [/echo[^\n]*BACKUP_STORAGE_SECRET_KEY/, 'the script echoes the object-storage secret'],
  ]) {
    assert.equal(pattern.test(dumpScript), false, `${what}; a backup job's log is a place a credential ends up`);
  }
  // The connection line must name the user and host without the password.
  assert.match(dumpScript, /password not logged/, 'dump.sh does not say that the password is not logged');
});

test('the job fails on a non-2xx upload rather than warning about it', () => {
  // An archive left on an ephemeral pod volume is gone. A 403 from a private
  // bucket is a normal response to a wrong key, and a job that logs it and
  // exits 0 is a backup that has not run while reporting that it has.
  assert.match(dumpScript, /UPLOAD_FAILED/, 'dump.sh has no reason for a failed upload');
  assert.match(dumpScript, /403\)/, 'dump.sh does not call out an authorisation failure specifically');
  assert.match(dumpScript, /--write-out '%\{http_code\}'/, 'dump.sh does not check the HTTP status of the upload');
});

test('the restore target is staging, and a production drill needs an approved window', () => {
  assert.equal(policy.restoreVerification.target, 'staging', 'the restore target is not staging');
  assert.match(
    policy.restoreVerification.targetRule,
    /approved|time-boxed|access window/i,
    'the target rule does not require an approved access window for anything else',
  );
  // One spot-read per group, each naming what it asserts.
  const spotReads = policy.restoreVerification.spotReads ?? [];
  assert.equal(spotReads.length, REQUIRED_GROUPS.length, `only ${spotReads.length} spot-reads declared for ${REQUIRED_GROUPS.length} groups`);
  for (const read of spotReads) {
    assert.ok(REQUIRED_GROUPS.some((g) => g.id === read.group), `a spot-read names the group "${read.group}", which is not one of the seven`);
    assert.ok(read.id.startsWith('de71'), `the spot-read for ${read.group} does not use a synthetic de71 id`);
    assert.ok(read.assert.length > 3, `the spot-read for ${read.group} asserts nothing specific`);
  }
});

// ================================== the doc ====================================

test('the doc states the policy in the terms the gate enforces', () => {
  // Lowercased and compared lowercase, because the requirement is that the
  // policy is READABLE in these terms, not that a heading uses one
  // particular capitalisation. The earlier version compared case-sensitively and
  // failed on `## Failure escalation` while the page plainly had the section.
  for (const section of ['rpo', 'rto', 'escalation', 'restore drill', 'release gate']) {
    assert.ok(doc.toLowerCase().includes(section), `docs/backup.md does not mention "${section}"`);
  }
  // R5: link, do not fork. The procedure belongs to Plan A's pages.
  for (const link of ['dr/backup-restore.md', 'runbooks/backup-restore.md', 'dr/drill-log.md']) {
    assert.ok(doc.includes(`](${link})`), `docs/backup.md does not link ${link}`);
  }
  assert.ok(
    !/pg_restore --target-time=/.test(doc),
    'docs/backup.md restates the PITR command from dr/backup-restore.md; that is a fork',
  );
  // The seven groups must be nameable in the doc, in the task's own words.
  for (const group of ['tenant user', 'preference', 'notification', 'activity event', 'membership', 'voice preview', 'project metadata']) {
    assert.ok(doc.toLowerCase().includes(group), `docs/backup.md does not mention the ${group} group`);
  }
});

test('the doc carries no credential shape', () => {
  for (const pattern of [/eyJ[A-Za-z0-9_-]{10,}/, /Password=[^;"'\s]*[0-9]/i, /AKIA[0-9A-Z]{16}/]) {
    assert.equal(doc.match(pattern), null, 'docs/backup.md contains something shaped like a credential');
  }
  // And the backup policy file, which is a data file a script reads.
  const policyText = readFileSync(POLICY_PATH, 'utf8');
  for (const pattern of [/eyJ[A-Za-z0-9_-]{10,}/, /AKIA[0-9A-Z]{16}/]) {
    assert.equal(policyText.match(pattern), null, 'deploy/backup/policy.json contains something shaped like a credential');
  }
});

test('the CronJob manifest exists and is in the deploy tree the release gate scans', () => {
  assert.ok(existsSync(CRONJOB_PATH), 'deploy/k8s/backup-cronjob.yaml does not exist - this is gap D7');
});
