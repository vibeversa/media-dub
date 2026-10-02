// Backup coverage for the durable entities Plan B added (Task 043C, R2).
//
// WHY THE SCOPE LIVES IN A JSON FILE AND NOT IN THE PROSE
// -------------------------------------------------------
// Three consumers need the same list and they are maintained by different people
// at different times: a responder reading the coverage table, the drill deciding
// what to count, and the job deciding what to dump. Three hand-maintained lists
// drift, and they drift silently, because each one still looks correct on its
// own.
//
// That is not hypothetical here. `scripts/restore-drill.sh` originally read its
// table list from scope.json AND checked completeness against scope.json, so
// deleting a group from the data file removed it from the dump, from the count
// comparison, and from the completeness check in one edit - and the drill then
// compared nine tables against nine tables, matched every count, and printed
// PASS. It had verified less and said so in exactly the same way. (Gap D2 in
// docs/backup.md.)
//
// So the required set is written down HERE, in a file that a change to the data
// does not also change - the same shape as REQUIRED_RUNBOOKS in
// runbooks-index.test.mjs and PRODUCT_RUNBOOKS in product-runbooks.test.mjs.
import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const SCOPE_PATH = join(REPO_ROOT, 'deploy/backup/scope.json');
const DOC_PATH = join(REPO_ROOT, 'docs/backup.md');
const SEED_PATH = join(REPO_ROOT, 'deploy/backup/drill-seed.sql');
const DRILL_PATH = join(REPO_ROOT, 'scripts/restore-drill.sh');

const scope = JSON.parse(readFileSync(SCOPE_PATH, 'utf8'));
const doc = readFileSync(DOC_PATH, 'utf8');
const seed = readFileSync(SEED_PATH, 'utf8');
const drill = readFileSync(DRILL_PATH, 'utf8');

/**
 * The seven new-entity groups, task instruction 2, in the task's words.
 * Independent of scope.json on purpose - see the header.
 */
const REQUIRED_GROUPS = [
  { id: 'tenant-users', table: 'tenant_users' },
  { id: 'preferences', table: 'user_preferences' },
  { id: 'notifications', table: 'notifications' },
  { id: 'activity-events', table: 'activity_events' },
  { id: 'memberships', table: 'project_memberships' },
  { id: 'voice-preview-jobs', table: 'voice_preview_jobs' },
  { id: 'project-metadata', table: 'dubbing_projects' },
];

/** The nine columns `AddProductIdentityExtensions` added to dubbing_projects. */
const EXTENDED_PROJECT_COLUMNS = [
  'name',
  'description',
  'is_archived',
  'archived_at',
  'owner_user_id',
  'created_by_user_id',
  'updated_by_user_id',
  'processing_settings_json',
  'settings_version',
];

// -------------------------------------------------------------- R2: coverage ---

test('scope.json declares all seven new-entity groups (R2)', () => {
  const declared = new Set(scope.newEntityGroups.map((group) => group.id));
  for (const required of REQUIRED_GROUPS) {
    assert.ok(
      declared.has(required.id),
      `deploy/backup/scope.json does not declare the group "${required.id}"; a group that is not declared is not backed up`,
    );
  }
  assert.equal(
    scope.newEntityGroups.length,
    REQUIRED_GROUPS.length,
    `scope.json declares ${scope.newEntityGroups.length} groups and the task names ${REQUIRED_GROUPS.length}`,
  );
});

test('every declared group names a real table that the migrations create', () => {
  // A group naming a table that does not exist is a coverage claim that cannot
  // be drilled, and the failure is a pg_dump error at drill time rather than a
  // review-time observation. The table list is read out of the committed
  // migrations, which is the definition of what exists.
  const migrations = join(REPO_ROOT, 'src/DubbingPlatform.Infrastructure/Persistence/Migrations');
  const created = new Set();
  for (const file of readdirSync(migrations)) {
    if (!file.endsWith('.cs') || file.endsWith('.Designer.cs')) continue;
    const source = readFileSync(join(migrations, file), 'utf8');
    for (const match of source.matchAll(/migrationBuilder\.CreateTable\(\s*\n\s*name: "([a-z_]+)"/g)) {
      created.add(match[1]);
    }
  }
  // dubbing_projects is created by InitialCreate, whose CreateTable name is on
  // the same pattern; assert the set is non-empty so the loop is not vacuous.
  assert.ok(created.size > 20, `only ${created.size} tables were read from the migrations; the regex is probably wrong`);

  for (const group of scope.newEntityGroups) {
    for (const table of group.tables) {
      assert.ok(created.has(table), `scope.json group "${group.id}" names "${table}", which no migration creates`);
    }
  }
  for (const table of scope.parents.tables) {
    assert.ok(created.has(table), `scope.json parents name "${table}", which no migration creates`);
  }
});

test('the extended project-metadata group names all nine new columns', () => {
  // The group is "extended project metadata" and the count is the task's: a
  // restore from a pre-expand archive keeps the row and loses these, so a group
  // that names six of them describes a coverage that does not exist.
  const group = scope.newEntityGroups.find((g) => g.id === 'project-metadata');
  assert.ok(group, 'the project-metadata group is missing from scope.json');
  for (const column of EXTENDED_PROJECT_COLUMNS) {
    assert.ok(
      group.columns?.includes(column),
      `the project-metadata group does not name the column "${column}"`,
    );
  }
  assert.equal(
    group.columns.length,
    EXTENDED_PROJECT_COLUMNS.length,
    `the project-metadata group names ${group.columns.length} columns and the migration adds ${EXTENDED_PROJECT_COLUMNS.length}`,
  );

  // And the migration really does add them, so the list is not aspirational.
  const migration = readFileSync(
    join(
      REPO_ROOT,
      'src/DubbingPlatform.Infrastructure/Persistence/Migrations/20260921093301_AddProductIdentityExtensions.cs',
    ),
    'utf8',
  );
  for (const column of EXTENDED_PROJECT_COLUMNS) {
    assert.ok(
      migration.includes(`name: "${column}",`),
      `AddProductIdentityExtensions does not add "${column}"; scope.json claims a column the migration does not create`,
    );
  }
});

test('every group declares a restore priority, a retention, and why it matters', () => {
  const priorities = new Set();
  for (const group of scope.newEntityGroups) {
    assert.equal(typeof group.restorePriority, 'number', `group "${group.id}" has no restorePriority`);
    assert.ok(group.restorePriority >= 1, `group "${group.id}" has a restorePriority below 1`);
    assert.ok(group.retentionDays >= 1, `group "${group.id}" has no retentionDays`);
    assert.ok(
      typeof group.why === 'string' && group.why.length > 40,
      `group "${group.id}" has no substantive "why"; a coverage table with no cost-of-loss is a list`,
    );
    assert.ok(
      typeof group.addedBy === 'string' && /^\d{14}_[A-Za-z]+$/.test(group.addedBy),
      `group "${group.id}" does not name the migration that created it (addedBy)`,
    );
    // Priorities are shared (three groups at 2) but must be contiguous from 1,
    // because a gap in the ordering means nobody wrote down what goes in it.
    priorities.add(group.restorePriority);
  }
  const sorted = [...priorities].sort((a, b) => a - b);
  assert.deepEqual(
    sorted,
    Array.from({ length: sorted.length }, (_, i) => i + 1),
    `the restore priorities are not contiguous from 1: ${sorted.join(', ')}`,
  );

  // Every addedBy must be a real migration file.
  for (const group of scope.newEntityGroups) {
    assert.ok(
      existsSync(
        join(
          REPO_ROOT,
          'src/DubbingPlatform.Infrastructure/Persistence/Migrations',
          `${group.addedBy}.cs`,
        ),
      ),
      `group "${group.id}" names migration "${group.addedBy}", which does not exist`,
    );
  }
});

test('the durable entities not yet backed up list is empty', () => {
  // An entry here is a release blocker by scope.json's own comment, so an
  // assertion is the only thing that makes the comment true.
  assert.deepEqual(
    scope.durableEntitiesNotYetBackedUp.entries,
    [],
    `scope.json records durable entities that are not backed up: ${JSON.stringify(scope.durableEntitiesNotYetBackedUp.entries)}`,
  );
});

// -------------------------------------------------------------- the seed ------

test('the drill seed covers every scoped table with at least one row', () => {
  // The drill fails on an empty table ("the drill would prove nothing"), so a
  // table added to the scope without a seed row turns the drill red for a reason
  // that looks like a product fault. Caught here instead, with a clear message.
  for (const group of scope.newEntityGroups) {
    for (const table of group.tables) {
      assert.ok(
        new RegExp(`INSERT INTO ${table}\\b`).test(seed),
        `deploy/backup/drill-seed.sql inserts nothing into "${table}" (group ${group.id}); the drill fails on an empty table`,
      );
    }
  }
  for (const table of scope.parents.tables) {
    assert.ok(
      new RegExp(`INSERT INTO ${table}\\b`).test(seed),
      `deploy/backup/drill-seed.sql inserts nothing into the parent "${table}"`,
    );
  }
});

test('the seed is idempotent and self-cleaning, and scoped to its own tenant', () => {
  // A seed that only works on an empty database is a seed that gets skipped
  // after the first attempt, which is the same as no seed on a re-run.
  assert.ok(
    /DELETE FROM tenants\s+WHERE id\s*=\s*'de710000/.test(seed),
    'the seed does not delete its own tenant first, so it cannot be re-run',
  );
  // Every DELETE must be narrowed by the drill's own tenant id (or, for the
  // tenant row itself, by the drill's own id). An unscoped DELETE in a seed file
  // is a statement that looks like setup and is not.
  const deletes = [...seed.matchAll(/DELETE FROM ([a-z_]+)([^;]*);/g)];
  assert.ok(deletes.length >= 9, `only ${deletes.length} DELETE statements found in the seed`);
  for (const [, table, predicate] of deletes) {
    assert.ok(
      /de710000-0000-4000-8000-000000000001/.test(predicate),
      `the seed's DELETE FROM ${table} is not scoped to the drill's own tenant id: ${predicate.trim()}`,
    );
  }
});

test('the seed contains synthetic ids only, and no real-looking credential', () => {
  // Every seeded uuid begins de71, which is valid hex and spells "drill". A seed
  // row copied from an environment is a drill that writes real data.
  const uuids = [...seed.matchAll(/'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'/g)].map((m) => m[0]);
  assert.ok(uuids.length > 20, `only ${uuids.length} uuids found in the seed; the regex is probably wrong`);
  for (const uuid of uuids) {
    assert.ok(uuid.startsWith("'de71"), `the seed contains a uuid that is not the synthetic de71 prefix: ${uuid}`);
  }

  // Emails must be on a reserved TLD that cannot resolve.
  const emails = [...seed.matchAll(/'([^']+@[^']+)'/g)].map((m) => m[1]);
  for (const email of emails) {
    assert.ok(email.endsWith('@drill.invalid'), `the seed contains a real-looking email: ${email}`);
  }
});

test('the seed covers all three notification states the runbooks depend on', () => {
  // The product runbooks reason about an unread row, a read row, and an expired
  // row - and the expired one is why an unread count and a list can disagree
  // after a restore. A seed with only fresh unread rows would let the drill pass
  // while the behaviour the runbook describes is untested.
  //
  // The column order in the INSERT is (read_at, created_at, expires_at), so a
  // READ row is three consecutive timestamp literals and an UNREAD row is
  // `NULL` in the first position. Asserting on the literals rather than on a
  // regex over the whole file is what keeps "read" from also matching created_at.
  const readTail = "'2026-10-01T00:20:00Z', '2026-10-01T00:18:00Z', '2026-11-01T00:18:00Z'";
  assert.ok(
    seed.includes(readTail),
    'the seed has no READ notification (a row whose read_at, created_at and expires_at are all literals)',
  );

  // UNREAD: the two Alice rows and the QcBlocked row put NULL in the read_at
  // position, i.e. a line ending `, NULL,` immediately before the created_at
  // literal. Counted so removing an unread row is caught.
  const unreadRows = [...seed.matchAll(/,\s*\n\s*NULL,\s*'20[\d-]+T[\d:]+Z',/g)];
  assert.ok(
    unreadRows.length >= 3,
    `the seed has only ${unreadRows.length} unread notification row(s); the unread-count path needs at least three`,
  );

  assert.ok(
    /past expires_at/i.test(seed),
    'the seed has no EXPIRED notification; product/notification-backlog.md reasons about exactly this row',
  );
  assert.ok(
    /'Running'/.test(seed),
    'the seed has no Running voice_preview_jobs row; docs/backup.md records it as a restored Running claim',
  );
  // The expired row must genuinely be in the past relative to the read row, or
  // "expired" is a comment rather than a state: read_at = created_at = 2026-09-01
  // and expires_at = 2026-09-02, both before the other rows' October dates.
  assert.ok(
    seed.includes("'2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z', '2026-09-02T00:00:00Z'"),
    "the seed's expired notification does not have expires_at after a read_at in the past",
  );
  assert.ok(
    seed.includes("'2026-09-02T00:00:00Z'"),
    "the seed's expired notification has no expires_at",
  );
});

// -------------------------------------------------------------- the drill -----

test('the drill carries its own copy of the required groups', () => {
  // The D2 fix. If this list is deleted from the script, the drill goes back to
  // verifying scope.json against itself.
  const match = drill.match(/REQUIRED_GROUPS=\(([\s\S]*?)\)/);
  assert.ok(match, 'scripts/restore-drill.sh has no REQUIRED_GROUPS list; the completeness check has been removed');
  for (const required of REQUIRED_GROUPS) {
    assert.ok(
      match[1].includes(required.id),
      `scripts/restore-drill.sh does not require the group "${required.id}"`,
    );
  }
});

test('every group table participates in at least one referential check', () => {
  // D6. Found by truncating referentialChecks to three: the drill ran three
  // orphan queries, found nothing, and printed a PASS byte-identical to a clean
  // run. The floor is therefore asserted HERE as well as enforced in the script,
  // because a floor that lives only in the script is a floor whose removal is
  // invisible until a restore is incoherent.
  //
  // "Participates", not "is the child side": `tenant_users` is only ever a
  // PARENT (nothing in the scope points at a user that does not exist *from*
  // tenant_users), and it must be, because it is a root of the reference graph. A
  // group that is missing from every check is the undetectable case — a partial
  // restore of it produces no orphan anywhere.
  const participating = new Set();
  for (const rule of scope.referentialChecks) {
    for (const side of rule.split('->')) {
      participating.add(side.trim().split('.')[0]);
    }
  }
  for (const required of REQUIRED_GROUPS) {
    assert.ok(
      participating.has(required.table),
      `no referential check mentions ${required.table}; a partial restore of that group would be undetectable`,
    );
  }
  // And the declared count must be at least the group count, which is the floor
  // the script enforces.
  assert.ok(
    scope.referentialChecks.length >= REQUIRED_GROUPS.length,
    `scope.json declares ${scope.referentialChecks.length} referential checks for ${REQUIRED_GROUPS.length} groups`,
  );
});

test('the drill runs the four checks that catch a silent failure', () => {
  // Each of these is a failure mode that reports success.
  assert.ok(
    /rowsecurity FROM pg_tables/.test(drill),
    'the drill does not check that row level security is enabled before writing (D1)',
  );
  assert.ok(
    /REASONS_EMPTY_RESTORE/.test(drill) && /EMPTY_RESTORE/.test(drill),
    'the drill does not fail separately on an empty restore (D3); pg_restore reports success after inserting zero rows',
  );
  assert.ok(
    /referentialChecks/.test(drill),
    'the drill does not run the referential checks (D4); the schema has no foreign keys between these tables',
  );
  // The floor is `${#GROUP_TABLES[@]}` and the comparison is `-lt`. Asserted
  // separately because either half alone is satisfied by a comment.
  assert.ok(
    /FLOOR="\$\{#GROUP_TABLES\[@\]\}"/.test(drill),
    'the drill does not define a referential-check floor of one per group table (D6)',
  );
  assert.ok(
    /\[ "\$EXPECTED_CHECKS" -lt "\$FLOOR" \]/.test(drill),
    'the drill does not compare the declared check count against the floor (D6)',
  );
  // D5, and its extension to all seven groups. Before Task 047 the drill
  // spot-read ONE row that happened to be a three-table join, so four of the
  // seven groups were verified by count alone - the same defect one level down.
  // Each group is asserted here to have its own read, by its own predicate,
  // against its own marker, so removing a group's read fails this test rather
  // than shortening the drill quietly.
  for (const required of REQUIRED_GROUPS) {
    assert.ok(
      drill.includes(`|${required.table}|`),
      `the drill has no per-group spot-read for ${required.table} (group ${required.id}); a group verified by count only is a group whose wrong rows restore cleanly`,
    );
  }
  // The floor is a CONSTANT, and both the constant and the comparison are
  // asserted, because either half alone is satisfied by a comment.
  assert.ok(
    /readonly SPOT_REQUIRED=7/.test(drill),
    'the drill does not hold a constant for the number of per-group spot-reads it must run; a floor derived from the list it iterates shrinks when the list does',
  );
  assert.ok(
    /\[ "\$\{#SPOT_CHECKS\[@\]\}" -eq "\$SPOT_REQUIRED" \]/.test(drill),
    'the drill does not compare the spot-read list length against the required count',
  );
  // The result line reports how many reads ran, so a drill that died before
  // step 9 says spot=0 rather than saying nothing at all.
  assert.ok(
    /RESTORE_DRILL_RESULT reason=\$\{REASON\} status=\$\{STATUS\} tables=\$\{tables\} rows=\$\{rows\} gaps=\$\{GAPS\} spot=\$\{SPOT_READS\}/.test(drill),
    'the drill result line does not report the number of per-group spot-reads that ran',
  );
  // And the column-set assertion, which is the only check that survives a
  // pre-expand archive: the row count matches and the names are NULL.
  //
  // The EXACT comparison is asserted, not merely the presence of the query. The
  // first version checked that `information_schema.columns` and the migration
  // name appeared somewhere in the script, and weakening `[ "$COLUMNS" = "9" ]`
  // to `[ "$COLUMNS" -ge 0 ]` - which makes the assertion vacuous while leaving
  // both strings in the file - passed it. A check that the right query is present
  // is not a check that the query is compared against the right answer.
  assert.ok(
    /information_schema\.columns/.test(drill) && /AddProductIdentityExtensions/.test(drill),
    "the drill does not assert the nine extended project-metadata COLUMNS; a restore from a pre-expand archive passes every count and every spot-read",
  );
  assert.ok(
    /\[ "\$COLUMNS" = "9" \]/.test(drill),
    'the drill does not compare the extended-column count against exactly 9; a weaker comparison leaves the check present and vacuous',
  );
  // The read must resolve to EXACTLY ONE row. `grep -q <marker>` against a
  // multi-row result is a check that cannot tell the right row from the right
  // characters, which is D5 inside the check written to catch D5 (gap D11).
  assert.ok(
    /a spot-read must resolve to exactly one/.test(drill),
    "the drill does not require a spot-read to return exactly one row; a read of a composite key by half of it returns several and passes on a substring",
  );
});

test('the drill refuses a non-loopback target without an explicit override', () => {
  // The safety property, asserted. A drill that will cheerfully restore over
  // whatever DRILL_HOST names is one `export` away from a production change.
  assert.ok(
    /DRILL_ALLOW_REMOTE/.test(drill) && /REMOTE_TARGET_REFUSED/.test(drill),
    'the drill does not refuse a remote target without DRILL_ALLOW_REMOTE=1',
  );
  assert.match(
    drill,
    /DRILL_HOST[\s\S]{0,400}127\.0\.0\.1[\s\S]{0,400}DRILL_ALLOW_REMOTE/,
    'the loopback check does not cover DRILL_HOST',
  );
});

test('the drill never concatenates SQL and never interpolates an unvalidated identifier', () => {
  // The task's absolute rule, asserted rather than trusted. A table name from
  // scope.json reaches psql only after a lowercase-identifier check.
  assert.ok(
    /\[a-z_\]\*\)/.test(drill),
    'the drill does not validate table identifiers before using them',
  );
  assert.ok(
    /table name is not a bare lowercase identifier/.test(drill),
    'the drill does not fail on an unvalidated table identifier',
  );
  // A psql invocation may interpolate a variable in exactly two positions, and
  // both are identifiers that passed the check above:
  //   1. the FROM clause of a count, and
  //   2. the predicate of the row-security lookup.
  // Anything else - a value, a whole clause, a table list - is string-built SQL.
  const invocations = [...drill.matchAll(/psql_run -c "([^"]*)"/g)].map((m) => m[1]);
  assert.ok(invocations.length >= 5, `only ${invocations.length} psql invocations found; the regex is probably wrong`);

  const ALLOWED = [
    // 1. A count against one validated table name.
    /^SELECT count\(\*\) FROM \$\{?[A-Za-z_]+\}?;?$/,
    // 2. The row-security lookup, keyed on one validated table name.
    /^SELECT rowsecurity FROM pg_tables WHERE schemaname='public' AND tablename='\$\{?[A-Za-z_]+\}';?$/,
    // 3. A referential check, generated by the `node -e` block above it from
    //    scope.json's `referentialChecks`. It is a whole statement rather than an
    //    identifier, which is why the generator validates every table and column
    //    it emits before the statement is built - asserted just below.
    /^\$check$/,
  ];

  for (const query of invocations) {
    const normalised = query.replace(/\n\s+/g, ' ').trim();
    const allowed = ALLOWED.some((pattern) => pattern.test(normalised));
    // A variable anywhere in the query that is not `$t` is the thing to catch.
    const variables = [...normalised.matchAll(/\$\{?([A-Za-z_]+)/g)].map((m) => m[1]);
    const unvalidatedVariables = variables.filter((name) => name !== 't');
    assert.ok(
      allowed || unvalidatedVariables.length === 0,
      `a psql query is string-built with an unvalidated variable: ${normalised.slice(0, 120)}`,
    );
  }

  // The referential-check generator is the one place a whole statement is built
  // from scope.json, so the validation it performs is asserted rather than
  // assumed: every table and column in a `child.col -> parent.col` rule must be a
  // bare lowercase identifier, or a compromised scope file turns a coverage file
  // into a SQL injection point.
  // Located by the DANGLING sentinel that immediately precedes it, not by
  // searching for `node -e` - the drill has three of those blocks (the table
  // list, the group declaration, the referential checks) and picking the first
  // would assert against the wrong one.
  const sentinel = drill.indexOf('DANGLING=0');
  assert.ok(sentinel > 0, 'the referential-check section could not be located in the drill');
  const start = drill.indexOf("node -e '", sentinel);
  const end = drill.indexOf("' \"$SCOPE\"", start);
  assert.ok(start > sentinel && end > start, 'the referential-check generator could not be located in the drill');
  const generator = drill.slice(start, end);
  assert.ok(
    /IDENTIFIER_RE/.test(generator) && /throw new Error/.test(generator),
    'the referential-check generator does not validate every identifier it emits; a scope file is data, not trusted input',
  );

  // The wipe must be TRUNCATE, by validated name, not DELETE.
  assert.ok(
    /TRUNCATE TABLE \$t CASCADE/.test(drill),
    'the wipe is not TRUNCATE-by-validated-name; a partial wipe makes the count comparison meaningless',
  );
  assert.ok(
    !/DELETE FROM \$t/.test(drill),
    'the drill wipes with DELETE; a DELETE that fails partway leaves a half-empty table and the count comparison then passes',
  );

  // The per-group spot-read (Task 047) is the second place the drill builds a
  // whole statement, and the assertion that it validates its identifiers is here
  // for the same reason the referential-check generator's is. A spot-read that
  // interpolated an unvalidated name would be a SQL injection point in the one
  // step that runs against a restored instance.
  const spotSection = drill.slice(drill.indexOf('SPOT_CHECKS=('));
  assert.ok(spotSection.length > 0, 'the per-group spot-read section could not be located in the drill');
  assert.ok(
    /spot-read table is not a bare lowercase name/.test(spotSection),
    'the spot-read does not validate its table name before building a statement',
  );
  assert.ok(
    /spot-read marker column is not a bare lowercase name/.test(spotSection),
    'the spot-read does not validate the column it selects',
  );
  // The predicate values are checked against a character class that admits only
  // a synthetic `de71…` uuid or a bare lowercase literal. The message names both
  // because the drill's own composite key needs both: `preferences` is read on
  // `user_id = de71…` AND `key = ui.theme`, and a rule that only allowed uuids
  // could not express the second half of that predicate.
  assert.ok(
    /is neither a synthetic de71 id nor a bare lowercase literal/.test(spotSection),
    'the spot-read does not refuse a predicate value that is neither synthetic nor a bare lowercase literal; a real tenant id in a drill is a drill that reads real data',
  );
  // The value must be a lowercase literal, not merely a checked string: a quote,
  // a space or a semicolon has to be refused rather than escaped, since this
  // script builds quoted SQL and a value it had to escape is one it should not
  // accept at all.
  assert.ok(
    /de71\[0-9a-f-\]\*\|\[a-z0-9._-\]\*\)/.test(spotSection),
    "the spot-read's value character class admits a quote or whitespace, so the value is escaped rather than refused",
  );
  // The statement goes to psql on STDIN, never as an argument: an argument is in
  // the process table and in `ps` output for every other process on the host.
  assert.ok(
    /\| psql_run 2>"\$WORK\/spot\.err"/.test(spotSection),
    "the spot-read does not send its statement to psql on stdin; an argv SQL string is visible in the process table",
  );
});

test('the drill result line and its reason vocabulary are closed and documented', () => {
  assert.ok(
    /RESTORE_DRILL_RESULT reason=\$\{REASON\} status=\$\{STATUS\}/.test(drill),
    'the drill does not end with the machine-readable result line',
  );
  // Every REASONS_* constant the script defines must be documented in
  // docs/backup.md or docs/ci-branch-protection.md, or a release job cannot
  // branch on it.
  const reasons = [...drill.matchAll(/readonly REASONS_[A-Z_]+="([A-Z_]+)"/g)].map((m) => m[1]);
  assert.ok(reasons.length >= 8, `only ${reasons.length} reasons found; the vocabulary is probably not being read`);
  const ci = readFileSync(join(REPO_ROOT, 'docs/ci-branch-protection.md'), 'utf8');
  for (const reason of reasons) {
    assert.ok(
      ci.includes(reason),
      `the drill can emit reason=${reason}, which docs/ci-branch-protection.md does not list; a release job cannot branch on an undocumented reason`,
    );
  }
});

// ------------------------------------------------------------------ the doc ---

test('docs/backup.md names every table and every group (R2, from the doc side)', () => {
  for (const required of REQUIRED_GROUPS) {
    assert.ok(doc.includes(required.table), `docs/backup.md does not mention ${required.table}`);
  }
  for (const column of EXTENDED_PROJECT_COLUMNS) {
    assert.ok(doc.includes(column), `docs/backup.md does not mention the extended project column ${column}`);
  }
});

test('docs/backup.md records the drill with a date, a result and its gaps', () => {
  // R3: "Restore drill recorded (date + result), not just documented."
  // The heading used to read "### 2026-10-01 — new-entity restore drill", and
  // Task 047 restructured the section into "## Recorded drill results" with
  // dated sub-headings beneath it - so this anchor stopped matching while the
  // record was still there and as complete as ever. That is the shape of every
  // defect in this repository's history: a rule that fails because the prose
  // moved, and which then gets deleted or loosened. The anchor is now on the
  // DATE and the word "drill" in the same heading, which is what the requirement
  // actually is.
  const recorded = doc.match(/^#{2,4}[^\n]*\d{4}-\d{2}-\d{2}[^\n]*drill/im);
  assert.ok(recorded, 'docs/backup.md has no dated drill heading');
  assert.ok(/RESTORE_DRILL_RESULT/.test(doc), 'the recorded drill does not quote the machine-readable result line');
  // The verdict must be its own token on its own line, so a `PASS` quoted later in
  // the paragraph — or inside the `RESTORE_DRILL_RESULT` template above it — cannot
  // satisfy this. Anchored, and either bold arrangement is accepted because the
  // point is the verdict, not the punctuation.
  assert.ok(
    /^\s*[-*]\s+\*{0,2}Result\*{0,2}:?\s*\*{0,2}(PASS|FAIL)\b/m.test(doc),
    'the recorded drill has no PASS/FAIL verdict on its Result line',
  );
  // And specifically a PASS, since a FAIL is a failed task, not a recorded drill.
  assert.ok(
    /^\s*[-*]\s+\*{0,2}Result\*{0,2}:?\s*\*{0,2}PASS\b/m.test(doc),
    'the recorded drill result is not PASS',
  );
  assert.ok(/\*\*Gaps/i.test(doc), 'docs/backup.md records no gaps for the drill');
  // Every gap row must name an owner, or it is a complaint rather than a task.
  //
  // The gap table is located BY ITS HEADER, not by a row pattern, and the reason
  // is specific: Task 047 added an escalation table whose rows have the same
  // shape as gap rows (`| **L1** | ... | 15 min | ... |`), so the widened row
  // pattern matched the escalation ladder and the test failed on a row that was
  // never a gap. On a page with several tables, "a row that looks like a gap row"
  // is not a definition of a gap row. Bounding the search at the next `##`
  // heading is the version that means what it says.
  const gapTable = locateTable(doc, 'Gap');
  const gapRows = gapTable.match(/^\|\s*\*\*[A-Z]\d+[a-z]?\*\*\s*\|.*$/gm) ?? [];
  assert.ok(gapRows.length >= 5, `only ${gapRows.length} gap rows found; the gaps table is probably not being read`);
  for (const row of gapRows) {
    // Somewhere in the row, a three-digit task number - optionally with a letter
    // suffix, so `043C` and `047` both match - or a named accountable party. A
    // `CHANGE_ME` owner is not an owner.
    //
    // The suffix matters and cost a round trip: `\b\d{3}\b` does not match
    // `043C`, because the `C` means there is no word boundary after the digits.
    // The first version of this pattern was `\| \d{3}[A-Z]?[^|]*\|`, which
    // required the owner to be the SECOND cell and therefore matched only the
    // handful of rows whose owner happened to sit there.
    assert.ok(
      /\b\d{3}[A-Z]?\b|[a-z]+ (?:review| team| lead|on-call)/i.test(row),
      `a gap row names no owner: ${row.slice(0, 90)}`,
    );
    assert.ok(!/\| *CHANGE_ME *\|/.test(row), `a gap row names CHANGE_ME as its owner: ${row.slice(0, 90)}`);
  }
});

/**
 * The body of a markdown table, located by a header cell and bounded at the next
 * heading of the same or higher level.
 *
 * Exists because this page now has three tables whose rows are shaped alike -
 * gaps, escalation levels, coverage groups - and a row-shape pattern cannot tell
 * them apart. A table whose column count changes is a formatting change; a page
 * that gains a table is a content change, and neither should be able to silently
 * redirect a check onto the wrong rows.
 */
function locateTable(text, headerCell) {
  const start = text.search(new RegExp(`^\\|\\s*#\\s*\\|\\s*${headerCell}\\s*\\|`, 'm'));
  assert.ok(start > 0, `docs/backup.md has no table with a "${headerCell}" header column`);
  const rest = text.slice(start);
  const end = rest.slice(1).search(/^#{1,4} /m);
  return end > 0 ? rest.slice(0, end + 1) : rest;
}

test('docs/backup.md states the media half is not covered by this drill', () => {
  // The claim this page exists to keep honest: a database restore does not bring
  // back a media file. If the sentence is removed, the coverage table reads as
  // "everything is backed up".
  assert.ok(
    /does not bring back a media file/i.test(doc),
    'docs/backup.md does not state that a database restore does not bring back a media file',
  );
  assert.ok(
    /D1a|D2a|D3a/.test(doc),
    'docs/backup.md records no open gaps; the drill verified half the system and the page must say so',
  );
  // Every gap the script names by id must be documented here, and vice versa, so
  // the two cannot drift into describing different sets of findings.
  const scriptGapIds = new Set(
    [...drill.matchAll(/gap (D\d+[a-z]?) in docs\/backup\.md/g)].map((m) => m[1]),
  );
  for (const id of scriptGapIds) {
    assert.ok(doc.includes(id), `scripts/restore-drill.sh refers to gap ${id}, which docs/backup.md does not record`);
  }
  // Every gap row is `| **Dn** | description | severity | owner | status |`, and
  // the status cell is bold, so the row is matched as a whole rather than by
  // column count - a table whose column count changes is a formatting change,
  // not a coverage change, and this assertion is about coverage.
  //
  // The id pattern is `[A-Z]\d+[a-z]?`, not `D\d+[a-z]?`, because Task 047 added
  // an `F`-prefixed series (F1 the unrun full-system drill, F2 the unmeasured
  // RTO, F3 the PITR-only tier) for gaps that are not defects in the drill. The
  // original pattern did not match them, so every assertion below silently
  // applied to the D-series only and a malformed F-row would have passed.
  const gapTable = locateTable(doc, 'Gap');
  const gapRows = [...gapTable.matchAll(/^\|\s*\*\*([A-Z]\d+[a-z]?)\*\*\s*\|(.+)$/gm)];
  assert.ok(gapRows.length >= 8, `only ${gapRows.length} gap rows found in the gaps table; the table is probably not being read`);
  for (const [, id, rest] of gapRows) {
    assert.ok(
      /\*\*fixed\*\*|\*\*open\*\*/.test(rest),
      `gap ${id} has no status cell (expected **fixed** or **open**): ${rest.slice(-60)}`,
    );
    // The owner cell accepts a task number OR a named team, for the reason given
    // in the other gap test: 047 added a row owned by `security review`, because
    // widening the network reach of the one workload that can DDL is not a
    // numbered task's work. What it must not be is a `CHANGE_ME` placeholder.
    assert.ok(
      /\b\d{3}[A-Z]?\b|[a-z]+ (?:review| team| lead| on-call)/i.test(rest),
      `gap ${id} names no owner: ${rest.slice(-70)}`,
    );
    assert.ok(!/CHANGE_ME/.test(rest), `gap ${id} names CHANGE_ME as its owner: ${rest.slice(-70)}`);
  }
  const docFixed = gapRows.filter(([, , rest]) => /\*\*fixed\*\*/.test(rest));
  assert.ok(
    docFixed.length >= 5,
    `docs/backup.md records only ${docFixed.length} fixed gaps; the drill found more than that by being run`,
  );
  // And the open ones are not decoration: 047's blocking gaps are the whole
  // argument for the release gate existing, so a register with none of them
  // visible is a register nobody is maintaining.
  const docOpen = gapRows.filter(([, , rest]) => /\*\*open\*\*/.test(rest));
  assert.ok(docOpen.length >= 3, `only ${docOpen.length} open gap(s) recorded; the register should name what is still broken`);
});

test('docs/backup.md links Plan A rather than forking the procedure', () => {
  for (const link of ['dr/backup-restore.md', 'runbooks/backup-restore.md', 'dr/drill-log.md']) {
    assert.ok(doc.includes(`](${link})`), `docs/backup.md does not link ${link}; the procedure is Plan A's and is not restated`);
  }
  // And it must not restate the PITR command sequence.
  assert.ok(
    !/pg_restore --target-time=/.test(doc),
    'docs/backup.md restates the PITR command from dr/backup-restore.md; that is a fork',
  );
});

test('docs/backup.md forbids secrets in a drill record', () => {
  assert.ok(
    /no drill log may contain/i.test(doc) && /connection string/i.test(doc),
    'docs/backup.md does not forbid connection strings in a drill record',
  );
  // And the page itself contains none.
  for (const pattern of [/eyJ[A-Za-z0-9_-]{10,}/, /Password=[^;"'\s]*[0-9]/i, /AKIA[0-9A-Z]{16}/]) {
    assert.equal(doc.match(pattern), null, 'docs/backup.md contains something shaped like a credential');
  }
});
