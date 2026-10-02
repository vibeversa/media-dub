#!/usr/bin/env node
// Backup/restore POLICY gate (Task 047, R1/R3/R4).
//
// WHAT IT CHECKS
// --------------
//   1. the seven durable-entity groups are in the backup JOB, not only in a
//      coverage document (R1)
//   2. every table a migration creates is CLASSIFIED - in a group, a parent, or
//      in the explicit out-of-scope list. An unclassified table is the "new table
//      added later without a backup entry" edge case, and it fails.
//   3. RPO and RTO are declared, and a target is not presented as a measurement
//      (R3)
//   4. the escalation ladder is complete enough to call somebody
//   5. a drill is RECORDED, with a date, a result and its gaps (R2)
//   6. no open blocking gap (R4)
//
// WHY A FOURTH COPY OF THE REQUIRED GROUPS
// ----------------------------------------
// The first three sources this gate compares (scope.json, the CronJob's
// annotations, dump.sh) are all files a change can reach. A change that removes a
// group from all three at once is a diff a reviewer reads as a deletion, not as a
// coverage change - and 043C proved the failure empirically: deleting the
// `notifications` group from scope.json alone produced a drill that compared nine
// tables against nine tables and printed PASS (gap D2). So the seven ids are
// written down HERE, in the gate, and tools/backup-policy.test.mjs writes them
// down again. If a group is ever removed, three of the four disagree and this
// gate fails. That is the entire argument for the duplication.
//
// MACHINE-READABLE OUTPUT
//   BACKUP_GATE_RESULT reason=<REASON> status=<PASS|FAIL> groups=<n> tables=<n> gaps=<n> open=<n> blocking=<n>
//   REASONS is a closed set, documented in docs/ci-branch-protection.md.
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

const SCOPE_PATH = join(ROOT, 'deploy/backup/scope.json');
const POLICY_PATH = join(ROOT, 'deploy/backup/policy.json');
const CRONJOB_PATH = join(ROOT, 'deploy/k8s/backup-cronjob.yaml');
const DUMP_PATH = join(ROOT, 'deploy/backup/dump.sh');
const DOC_PATH = join(ROOT, 'docs/backup.md');
const MIGRATIONS = join(ROOT, 'src/DubbingPlatform.Infrastructure/Persistence/Migrations');

const REASONS = {
  OK: 'OK',
  INPUT: 'INPUT_INVALID',
  GROUPS: 'COVERAGE_GROUPS_MISSING',
  JOB: 'BACKUP_JOB_MISSING',
  DRIFT: 'COVERAGE_DRIFT',
  UNCLASSIFIED: 'DURABLE_TABLE_UNCLASSIFIED',
  RPO: 'RPO_UNDECLARED',
  RTO: 'RTO_UNDECLARED',
  ESCALATION: 'ESCALATION_INCOMPLETE',
  DRILL_MISSING: 'DRILL_RECORD_MISSING',
  DRILL_STALE: 'DRILL_OVERDUE',
  DRILL_FAILED: 'DRILL_FAILED',
  GAP: 'GAP_BLOCKS_RELEASE',
};

/** The seven groups, fourth declaration. See the header. */
const REQUIRED_GROUPS = [
  { id: 'tenant-users', table: 'tenant_users' },
  { id: 'memberships', table: 'project_memberships' },
  { id: 'project-metadata', table: 'dubbing_projects' },
  { id: 'preferences', table: 'user_preferences' },
  { id: 'notifications', table: 'notifications' },
  { id: 'activity-events', table: 'activity_events' },
  { id: 'voice-preview-jobs', table: 'voice_preview_jobs' },
];

/**
 * Tables that must never be in the SELECTABLE ARCHIVE, and why.
 *
 * The "new table with no backup entry" edge case has two failure modes, and this
 * list guards the one that is easy to get wrong in the other direction. The tier
 * assignment in deploy/backup/policy.json already requires every migration table
 * to be declared, so an unclassified new table fails. What it does not stop is
 * the opposite mistake - adding a queue table to the CronJob, which would put a
 * redelivery-ordering hazard into an archive that a responder would restore on a
 * restore day.
 *
 * Not derived from scope.json on purpose, same reason as REQUIRED_GROUPS: a
 * prohibition that lives in the file it polices can be edited away with the thing
 * it prohibits.
 */
const NEVER_IN_SELECTABLE_ARCHIVE = {
  outbox_message: 'MassTransit outbox. Recovery is redrive-from-outbox, never a queue restore; an archived outbox restores a redelivery order that no longer matches the messages in flight.',
  inbox_state: 'MassTransit inbox deduplication. Rederived as messages are consumed; a restored state is a dedupe window that would hide a redelivery.',
  outbox_state: 'MassTransit transport statistics, not product state.',
  __efmigrationshistory: 'EF Core migration history. The migration chain is the schema authority for a restore, not an artefact one.',
};

const findings = [];
let reason = REASONS.OK;
/** A finding that does not itself fail the gate. Reported, never counted. */
const note = (text) => { findings.push({ reason: 'NOTE', text }); console.log(`  -- [NOTE] ${text}`); };
const pass = (text) => console.log(`  ok ${text}`);

/**
 * Record a finding. `newReason` becomes the result line's reason only for the
 * FIRST failure, so the result line names the earliest problem in the file
 * rather than whichever check happened to run last.
 *
 * Every finding line is prefixed with its own reason, and that is deliberate.
 * `reason=` is a single value, so when the gate legitimately reports several
 * unrelated findings at once, "which one is this?" is otherwise unanswerable
 * from the output - and the tests, which inject one fault at a time into a
 * repository that already has open gaps, cannot assert a specific reason at all.
 * The prefix is what makes a multi-finding run readable and a single-fault
 * injection assertable, and it costs one column.
 */
function fail(newReason, text) {
  if (reason === REASONS.OK) reason = newReason;
  findings.push({ reason: newReason, text });
  console.log(`  !! [${newReason}] ${text}`);
}

function readOr(reasonOnMissing, path) {
  if (!existsSync(path)) {
    fail(reasonOnMissing, `${path.replace(`${ROOT}/`, '')} does not exist; a policy that reads nothing has cleared nothing`);
    return null;
  }
  try {
    return readFileSync(path, 'utf8');
  } catch (error) {
    fail(REASONS.INPUT, `${path.replace(`${ROOT}/`, '')} could not be read: ${error.message}`);
    return null;
  }
}

let scope = null;
let policy = null;
let cronjob = null;
let dumpScript = null;
let doc = null;

console.log('== backup policy gate (Task 047) ==');

const scopeText = readOr(REASONS.INPUT, SCOPE_PATH);
const policyText = readOr(REASONS.INPUT, POLICY_PATH);
const cronjobText = readOr(REASONS.JOB, CRONJOB_PATH);
const dumpText = readOr(REASONS.JOB, DUMP_PATH);
const docText = readOr(REASONS.INPUT, DOC_PATH);

if (scopeText) {
  try { scope = JSON.parse(scopeText); } catch (error) { fail(REASONS.INPUT, `deploy/backup/scope.json is not JSON: ${error.message}`); }
}
if (policyText) {
  try { policy = JSON.parse(policyText); } catch (error) { fail(REASONS.INPUT, `deploy/backup/policy.json is not JSON: ${error.message}`); }
}
cronjob = cronjobText;
dumpScript = dumpText;
doc = docText;

const scopedTables = () => {
  if (!scope) return new Set();
  const names = new Set();
  for (const group of scope.newEntityGroups ?? []) for (const table of group.tables ?? []) names.add(table);
  for (const table of scope.parents?.tables ?? []) names.add(table);
  return names;
};

const scopeGroups = () => new Set((scope?.newEntityGroups ?? []).map((group) => group.id));

/**
 * The tables deploy/backup/dump.sh will actually hand to pg_dump, read from the
 * script's own two bash arrays: `SCOPE_GROUPS` (entries shaped
 * `group-id:table`) and `SCOPE_PARENTS` (entries that are bare table names).
 *
 * Written as a parser of the real file rather than a second hard-coded list
 * because the list the gate checks is the one the job runs. The cost is that a
 * change to the array's syntax makes the gate find no tables - which is why the
 * caller treats an empty result as a finding, not as "no gaps".
 *
 * The array names carry a `SCOPE_` prefix for a reason that is not taste: bash
 * reserves `GROUPS` as a special array of the current user's group ids, and an
 * assignment to it is silently ineffective. The script was originally named
 * `GROUPS` and dumped nothing; `bash -n` accepted it and a reader would not see
 * it. The gate reads the name the script actually uses, so a rename cannot
 * silently detach the gate from the job - `tablesInDumpScript` returning zero
 * tables is itself a finding.
 */
function tablesInDumpScript(source) {
  const tables = new Set();
  const array = (name) => {
    const match = source.match(new RegExp(`^${name}=\\(\\n([\\s\\S]*?)^\\)`, 'm'));
    return match ? match[1] : '';
  };
  for (const line of array('SCOPE_GROUPS').split('\n')) {
    // `[a-z0-9_-]+` for the group id, NOT `[a-z-]+`. The underscore matters: the
    // first version's class excluded it, so a group id containing one was not
    // recognised as a group line and the table behind it was never added to the
    // set - which meant the "never in the selectable archive" prohibition below
    // silently did not apply to any such table. It is the same defect as every
    // other one in this repository's history: a rule whose evidence for
    // "no violations" was a pattern that had never matched anything.
    const entry = line.match(/^\s*"[a-z0-9_-]+:([a-z0-9_]+)"\s*$/);
    if (entry) tables.add(entry[1]);
  }
  for (const line of array('SCOPE_PARENTS').split('\n')) {
    const entry = line.match(/^\s*"([a-z0-9_]+)"\s*$/);
    if (entry) tables.add(entry[1]);
  }
  return tables;
}

// ---- 1. the seven groups exist in every source --------------------------------
if (scope) {
  const declared = scopeGroups();
  for (const required of REQUIRED_GROUPS) {
    if (!declared.has(required.id)) {
      fail(REASONS.GROUPS, `deploy/backup/scope.json does not declare the group "${required.id}"; a group that is not in the scope is not in the dump, not in the count check, and not in the drill`);
    }
  }
  if (declared.size === REQUIRED_GROUPS.length) {
    pass(`scope.json declares all ${REQUIRED_GROUPS.length} required entity groups`);
  }
}

if (policy) {
  const policyGroups = new Set((policy.coverage?.groups ?? []).map((group) => group.id));
  for (const required of REQUIRED_GROUPS) {
    if (!policyGroups.has(required.id)) {
      fail(REASONS.GROUPS, `deploy/backup/policy.json does not list the group "${required.id}"`);
    }
  }
  if (policyGroups.size === REQUIRED_GROUPS.length) {
    pass(`policy.json lists all ${REQUIRED_GROUPS.length} required entity groups`);
  }
}

// ---- 2. the JOB carries them (R1) ---------------------------------------------
// The check that did not exist before this task. docs/backup.md and scope.json
// both claimed a CronJob that was never written, and every coverage check passed
// because the three sources agreed with each other. This compares the job's own
// annotations against the scope, which is a different file, so a removal has to
// be made in two places to go unnoticed.
if (cronjob) {
  const annotationGroups = new Map();
  for (const match of cronjob.matchAll(/dubbing\.io\/backup-group-\d+-([a-z-]+):\s*"([^"]+)"/g)) {
    annotationGroups.set(match[1], match[2]);
  }
  for (const required of REQUIRED_GROUPS) {
    const line = annotationGroups.get(required.id);
    if (line === undefined) {
      // COVERAGE_GROUPS_MISSING, not BACKUP_JOB_MISSING. The job file is right
      // there and was read successfully; what is missing is the group's coverage
      // line in it. BACKUP_JOB_MISSING is reserved for the file being absent, and
      // conflating the two would send an operator looking for a missing manifest
      // when the manifest is present and wrong. The distinction is the difference
      // between "the job is not deployed" and "the job is deployed and does not
      // cover this", which are different incidents with different fixes.
      fail(REASONS.GROUPS, `deploy/k8s/backup-cronjob.yaml has no coverage line for the group "${required.id}"; the job does not say it covers it, so nothing checks that it does`);
      continue;
    }
    // The line must name the table it dumps, not merely the group: a group with
    // no table in its own coverage line is a claim.
    if (!line.includes(`table=${required.table}`)) {
      fail(REASONS.DRIFT, `the CronJob's coverage line for "${required.id}" does not name table=${required.table}: "${line}"`);
    }
  }
  if (annotationGroups.size === REQUIRED_GROUPS.length) {
    pass(`the CronJob declares a coverage line for each of the ${REQUIRED_GROUPS.length} groups`);
  }

  // And the script that does the work must dump the same tables. PARENTS is the
  // script's own bash array (`"tenants"`, `"speakers"`, ...), so it is read from
  // there rather than inferred from the seven group lines.
  if (dumpScript) {
    const dumped = tablesInDumpScript(dumpScript);
    if (dumped.size === 0) {
      fail(REASONS.DRIFT, 'deploy/backup/dump.sh declares no dumpable table; its GROUPS and PARENTS lists could not be read, so a check that found no table is not a check that found no gap');
    }
    for (const required of REQUIRED_GROUPS) {
      if (!dumped.has(required.table)) {
        fail(REASONS.DRIFT, `deploy/backup/dump.sh does not dump ${required.table} (group ${required.id}); the job's coverage line and the job's pg_dump disagree`);
      }
    }
    // Parents too: a dump of the groups without the tables they reference
    // restores notifications whose project does not exist.
    for (const parent of scope?.parents?.tables ?? []) {
      if (!dumped.has(parent)) {
        fail(REASONS.DRIFT, `deploy/backup/dump.sh does not dump the parent "${parent}"; the schema has no foreign key to it, so a dump without it restores incoherently and silently`);
      }
    }
    if (dumped.size > 0) {
      pass(`dump.sh dumps ${dumped.size} table(s) (${REQUIRED_GROUPS.length} groups + parents)`);
    }
  }

  // The manifest must be a real, scheduled job and not a comment.
  for (const [pattern, what] of [
    [/^kind:\s*CronJob$/m, 'kind: CronJob'],
    [/schedule:\s*"[^"]+"/, 'a schedule'],
    [/concurrencyPolicy:\s*Forbid/, 'concurrencyPolicy: Forbid'],
    [/startingDeadlineSeconds:\s*\d+/, 'startingDeadlineSeconds'],
    [/activeDeadlineSeconds:\s*\d+/, 'activeDeadlineSeconds'],
    [/maintenance-connection/, 'the maintenance-role connection string'],
  ]) {
    if (!pattern.test(cronjob)) {
      fail(REASONS.JOB, `deploy/k8s/backup-cronjob.yaml is missing ${what}; a backup manifest without it is not a scheduled job`);
    }
  }
  // The app role produces an empty archive on every RLS table and pg_restore
  // reports success (gap D3). Asserting the secret key is the cheapest place to
  // catch that, and the script refuses a role without BYPASSRLS as the backstop.
  if (/key:\s*connection-string\b/.test(cronjob) && !/key:\s*maintenance-connection/.test(cronjob)) {
    fail(REASONS.JOB, 'deploy/k8s/backup-cronjob.yaml reads the app-role connection string; an archive taken as the app role is EMPTY on every row-level-security table and pg_restore reports success (gap D3)');
  }
  pass('the CronJob is scheduled, forbids concurrency, has deadlines, and uses the maintenance role');
}

// ---- 3. every migration table has a declared tier ------------------------------
// The "new table added later without a backup entry" edge case. Handled by
// requiring the tier assignment in policy.json to be TOTAL over what the
// migrations create, rather than by keeping a hand-maintained list of "known
// unbacked" tables here - that list would be the same third copy that D2 and D6
// and D7 were about, and a new table's author would have to find it to classify
// their own table, which is precisely the case that never happens.
if (scope && policy) {
  const created = new Set();
  let migrationFiles = [];
  try {
    migrationFiles = readdirSync(MIGRATIONS);
  } catch (error) {
    fail(REASONS.INPUT, `the migrations directory could not be read: ${error.message}`);
  }
  for (const file of migrationFiles) {
    if (!file.endsWith('.cs') || file.endsWith('.Designer.cs')) continue;
    const source = readFileSync(join(MIGRATIONS, file), 'utf8');
    for (const match of source.matchAll(/migrationBuilder\.CreateTable\(\s*\n\s*name: "([a-z_]+)"/g)) {
      created.add(match[1]);
    }
  }
  // A non-empty read is asserted, or this loop is vacuous and every table in it
  // is trivially "classified". 043C's D2 and D6 and 046's Finding 3 were all
  // rules that had never matched anything.
  if (created.size < 20) {
    fail(REASONS.INPUT, `only ${created.size} tables were read from the migrations; the CreateTable pattern is probably wrong, and a check that reads nothing classifies everything`);
  } else {
    const tiers = policy.tableTiers ?? {};
    const selectable = new Set(tiers.selectableArchive ?? []);
    const primary = new Set(tiers.primaryBaseBackup ?? []);
    const notRestored = tiers.notRestored ?? {};

    // Totality, in both directions. A tier for a table no migration creates is a
    // stale entry, and a stale entry in a coverage file is how a table quietly
    // stops existing without anyone noticing the coverage changed.
    //
    // `infrastructure` is the declared exemption, and it exists because EF Core
    // creates __EFMigrationsHistory outside any migration. The gate cannot see
    // that table by scanning CreateTable, so without a declared place for it
    // either the check would have to hard-code a name it "knows" about, or the
    // directory would have to be trusted to contain everything. Declaring it is
    // the difference between an exemption that is visible in a data file and one
    // that is invisible in a check.
    const infrastructure = new Set(Object.keys(tiers.infrastructure ?? {}));
    const declared = new Set([...selectable, ...primary, ...Object.keys(notRestored), ...infrastructure]);
    for (const table of declared) {
      if (!created.has(table) && !infrastructure.has(table)) {
        fail(REASONS.UNCLASSIFIED, `deploy/backup/policy.json assigns a backup tier to "${table}", which neither a migration nor the infrastructure list accounts for. A tier for a table that does not exist is a coverage entry that describes nothing.`);
      }
    }
    const tiersFor = (table) => {
      if (selectable.has(table)) return 'selectable-archive';
      if (primary.has(table)) return 'primary-base-backup';
      if (table in notRestored) return 'not-restored';
      return null;
    };
    let unclassified = 0;
    for (const table of [...created].sort()) {
      if (tiersFor(table)) continue;
      unclassified += 1;
      note(`table "${table}" is created by a migration and has NO backup tier in deploy/backup/policy.json. Add it to selectableArchive, primaryBaseBackup, or notRestored with a reason. A new durable table with no backup entry blocks the release until it is classified.`);
    }
    if (unclassified > 0) {
      fail(REASONS.UNCLASSIFIED, `${unclassified} table(s) created by a migration have no declared backup tier`);
    } else {
      pass(`all ${created.size} migration-created tables have a declared tier (${selectable.size} selectable, ${primary.size} primary, ${Object.keys(notRestored).length} not restored)`);
    }

    // And the SELECTABLE tier must be exactly the scope: a table in the archive
    // that the scope does not know about has no retention, no priority and no
    // referential check, which is a table that is dumped and never verified.
    const scoped = scopedTables();
    for (const table of selectable) {
      if (!scoped.has(table)) {
        fail(REASONS.DRIFT, `"${table}" is in policy.json's selectableArchive but in neither newEntityGroups nor parents in scope.json, so it has no declared retention, restore priority or referential check`);
      }
    }
    for (const table of scoped) {
      if (!selectable.has(table)) {
        fail(REASONS.DRIFT, `"${table}" is in scope.json's coverage but not in policy.json's selectableArchive, so the two disagree about what the CronJob dumps`);
      }
    }

    // And the prohibition holds in the real artefact, not only in this file.
    // Inside the same `if (scope && policy)` block as the tier check, because a
    // table with no tier is not something the prohibition can meaningfully be
    // checked against - and putting it outside meant that mutating dump.sh alone,
    // with the policy untouched, skipped the check entirely and reported nothing.
    if (dumpScript) {
      const dumped = tablesInDumpScript(dumpScript);
      for (const [table, why] of Object.entries(NEVER_IN_SELECTABLE_ARCHIVE)) {
        if (dumped.has(table)) {
          fail(REASONS.DRIFT, `deploy/backup/dump.sh dumps "${table}", which must never be in the selectable archive: ${why}`);
        }
      }
    }
  }
}

// ---- 4. RPO and RTO are declared, and a target is not a measurement (R3) --------
if (policy) {
  const rpoTargets = policy.rpo?.targets ?? [];
  if (rpoTargets.length === 0) {
    fail(REASONS.RPO, 'deploy/backup/policy.json declares no RPO target; the plan requires that an expectation exists, and an absent one is not an expectation');
  }
  for (const target of rpoTargets) {
    if (typeof target.minutes !== 'number' || target.minutes < 1) {
      fail(REASONS.RPO, `the RPO target for "${target.scope}" has no numeric minutes`);
    }
    if (typeof target.mechanism !== 'string' || target.mechanism.length < 10) {
      fail(REASONS.RPO, `the RPO target for "${target.scope}" names no mechanism; an RPO without a mechanism is a wish`);
    }
    if (typeof target.source !== 'string' || !target.source.includes('docs/')) {
      fail(REASONS.RPO, `the RPO target for "${target.scope}" does not link a document; the numbers in this file are team-chosen, and the source of a number is what makes it reviewable`);
    }
    // The distinction that matters: a target that claims to be measured, with no
    // date, is a measurement claim with nothing behind it.
    if (target.measured === true && !target.measuredOn) {
      fail(REASONS.RPO, `the RPO target for "${target.scope}" is measured=true with no measuredOn date`);
    }
  }
  if (rpoTargets.length > 0) pass(`${rpoTargets.length} RPO target(s) declared, each with a mechanism and a source`);

  if (typeof policy.rto?.targetMinutes !== 'number' || policy.rto.targetMinutes < 1) {
    fail(REASONS.RTO, 'deploy/backup/policy.json declares no numeric RTO; the plan requires that the expectation exists');
  } else {
    if (policy.rto.measured === true && !policy.rto.measuredOn) {
      fail(REASONS.RTO, 'the RTO is measured=true with no measuredOn date; a target that claims a measurement it does not have is worse than an honest unmeasured one');
    }
    if (!policy.rto.consequence || policy.rto.consequence.length < 20) {
      fail(REASONS.RTO, 'the RTO declares no consequence for missing it. A target with no stated consequence is not a target; a drill that misses it must be a FAILED drill');
    }
    pass(`RTO target is ${policy.rto.targetMinutes} min, measured=${policy.rto.measured === true}, with a stated consequence for missing it`);
  }
}

// ---- 5. escalation is callable (R3) -------------------------------------------
if (policy) {
  const ladder = policy.escalation?.ladder ?? [];
  if (ladder.length < 2) {
    fail(REASONS.ESCALATION, `the escalation ladder has ${ladder.length} level(s); a single contact is not a ladder, it is a person who is also on holiday`);
  }
  for (const level of ladder) {
    if (!level.level || !level.role) fail(REASONS.ESCALATION, 'an escalation level names no role');
    if (typeof level.slaMinutes !== 'number' || level.slaMinutes < 1) fail(REASONS.ESCALATION, `escalation level ${level.level} has no SLA`);
    if (!Array.isArray(level.triggers) || level.triggers.length === 0) {
      fail(REASONS.ESCALATION, `escalation level ${level.level} lists no trigger; an escalation path with no trigger is a phone tree nobody calls`);
    }
    // A CHANGE_ME contact is allowed - this repository ships placeholders and
    // the rule is that they are VISIBLE, not that they are absent. What is not
    // allowed is a level with no contact field at all, which would be a level
    // nobody can be reached on.
    if (typeof level.contact !== 'string' || level.contact.length === 0) {
      fail(REASONS.ESCALATION, `escalation level ${level.level} has no contact field at all`);
    }
  }
  if (ladder.length >= 2) pass(`escalation ladder has ${ladder.length} levels, each with a role, an SLA, a trigger and a contact field`);
  if (!policy.escalation?.drillUnavailable || !/overdue/i.test(policy.escalation.drillUnavailable)) {
    fail(REASONS.ESCALATION, 'the policy does not say what happens when a drill cannot run; "overdue, not waived" is the rule and it has to be written down to be enforced');
  }
}

// ---- 6. the drill is recorded (R2) ---------------------------------------------
if (policy) {
  for (const [name, track] of Object.entries(policy.drills ?? {})) {
    // A `$comment` key is documentation, not a drill. Reading one as a track
    // produces "this drill has no lastRun record" for a key that was never a
    // drill - a false positive that trains people to skim the gate's output. It
    // is D10 in docs/backup.md.
    if (name.startsWith('$')) continue;
    const last = track?.lastRun;
    if (!last) {
      fail(REASONS.DRILL_MISSING, `drill track "${name}" has no lastRun record; a drill that is documented but never executed is not a drill`);
      continue;
    }
    // The full-system track is staging-gated and has never run. That is recorded
    // as NOT_RUN with a null date, and it blocks PROMOTION (F1) - it is not a
    // failed new-entity drill, so it does not fail this gate's own status. The
    // distinction is asserted rather than assumed: `blocksPromotion` must be
    // declared true for a track that has never run, or "not run" and "not
    // required" become indistinguishable.
    if (last.date === null) {
      if (last.result !== 'NOT_RUN') {
        fail(REASONS.DRILL_MISSING, `drill track "${name}" has no date but reports "${last.result}" rather than NOT_RUN`);
      }
      if (track.blocksPromotion !== true) {
        fail(REASONS.DRILL_MISSING, `drill track "${name}" has never run and does not declare blocksPromotion; an unrun drill that blocks nothing is not a gate`);
      }
      // An unrun track is a FINDING, not a failure, and the distinction is
      // asserted rather than assumed: `blocksPromotion` is what carries the
      // consequence, and the open F1 gap is what the gate reports. Printing it
      // here says "this is overdue" without making a repository that has never
      // had a staging environment permanently red - and it is printed as a
      // finding on its own line so a reader cannot miss it above the summary.
      note(`drill track "${name}" has never run (${track.requiresEnvironment ?? 'environment unavailable'}); promotion is blocked by gap F1 and this is an OVERDUE drill, not a waiver`);
      continue;
    }
    if (!/^\d{4}-\d{2}-\d{2}$/.test(last.date)) {
      fail(REASONS.DRILL_MISSING, `drill track "${name}" records a lastRun date of "${last.date}", which is not a date`);
    }
    if (!['PASS', 'FAIL', 'NOT_RUN'].includes(last.result)) {
      fail(REASONS.DRILL_MISSING, `drill track "${name}" records result "${last.result}", which is not PASS, FAIL or NOT_RUN`);
    }
    if (last.result === 'FAIL') {
      fail(REASONS.DRILL_FAILED, `the last ${name} drill (${last.date}) is recorded FAIL; a failed drill blocks until it is re-run, it does not become a pass with a note`);
    }
    for (const field of policy.restoreVerification?.recordFields ?? []) {
      if (!(field in last)) {
        fail(REASONS.DRILL_MISSING, `the ${name} drill record has no "${field}"; the record fields are the deliverable, and a record missing one is not evidence of anything`);
      }
    }
    // Freshness, from the track's own declared cadence. Staleness is computed
    // against the file's mtime-free date rather than the clock when the clock is
    // unavailable, so the gate is deterministic in CI.
    if (typeof track.cadenceDays === 'number') {
      const age = daysBetween(last.date, process.env.BACKUP_GATE_TODAY ?? today());
      if (age > track.cadenceDays) {
        fail(REASONS.DRILL_STALE, `the ${name} drill last ran ${last.date}, ${age} days ago, and its cadence is ${track.cadenceDays} days. An overdue drill is recorded as overdue; it is never treated as a pass`);
      } else {
        pass(`${name} drill recorded ${last.date} as ${last.result} (${age} days old, cadence ${track.cadenceDays})`);
      }
    }
  }
}

function today() {
  return new Date().toISOString().slice(0, 10);
}
function daysBetween(from, to) {
  const a = Date.parse(`${from}T00:00:00Z`);
  const b = Date.parse(`${to}T00:00:00Z`);
  if (Number.isNaN(a) || Number.isNaN(b)) return 0;
  return Math.floor((b - a) / 86400000);
}

// ---- 7. open blocking gaps block the release (R4) -----------------------------
let gapCount = 0;
let openCount = 0;
let blockingCount = 0;
if (policy) {
  const entries = policy.gaps?.entries ?? [];
  if (entries.length === 0) {
    fail(REASONS.GAP, 'the policy declares no gaps at all. A register with no entries after six tasks of backup work is not a register, it is an absence of one');
  }
  for (const entry of entries) {
    gapCount += 1;
    if (entry.status === 'open') openCount += 1;
    if (entry.status === 'open' && entry.blocking === true) {
      blockingCount += 1;
      fail(REASONS.GAP, `gap ${entry.id} is open and blocking: ${entry.summary}`);
    }
    for (const field of ['id', 'severity', 'status', 'summary', 'owner', 'reviewBy']) {
      if (!entry[field]) {
        fail(REASONS.GAP, `a gap entry has no "${field}". A gap with no owner is a complaint; a gap with no review date is never closed`);
      }
    }
    if (entry.status !== 'open' && entry.status !== 'fixed' && entry.status !== 'accepted') {
      fail(REASONS.GAP, `gap ${entry.id} has status "${entry.status}", which is not open, fixed or accepted`);
    }
    if (entry.status === 'fixed' && !/coveredBy/.test(JSON.stringify(entry))) {
      note(`gap ${entry.id} is marked fixed with no coveredBy field; a fix with no artefact named is a claim`);
    }
  }
  // The scope's own "not yet backed up" list is a release blocker by its
  // comment, so the gate reads it too. It is the strongest form of the rule: a
  // durable entity with no backup at all.
  const notBackedUp = scope?.durableEntitiesNotYetBackedUp?.entries ?? null;
  if (notBackedUp && notBackedUp.length > 0) {
    fail(REASONS.GAP, `deploy/backup/scope.json records durable entities that are not backed up: ${JSON.stringify(notBackedUp)}. That is a release blocker by the file's own rule`);
  }
  if (policy.releaseGate?.rule) {
    if (!/block/i.test(policy.releaseGate.rule)) {
      fail(REASONS.GAP, 'the releaseGate rule does not say that a gap blocks; the rule is the deliverable, not its enforcement');
    }
  } else {
    fail(REASONS.GAP, 'deploy/backup/policy.json declares no releaseGate.rule; instruction 5 is the rule, stated in data so the gate and the doc cannot disagree about it');
  }
  pass(`${gapCount} gap(s) recorded, ${openCount} open, ${blockingCount} blocking`);
}

// ---- the doc must agree with the policy ---------------------------------------
if (doc && policy) {
  // Case-insensitive on purpose: the requirement is that the policy is readable
  // in these terms, not that a heading uses one particular capitalisation. A
  // case-sensitive match failed on `## Failure escalation` while the page plainly
  // had the section, which is a gate teaching people to ignore it.
  for (const section of ['RPO', 'RTO', 'escalation', 'restore drill', 'release gate']) {
    if (!doc.toLowerCase().includes(section.toLowerCase())) {
      fail(REASONS.INPUT, `docs/backup.md does not mention "${section}"; the policy is enforced in code and has to be readable by the person who is told the release is blocked`);
    }
  }
  // The recorded drill in the doc and the recorded drill in the policy must be
  // the same run. Two records of two different drills is the state this task's
  // R2 exists to prevent, and it is invisible on the page.
  const last = policy.drills?.newEntity?.lastRun;
  if (last?.date && last.resultLine) {
    if (!doc.includes(last.date)) {
      fail(REASONS.INPUT, `docs/backup.md does not record the ${last.date} drill that deploy/backup/policy.json reports as the last run`);
    }
    if (!doc.includes(last.resultLine)) {
      fail(REASONS.INPUT, `docs/backup.md does not quote the result line policy.json records: ${last.resultLine}`);
    }
  }
  pass('docs/backup.md carries the same drill record the policy reports');
}

// ---- verdict ------------------------------------------------------------------
const groupCount = (scope?.newEntityGroups ?? []).length;
const tableCount = scopedTables().size;
const status = reason === REASONS.OK ? 'PASS' : 'FAIL';
console.log();
if (status === 'PASS') {
  console.log(`backup policy: ${groupCount} groups, ${tableCount} tables, ${gapCount} gaps (${openCount} open, ${blockingCount} blocking)`);
} else {
  console.log(`${findings.length} finding(s):`);
  for (const finding of findings) console.log(`  - [${finding.reason}] ${finding.text}`);
}
console.log();
console.log(`BACKUP_GATE_RESULT reason=${reason} status=${status} groups=${groupCount} tables=${tableCount} gaps=${gapCount} open=${openCount} blocking=${blockingCount}`);
process.exit(status === 'PASS' ? 0 : 1);
