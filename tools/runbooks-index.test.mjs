// The operations index and its links (Task 043, R7).
//
// WHY THIS IS A GATE AND NOT A LINK CHECK IN A REVIEW
// ---------------------------------------------------
// Every requirement here is a file that must exist and be reachable from the ops
// index. Both halves fail silently: a runbook that exists but is not linked is
// never found during an incident, and a link to a renamed file is a 404 that
// costs a responder a minute at 2 a.m.
//
// So the assertions are structural, not a search for a filename:
//   * every runbook the task requires is present;
//   * every relative markdown link in the index resolves to a file that exists;
//   * every runbook carries the five sections, in the order the index promises;
//   * each section is non-empty, because a heading with nothing under it is a
//     runbook that will not help.
import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const RUNBOOKS = join(REPO_ROOT, 'docs/runbooks');
const INDEX = join(RUNBOOKS, 'index.md');

/** The eight runbooks Task 043 instruction 7 names, plus the ops-critical ten. */
const REQUIRED_RUNBOOKS = [
  // Task 043 instruction 7
  'auth-outage.md',
  'cdn-cache-poison.md',
  'sse-degraded.md',
  'deploy-failed.md',
  'notification-backlog.md',
  'contract-drift.md',
  'upload-surge.md',
  'review-surge.md',
  // Task 043 instructions 6 and 8
  'rollback.md',
  'backup-restore.md',
];

/** Pre-existing runbooks. Not required by 043, but linked from the index and
 *  so covered by the link check. */
const EXISTING_RUNBOOKS = [
  'db-failover.md',
  'dlq.md',
  'escalation.md',
  'lease.md',
  'orphan.md',
  'provider-outage.md',
  'quota-cost.md',
  'review-backlog.md',
  'storage-outage.md',
];

/** The five sections, in the order the index promises every runbook has them. */
const SECTION_ORDER = [
  '## Symptoms',
  '## Five-minute triage',
  '## Mitigation',
  '## Escalation',
  '## Postmortem trigger',
];

test('every runbook Task 043 requires exists', () => {
  for (const name of REQUIRED_RUNBOOKS) {
    assert.ok(existsSync(join(RUNBOOKS, name)), `docs/runbooks/${name} is missing`);
  }
});

test('the ops index exists', () => {
  assert.ok(existsSync(INDEX), 'docs/runbooks/index.md is missing; R7 requires the runbooks to be linked from an index');
});

test('every runbook is linked from the ops index', () => {
  // The one that matters. A runbook nobody can find is not a runbook, and
  // "it exists" reads as done in a task report.
  const index = readFileSync(INDEX, 'utf8');
  for (const name of [...REQUIRED_RUNBOOKS, ...EXISTING_RUNBOOKS]) {
    assert.ok(index.includes(`(${name})`), `docs/runbooks/index.md does not link ${name}`);
  }
});

test('every relative link in the ops index resolves', () => {
  const index = readFileSync(INDEX, 'utf8');
  const links = [...index.matchAll(/\]\(([^)#][^)]*\.md)(?:#[^)]*)?\)/g)].map((match) => match[1]);
  assert.ok(links.length >= REQUIRED_RUNBOOKS.length + EXISTING_RUNBOOKS.length, 'the index links fewer files than it should');

  for (const link of links) {
    const target = resolve(dirname(INDEX), link);
    assert.ok(existsSync(target), `docs/runbooks/index.md links ${link}, which does not exist`);
  }
});

// The eight incident runbooks the task names. `rollback.md` and
// `backup-restore.md` are NOT in this list: the task specifies the five-section
// shape for instruction 7's runbooks, and instructions 6 and 8 specify
// different content for those two (a decision table, and a coverage table plus a
// drill). The pre-existing runbooks predate the convention and are asserted only
// for being present and linked — rewriting nine documents to a convention they
// were not written under is not this task's work.
const INCIDENT_RUNBOOKS = REQUIRED_RUNBOOKS.filter((name) => name !== 'rollback.md' && name !== 'backup-restore.md');

test('every incident runbook carries the five sections, in order, and none is empty', () => {
  for (const name of INCIDENT_RUNBOOKS) {
    const text = readFileSync(join(RUNBOOKS, name), 'utf8');
    const positions = SECTION_ORDER.map((heading) => text.indexOf(heading));

    for (let index = 0; index < SECTION_ORDER.length; index += 1) {
      const heading = SECTION_ORDER[index];
      assert.notEqual(positions[index], -1, `${name} is missing "${heading}"`);

      // Non-empty. A heading with nothing under it is the failure mode where a
      // runbook gets padded to satisfy a checklist and helps nobody.
      const body = text.slice(
        positions[index] + heading.length,
        index + 1 < SECTION_ORDER.length ? positions[index + 1] : text.length,
      );
      const meaningful = body
        .split('\n')
        .map((line) => line.trim())
        .filter((line) => line.length > 0 && !line.startsWith('#'));
      assert.ok(meaningful.length >= 3, `${name}: "${heading}" has fewer than three lines under it`);
    }

    for (let index = 1; index < positions.length; index += 1) {
      assert.ok(
        positions[index] > positions[index - 1],
        `${name}: "${SECTION_ORDER[index]}" appears before "${SECTION_ORDER[index - 1]}"`,
      );
    }
  }
});

test('the postmortem trigger in each incident runbook is a list, not a sentence', () => {
  // "Was this bad enough for a postmortem?" is a question nobody should be
  // answering at 2 a.m. with no context. It is answered by a trigger list.
  for (const name of INCIDENT_RUNBOOKS) {
    const text = readFileSync(join(RUNBOOKS, name), 'utf8');
    const at = text.indexOf('## Postmortem trigger');
    assert.notEqual(at, -1, `${name} has no postmortem trigger`);
    const body = text.slice(at).split('\n').slice(1);
    const bullets = body.filter((line) => line.trim().startsWith('- '));
    assert.ok(bullets.length >= 2, `${name}: the postmortem trigger is not a list of at least two conditions`);
  }
});

test('the rollback runbook carries a decision table for expand-vs-contract', () => {
  // The task's specific requirement: "the decision table for expand-vs-contract
  // states". A table is the form; prose about it is not, because the responder
  // needs to find their row in seconds.
  const text = readFileSync(join(RUNBOOKS, 'rollback.md'), 'utf8');
  const tableRows = text.split('\n').filter((line) => line.trim().startsWith('|'));
  assert.ok(tableRows.length >= 6, 'rollback.md has no decision table with the expected rows');
  assert.ok(text.includes('migrationsPending'), 'the decision table does not key off the observable state');
  assert.ok(/forward[- ]fix/i.test(text), 'rollback.md does not state the forward-fix rule');
  assert.ok(/never reverses a migration/i.test(text), 'rollback.md does not state that a migration is never reversed');
});

test('the backup runbook names every data class the task lists', () => {
  // A restore drill that restores the database and not the media verifies half
  // the system, and the table is what makes that visible.
  const text = readFileSync(join(RUNBOOKS, 'backup-restore.md'), 'utf8');
  for (const subject of [
    'user_preferences',
    'notifications',
    'activity_events',
    'project_memberships',
    'voice_preview_jobs',
    'artifact_parents',
    'media',
    'RPO',
    'RTO',
    'quarterly',
    'release blocker',
  ]) {
    assert.ok(text.toLowerCase().includes(subject.toLowerCase()), `backup-restore.md does not mention ${subject}`);
  }
});

test('the ops index names the eight Task 043 runbooks by symptom', () => {
  const index = readFileSync(INDEX, 'utf8');
  for (const name of REQUIRED_RUNBOOKS) {
    assert.ok(index.includes(name), `the index does not name ${name}`);
  }
});

test('every runbook in the directory is either required or pre-existing', () => {
  // Catches a runbook added without being indexed, which is how the index stops
  // matching reality: the directory grows and the page does not.
  const onDisk = readdirSync(RUNBOOKS).filter((name) => name.endsWith('.md') && name !== 'index.md');
  const known = new Set([...REQUIRED_RUNBOOKS, ...EXISTING_RUNBOOKS]);
  for (const name of onDisk) {
    assert.ok(known.has(name), `docs/runbooks/${name} is on disk but is not in the test's known list; add it to the index and here`);
  }
});

test('the topology document draws the data flow the task names', () => {
  const text = readFileSync(join(REPO_ROOT, 'docs/topology.md'), 'utf8');
  for (const component of ['BROWSER', 'CDN EDGE', 'STATIC ORIGIN', 'API INGRESS', 'PostgreSQL', 'RabbitMQ', 'Redis', 'object storage']) {
    assert.ok(text.includes(component), `docs/topology.md does not include ${component}`);
  }
});

test('the rollout document states the two-green-releases contract gate', () => {
  const text = readFileSync(join(REPO_ROOT, 'docs/rollout.md'), 'utf8');
  assert.ok(/two green releases/i.test(text), 'docs/rollout.md does not state the two-green-release contract rule');
  assert.ok(/--require-post-deploy/.test(text), 'docs/rollout.md does not wire the post-deploy gate');
  assert.ok(/migration job/i.test(text), 'docs/rollout.md does not put the migration Job before the API');
});
