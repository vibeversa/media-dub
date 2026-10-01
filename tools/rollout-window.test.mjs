// The rollout-window tests (Task 043B, Testing).
//
// WHY THESE EXIST AND WHY THEY ARE NOT A DEPLOY VERIFICATION
// ---------------------------------------------------------
// The task types its test as "deploy verification, not unit", and the
// verification is `scripts/check-rollout-window.mjs` running against the
// repository's real migrations and its real records. This file is the other
// half and it is not a substitute: it proves each RULE fires on a fixture that
// violates exactly that rule, which the real repository cannot do - the real
// migrations are all legal, deliberately, so a rule that never fires is
// indistinguishable from a rule that is satisfied.
//
// The idiom throughout is one fixture per rule. A test that puts three
// violations in one migration and asserts `findings.length === 3` passes when
// one rule fires three times and the other two never fire at all.
//
// THE MIGRATION FIXTURES ARE REAL C#
// ---------------------------------
// A JavaScript object shaped like a migration would let the masker, the
// brace-matcher and the operation reader all agree on a fiction. The fixtures
// below are strings of the same shape `dotnet ef migrations add` emits,
// including the parts that break naive scanners: the generated `Down` that
// drops everything `Up` created, and `Sql("""...""")` raw literals whose bodies
// contain braces and semicolons.

import test from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  AUTHORIZATION_SOURCE_GLOBS,
  REQUIRED_MATRIX_CELLS,
  REQUIRED_ROLLBACK_TARGETS,
  addColumnIsNullableOrDefaulted,
  auditCompatMatrix,
  auditFlagRegister,
  auditMigrations,
  auditRehearsalRecord,
  classifySqlStatement,
  daysBetween,
  findUpBody,
  probeRoute,
  readDeclaredValue,
  readMigrationSources,
  readOperations,
  serverBasePath,
  stripCSharp,
  validateLedger,
} from './rollout-window.mjs';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const TODAY = '2026-09-30';

const read = (relativePath) => readFileSync(join(REPO_ROOT, relativePath), 'utf8');

/** A migration whose `Up` is `up` and whose `Down` is the generated inverse. */
function migration(name, up, down = '            migrationBuilder.DropTable(name: "widget");') {
  return `using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DubbingPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ${name} : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
${up}
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
${down}
        }
    }
}
`;
}

/** A ledger with one approved contract, merged over the defaults. */
function ledger(overrides = {}) {
  return {
    baseline: '20260101000000_Earlier',
    baselineReviewedOn: '2026-06-30',
    windowReleases: 1,
    approvedContracts: [],
    ...overrides,
  };
}

/** Runs the migration audit over one migration and returns only its findings. */
function auditOne(up, { down, id = '20260930000000_Probe', book = ledger(), sources = {} } = {}) {
  const all = { [id]: migration('Probe', up, down), ...sources };
  return auditMigrations({ sources: all, ledger: book, today: TODAY }).findings.filter((item) => item.migration === id);
}

// =============================================================================
// READING C#
// =============================================================================

test('stripCSharp preserves length, newlines and offsets', () => {
  const source = 'var a = "x";\n// comment\nvar b = 1;';
  const masked = stripCSharp(source);
  assert.equal(masked.length, source.length);
  assert.equal(masked.split('\n').length, source.split('\n').length);
  assert.equal(masked, 'var a =    ;\n          \nvar b = 1;');
});

test('stripCSharp masks a raw string literal body, so braces inside it do not count', () => {
  const source = 'migrationBuilder.Sql(\n    """\n    CREATE FUNCTION f() RETURNS trigger AS $$ BEGIN RETURN NEW; END; $$ LANGUAGE plpgsql;\n    """);';
  const masked = stripCSharp(source);
  assert.ok(!masked.includes('RETURN NEW'), 'the SQL body must be masked');
  assert.equal(masked.length, source.length);
});

test('findUpBody reads Up and ignores the generated Down', () => {
  const body = findUpBody(migration('Probe', '            migrationBuilder.AddColumn<bool>(name: "x", table: "t", type: "boolean", nullable: true);'));
  assert.ok(body !== null);
  assert.match(body.masked, /AddColumn/);
  assert.ok(!/DropTable/.test(body.masked), 'DropTable belongs to Down and must not be read');
  assert.match(body.text, /name: "x"/, 'the original text is kept, so a finding can name the column');
});

test('a Down that drops everything does not make the migration a contract', () => {
  const findings = auditOne('            migrationBuilder.CreateTable(name: "widget", columns: table => new { id = table.Column<int>(nullable: false) }, constraints: table => { table.PrimaryKey("pk_widget", x => x.id); });');
  assert.deepEqual(findings, []);
});

test('readOperations sees an operation written with a generic type argument', () => {
  // Eleven of this repository's additions are written `AddColumn<Guid>(`, and a
  // scanner that stops at `<` reads the file as having no operations at all -
  // which then reads as "no destructive operations", the worst possible answer.
  const source = migration('Probe', '            migrationBuilder.AddColumn<Guid>(name: "owner", table: "t", type: "uuid", nullable: true);');
  const operations = readOperations(findUpBody(source), source);
  assert.equal(operations.length, 1);
  assert.equal(operations[0].op, 'AddColumn');
});

test('lineOf reports a real line number, so a finding points at a place', () => {
  const source = migration('Probe', '            migrationBuilder.DropColumn(\n                name: "gone",\n                table: "t");');
  const operations = readOperations(findUpBody(source), source);
  assert.equal(operations[0].line, 13);
});

// =============================================================================
// R2 - the migration window
// =============================================================================

test('a nullable new column is an expand', () => {
  assert.deepEqual(auditOne('            migrationBuilder.AddColumn<int>(name: "n", table: "t", type: "integer", nullable: true);'), []);
});

test('a new column with a default is an expand, even NOT NULL', () => {
  assert.deepEqual(auditOne('            migrationBuilder.AddColumn<bool>(name: "b", table: "t", type: "boolean", nullable: false, defaultValue: false);'), []);
});

test('a NOT NULL new column with no default is a break, and the reason says why a schema diff misses it', () => {
  const findings = auditOne('            migrationBuilder.AddColumn<bool>(name: "b", table: "t", type: "boolean", nullable: false);');
  assert.equal(findings.length, 1);
  assert.equal(findings[0].verdict, 'break');
  assert.match(findings[0].reason, /first INSERT from a pod still running the previous release/);
});

test('addColumnIsNullableOrDefaulted reads both spellings of a default', () => {
  assert.equal(addColumnIsNullableOrDefaulted('name: "a", nullable: true'), true);
  assert.equal(addColumnIsNullableOrDefaulted('name: "a", nullable: false, defaultValue: 1'), true);
  assert.equal(addColumnIsNullableOrDefaulted('name: "a", nullable: false, defaultValueSql: "now()"'), true);
  assert.equal(addColumnIsNullableOrDefaulted('name: "a", nullable: false'), false);
});

test('a drop inside the window is a finding, with the migration and the line', () => {
  const findings = auditOne('            migrationBuilder.DropColumn(\n                name: "bus_name",\n                table: "outbox_state");');
  assert.equal(findings.length, 1);
  assert.equal(findings[0].verdict, 'contract');
  assert.equal(findings[0].subject, 'bus_name');
  assert.equal(findings[0].line, 13);
  assert.match(findings[0].reason, /release N stops writing, release N\+1 drops/);
});

test('a drop at or before the baseline is history, not a finding', () => {
  const findings = auditOne('            migrationBuilder.DropColumn(name: "bus_name", table: "outbox_state");', {
    id: '20260101000000_Earlier',
    book: ledger({ baseline: '20260101000000_Earlier' }),
  });
  assert.deepEqual(findings, []);
});

test('a drop inside the window is permitted by a ledger entry, and counted as ledgered', () => {
  const result = auditMigrations({
    sources: { '20260930000000_Probe': migration('Probe', '            migrationBuilder.DropColumn(name: "bus_name", table: "outbox_state");') },
    ledger: ledger({
      approvedContracts: [
        {
          migration: '20260930000000_Probe',
          approvedBy: 'change-approval-1234',
          approvedOn: '2026-09-20',
          reason: 'nothing has written the column since release N',
          followUpTask: '043D',
          reviewBy: '2026-12-31',
        },
      ],
    }),
    today: TODAY,
  });
  assert.deepEqual(result.findings, []);
  assert.equal(result.stats.ledgered, 1);
});

test('a rename is a contract operation', () => {
  const findings = auditOne('            migrationBuilder.RenameColumn(name: "a", table: "t", newName: "b");');
  assert.equal(findings.length, 1);
  assert.equal(findings[0].verdict, 'contract');
});

test('an in-place AlterColumn is a contract operation, not an expand', () => {
  const findings = auditOne('            migrationBuilder.AlterColumn<int>(name: "n", table: "t", type: "bigint", nullable: true);');
  assert.equal(findings.length, 1);
  assert.match(findings[0].reason, /An in-place type change is never the expand step/);
});

test('raw SQL that drops a table is caught, because no operation-name list can see it', () => {
  const findings = auditOne('            migrationBuilder.Sql(\n                """\n                DROP TABLE legacy_widget;\n                """);');
  assert.equal(findings.length, 1);
  assert.equal(findings[0].verdict, 'contract');
  assert.match(findings[0].reason, /Split this into two releases/);
});

test('raw SQL this gate cannot read is a finding, not a pass', () => {
  const findings = auditOne('            migrationBuilder.Sql(\n                """\n                DO $$ BEGIN PERFORM something_unexpected(); END $$;\n                """);');
  assert.ok(findings.length >= 1);
  // One per unreadable statement, not one per call: a `Sql` block is several
  // statements and two of them being safe says nothing about the third.
  assert.ok(findings.every((item) => item.verdict === 'unclassified'));
});

test('CREATE POLICY and ENABLE ROW LEVEL SECURITY on a table the same Up created is an expand', () => {
  const findings = auditOne(
    '            migrationBuilder.CreateTable(name: "widget", columns: table => new { id = table.Column<int>(nullable: false) }, constraints: table => { table.PrimaryKey("pk_widget", x => x.id); });\n' +
      '            migrationBuilder.Sql(\n                """\n                ALTER TABLE widget ENABLE ROW LEVEL SECURITY;\n                CREATE POLICY tenant_isolation ON widget USING (tenant_id = current_setting(\'app.tenant_id\', true)::uuid);\n                """);',
  );
  assert.deepEqual(findings, []);
});

test('row-level security on a PRE-EXISTING table is a break, not an additive op with a scary name', () => {
  const findings = auditOne(
    '            migrationBuilder.AddColumn<int>(name: "n", table: "existing", type: "integer", nullable: true);\n' +
      '            migrationBuilder.Sql(\n                """\n                ALTER TABLE existing ENABLE ROW LEVEL SECURITY;\n                """);',
  );
  assert.equal(findings.length, 1);
  assert.equal(findings[0].verdict, 'break');
  assert.match(findings[0].reason, /reads zero rows/);
});

test('an EF operation in neither set fails closed', () => {
  const findings = auditOne('            migrationBuilder.SomeFutureOperation(name: "x");');
  assert.equal(findings.length, 1);
  assert.equal(findings[0].verdict, 'unclassified');
});

test('a migration with no readable Up is a finding, not a silent pass', () => {
  const sources = { '20260930000000_Empty': 'public partial class Empty { }' };
  const result = auditMigrations({ sources, ledger: ledger(), today: TODAY });
  assert.equal(result.findings.length, 1);
  assert.equal(result.findings[0].verdict, 'unreadable');
});

test('an Up whose operations cannot be found is a finding: an empty reading is usually a brace failure', () => {
  const sources = { '20260930000000_Empty': migration('Empty', '            // nothing here') };
  const result = auditMigrations({ sources, ledger: ledger(), today: TODAY });
  assert.equal(result.findings.length, 1);
  assert.equal(result.findings[0].verdict, 'unreadable');
});

test('classifySqlStatement is a closed set and says so when it does not know', () => {
  assert.equal(classifySqlStatement('CREATE TABLE x (id int)').verdict, 'additive');
  assert.equal(classifySqlStatement('ALTER TABLE x ADD COLUMN y int').verdict, 'additive');
  assert.equal(classifySqlStatement('ALTER TABLE x ALTER COLUMN y TYPE bigint').verdict, 'contract');
  assert.equal(classifySqlStatement('DELETE FROM x').verdict, 'contract');
  assert.equal(classifySqlStatement('TRUNCATE x').verdict, 'contract');
  assert.equal(classifySqlStatement('GRANT SELECT ON x TO someone').verdict, 'unclassified');
});

// --- the ledger's own invariants ------------------------------------------------

test('a ledger that is not an object is refused, because a gate with no rule has no verdict', () => {
  assert.equal(validateLedger(null).length, 1);
  assert.equal(validateLedger([]).length, 1);
});

test('a ledger with no baseline puts the whole repository inside the window', () => {
  const errors = validateLedger(ledger({ baseline: undefined }));
  assert.equal(errors.length, 1);
  assert.match(errors[0], /no `baseline`/);
});

test('a baseline naming no migration is a window boundary that is nowhere', () => {
  const errors = validateLedger(ledger({ baseline: '20200101000000_Nope' }), { sources: { '20260930000000_Probe': '' } });
  assert.ok(errors.some((message) => /not a migration in this repository/.test(message)));
});

test('windowReleases must be exactly 1', () => {
  assert.ok(validateLedger(ledger({ windowReleases: 2 })).some((message) => /windowReleases` must be exactly 1/.test(message)));
});

test('an approved contract with no approver, date, reason or follow-up task is refused', () => {
  const errors = validateLedger(ledger({ approvedContracts: [{ migration: 'x' }] }));
  for (const field of ['approvedBy', 'approvedOn', 'reason', 'followUpTask', 'reviewBy']) {
    assert.ok(errors.some((message) => message.includes(`'${field}'`)), `expected a finding for ${field}`);
  }
});

test('an approval dated in the future is refused', () => {
  const errors = validateLedger(
    ledger({
      approvedContracts: [{ migration: 'x', approvedBy: 'a', approvedOn: '2027-01-01', reason: 'r', followUpTask: 't', reviewBy: '2027-06-01' }],
    }),
  );
  assert.ok(errors.some((message) => /dated 2027-01-01, which is in the future/.test(message)));
});

test('an expired contract approval is a finding - the task edge case, a switch left on forever', () => {
  const entry = { migration: '20260930000000_Probe', approvedBy: 'a', approvedOn: '2026-01-01', reason: 'r', followUpTask: 't', reviewBy: '2026-06-01' };
  const book = ledger({ approvedContracts: [entry] });
  const result = auditMigrations({
    sources: { '20260930000000_Probe': migration('Probe', '            migrationBuilder.DropTable(name: "w");'), '20260101000000_Earlier': migration('Earlier', '') },
    ledger: book,
    today: TODAY,
  });
  assert.deepEqual(result.ledgerErrors, [], result.ledgerErrors.join('\n'));
  assert.ok(result.findings.some((item) => item.verdict === 'stale' && /expired on 2026-06-01/.test(item.reason)));
});

// =============================================================================
// R3 - the compatibility matrix
// =============================================================================

const OPENAPI = {
  servers: [{ url: '/api/v1' }],
  paths: {
    '/projects': { post: {} },
    '/projects/{projectId}/workspace': { get: {} },
  },
};

const EXPECT_HEALTHY = { migrationCurrency: 'Healthy', write: 201, workspace: 200 };
const EXPECT_AHEAD = { migrationCurrency: 'Unhealthy', write: 201, workspace: 200 };

function matrixWith(runs, matrix, unavailable) {
  return {
    matrix: matrix ?? [
      { id: 'old-api-new-db', expect: EXPECT_HEALTHY, probes: ['GET /health -> migration-currency Healthy', 'GET /api/v1/projects/{projectId}/workspace -> 200'] },
      { id: 'new-api-old-db', expect: EXPECT_AHEAD, readinessNote: 'the build is ahead of its schema, which is the gate doing its job', probes: ['GET /health -> migration-currency Unhealthy'] },
      { id: 'new-api-new-db', expect: EXPECT_HEALTHY, probes: ['GET /health -> migration-currency Healthy'] },
    ],
    unavailable,
    runs,
  };
}

const PASSING_RUN = {
  date: '2026-09-28',
  results: [
    { id: 'old-api-new-db', result: 'PASS', detail: 'old image, head 20260922082522, migration-currency Healthy, POST 201, workspace 200', migrationCurrency: 'Healthy', writeStatus: 201, workspaceStatus: 200 },
    { id: 'new-api-old-db', result: 'PASS', detail: 'new image, head 20260921115016, migration-currency Unhealthy, POST 201, workspace 200', migrationCurrency: 'Unhealthy', writeStatus: 201, workspaceStatus: 200 },
    { id: 'new-api-new-db', result: 'PASS', detail: 'new image, head 20260922082522, migration-currency Healthy, POST 201, workspace 200', migrationCurrency: 'Healthy', writeStatus: 201, workspaceStatus: 200 },
  ],
};

const EXEMPTION = {
  cell: 'old-api-new-db',
  recordedOn: '2026-01-01',
  reason: 'there is no previous release to point an image at',
  unblockedWhen: 'the first tagged release exists',
  riskAccepted: 'a code-only rollback has nothing to roll back to',
  reviewBy: '2026-12-31',
};

test('a matrix with all three cells, a fresh passing run, and real routes passes', () => {
  const result = auditCompatMatrix({ matrix: matrixWith([PASSING_RUN]), openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.deepEqual(result.findings, []);
});

test('a missing cell is a finding that names what that cell would have proved', () => {
  const matrix = matrixWith([PASSING_RUN]);
  matrix.matrix = matrix.matrix.filter((cell) => cell.id !== 'old-api-new-db');
  const result = auditCompatMatrix({ matrix, openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.subject === 'old-api-new-db' && /rollback the window exists to permit/.test(item.reason)));
});

test('a probe against a route the document does not serve is a finding, not a 404 read as a pass', () => {
  const matrix = matrixWith([PASSING_RUN]);
  matrix.matrix[0].probes.push('GET /api/v1/projects/{projectId}/seggments -> 200');
  const result = auditCompatMatrix({ matrix, openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.verdict === 'probe-not-a-route'));
});

test('an unrun matrix is a finding, and says so in those words', () => {
  const result = auditCompatMatrix({ matrix: matrixWith([]), openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.verdict === 'never-run'));
});

test('a run that skipped a cell is a finding for that cell', () => {
  const run = { ...PASSING_RUN, results: PASSING_RUN.results.filter((entry) => entry.id !== 'new-api-old-db') };
  const result = auditCompatMatrix({ matrix: matrixWith([run]), openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.subject === 'new-api-old-db' && item.verdict === 'cell-not-run'));
});

test('a stale matrix is a finding, and the reason says it is not refreshable later', () => {
  const run = { ...PASSING_RUN, date: '2026-01-01' };
  const result = auditCompatMatrix({ matrix: matrixWith([run]), openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.verdict === 'stale' && /not refreshable later - it is lost/.test(item.reason)));
});

test('a cell that declares no expectations cannot be compared, and that is a finding', () => {
  const matrix = matrixWith([PASSING_RUN]);
  delete matrix.matrix[0].expect;
  const result = auditCompatMatrix({ matrix, openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.verdict === 'no-expectations'));
});

test('a run marked PASS whose statuses contradict the cell declaration is a finding', () => {
  // The false pass this matrix exists to prevent, in the form it actually takes:
  // the cell says `PASS`, and the numbers beside it say otherwise.
  const run = {
    ...PASSING_RUN,
    results: PASSING_RUN.results.map((entry) => (entry.id === 'new-api-new-db' ? { ...entry, migrationCurrency: 'Unhealthy' } : entry)),
  };
  const result = auditCompatMatrix({ matrix: matrixWith([run]), openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  const mismatch = result.findings.find((item) => item.verdict === 'status-mismatch');
  assert.ok(mismatch, 'the recorded status does not match the declared expectation');
  assert.match(mismatch.reason, /false pass/);
});

test('an expectation that is not Healthy must explain itself', () => {
  const matrix = matrixWith([PASSING_RUN]);
  delete matrix.matrix[1].readinessNote;
  const result = auditCompatMatrix({ matrix, openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.verdict === 'unexpected-verdict-unexplained'));
});

// --- declared exemptions --------------------------------------------------------

test('a fully-specified exemption is honoured, returned, and the exempt cell is not demanded', () => {
  const run = { ...PASSING_RUN, results: PASSING_RUN.results.filter((entry) => entry.id !== 'old-api-new-db') };
  const result = auditCompatMatrix({ matrix: matrixWith([run], undefined, [EXEMPTION]), openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.deepEqual(result.findings, []);
  assert.equal(result.exemptions.length, 1);
  assert.equal(result.exemptions[0].cell, 'old-api-new-db');
});

test('an exemption missing any of its five fields is refused', () => {
  for (const field of ['reason', 'unblockedWhen', 'riskAccepted', 'reviewBy']) {
    const entry = { ...EXEMPTION };
    delete entry[field];
    const result = auditCompatMatrix({ matrix: matrixWith([], undefined, [entry]), openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
    const incomplete = result.findings.find((item) => item.verdict === 'exemption-incomplete');
    assert.ok(incomplete, `an exemption with no ${field} must be refused`);
    assert.match(incomplete.reason, new RegExp(field));
  }
});

test('an expired exemption stops being accepted', () => {
  const result = auditCompatMatrix({ matrix: matrixWith([], undefined, [{ ...EXEMPTION, reviewBy: '2026-01-01' }]), openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.verdict === 'exemption-expired'));
});

test('an exemption for a cell that does not exist exempts nothing', () => {
  const result = auditCompatMatrix({ matrix: matrixWith([], undefined, [{ ...EXEMPTION, cell: 'nope' }]), openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.verdict === 'exemption-for-unknown-cell'));
});

test('a matrix where every cell is exempt is a finding however well each one is written', () => {
  const all = REQUIRED_MATRIX_CELLS.map((cell) => ({ ...EXEMPTION, cell }));
  const result = auditCompatMatrix({ matrix: matrixWith([], undefined, all), openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.verdict === 'never-run' && /measures nothing/.test(item.reason)));
});

test('the committed matrix is current, and its exemption names a real cell', () => {
  const matrix = JSON.parse(read('deploy/rollout/compat-matrix.json'));
  const result = auditCompatMatrix({
    matrix,
    openapi: JSON.parse(read('src/DubbingPlatform.Api/OpenApi/openapi.v1.json')),
    today: TODAY,
    maxAgeDays: 90,
  });
  const message = result.findings.map((item) => `${item.verdict} ${item.subject}: ${item.reason}`).join('\n');
  assert.equal(message, '', message);
});
test('a PASS with no detail is a finding', () => {
  const run = { ...PASSING_RUN, results: PASSING_RUN.results.map((entry) => ({ id: entry.id, result: 'PASS' })) };
  const result = auditCompatMatrix({ matrix: matrixWith([run]), openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.equal(result.findings.filter((item) => item.verdict === 'undetailed').length, REQUIRED_MATRIX_CELLS.length);
});

test('probeRoute returns the path verbatim, so a renamed parameter is caught', () => {
  assert.equal(probeRoute('GET /api/v1/projects/{projectId}/workspace -> 200'), '/api/v1/projects/{projectId}/workspace');
});

test('the hosting endpoints are exempt from the OpenAPI route check, because they are not in the document', () => {
  // They are registered by `MapHostingEndpoints` and are `AllowAnonymous`, so a
  // document that listed them would advertise unauthenticated paths. Asserting
  // them against `paths` is asserting against a set they are deliberately not in.
  assert.equal(probeRoute('GET /health/ready -> 200'), null);
  assert.equal(probeRoute('GET /health -> the migration-currency check reports Healthy'), null);
  assert.equal(probeRoute('GET /version -> 200'), null);
});

test("the document's base path is accounted for, so a real route does not read as missing", () => {
  assert.equal(serverBasePath(OPENAPI), '/api/v1');
  const result = auditCompatMatrix({ matrix: matrixWith([PASSING_RUN]), openapi: OPENAPI, today: TODAY, maxAgeDays: 90 });
  assert.ok(!result.findings.some((item) => item.verdict === 'probe-not-a-route'));
});

// =============================================================================
// R4 - the flag register
// =============================================================================

const FLAG_FILES = {
  'deploy/k8s/frontend/configmap.yaml': 'data:\n  VITE_ENABLE_DIAGNOSTICS: "false"\n  VITE_SENTRY_DSN: sentry-disabled\n',
  'frontend/.env.example': 'VITE_SSE_ENABLED=true\n',
  'src/DubbingPlatform.Application/Options/FeatureOptions.cs':
    'public sealed class FeatureOptions\n{\n    public bool VideoIntelligenceEnabled { get; set; } = false;\n}\n',
};

function registerWith(flags) {
  return {
    flags: flags.map((overrides) => ({
      key: 'VITE_ENABLE_DIAGNOSTICS',
      gates: 'presentation',
      authorization: false,
      default: 'false',
      declaredIn: 'deploy/k8s/frontend/configmap.yaml',
      reviewBy: '2026-12-29',
      ...overrides,
    })),
  };
}

const GOOD_FLAG = { key: 'VITE_ENABLE_DIAGNOSTICS' };

test('a well-formed register entry passes', () => {
  const result = auditFlagRegister({ register: registerWith([GOOD_FLAG]), files: FLAG_FILES, today: TODAY });
  assert.deepEqual(result.findings, []);
});

test('a flag whose declared default is not the recorded default is a finding', () => {
  const result = auditFlagRegister({ register: registerWith([{ ...GOOD_FLAG, default: 'true' }]), files: FLAG_FILES, today: TODAY });
  const finding = result.findings.find((item) => item.verdict === 'default-mismatch');
  assert.ok(finding, 'the register and the declaration disagree, and one of them is what a deploy will use');
  assert.match(finding.reason, /declares 'VITE_ENABLE_DIAGNOSTICS' as 'false'/);
});

test('a quoted YAML boolean and a bare one are the same value', () => {
  assert.equal(readDeclaredValue('  KEY: "false"\n', 'KEY'), 'false');
  assert.equal(readDeclaredValue('  KEY: false\n', 'KEY'), 'false');
  assert.equal(readDeclaredValue('KEY=true\n', 'KEY'), 'true');
  assert.equal(readDeclaredValue('public bool Flag { get; set; } = false;\n', 'Flag'), 'false');
});

test('a flag that gates authorization has nowhere to declare it', () => {
  const result = auditFlagRegister({ register: registerWith([{ ...GOOD_FLAG, gates: 'authorization' }]), files: FLAG_FILES, today: TODAY });
  assert.ok(result.findings.some((item) => item.verdict === 'bad-gate'));
});

test('a register entry that does not assert authorization:false is a finding', () => {
  const result = auditFlagRegister({ register: registerWith([{ ...GOOD_FLAG, authorization: true }]), files: FLAG_FILES, today: TODAY });
  assert.ok(result.findings.some((item) => item.verdict === 'authorizes'));
});

test('a flag with no review date is a finding - the task edge case', () => {
  const result = auditFlagRegister({ register: registerWith([{ ...GOOD_FLAG, reviewBy: undefined }]), files: FLAG_FILES, today: TODAY });
  assert.ok(result.findings.some((item) => item.verdict === 'no-review-date'));
});

test('a flag past its review date is a finding', () => {
  const result = auditFlagRegister({ register: registerWith([{ ...GOOD_FLAG, reviewBy: '2026-01-01' }]), files: FLAG_FILES, today: TODAY });
  assert.ok(result.findings.some((item) => item.verdict === 'review-overdue'));
});

test('a flag with no recorded default is a finding', () => {
  const result = auditFlagRegister({ register: registerWith([{ ...GOOD_FLAG, default: undefined }]), files: FLAG_FILES, today: TODAY });
  assert.ok(result.findings.some((item) => item.verdict === 'no-default'));
});

test('a flag that exists only in the register is documentation', () => {
  const result = auditFlagRegister({ register: registerWith([{ ...GOOD_FLAG, key: 'VITE_ENABLE_NOTHING' }]), files: FLAG_FILES, today: TODAY });
  assert.ok(result.findings.some((item) => item.verdict === 'not-declared'));
});

test('declaredAs lets a backend flag be checked against its C# property', () => {
  // Without `declaredAs` the gate looks for `Features__VideoIntelligenceEnabled`
  // in a .cs file, finds nothing, and reports the flag as undeclared - so a
  // register of purely backend flags would be unpassable, or would be "fixed"
  // by removing the check.
  const result = auditFlagRegister({
    register: registerWith([{ key: 'Features__VideoIntelligenceEnabled', declaredIn: 'src/DubbingPlatform.Application/Options/FeatureOptions.cs', declaredAs: 'VideoIntelligenceEnabled', default: 'false' }]),
    files: FLAG_FILES,
    today: TODAY,
  });
  assert.deepEqual(result.findings, []);
});

test('a register entry may not invent a declaration file', () => {
  const result = auditFlagRegister({ register: registerWith([{ ...GOOD_FLAG, declaredIn: 'somewhere/else.yaml' }]), files: FLAG_FILES, today: TODAY });
  assert.ok(result.findings.some((item) => item.verdict === 'undeclared-file'));
});

test('a flag referenced from the authorization code is a finding, whatever the register says', () => {
  const result = auditFlagRegister({
    register: registerWith([GOOD_FLAG]),
    files: FLAG_FILES,
    today: TODAY,
    authorizationSources: [
      {
        path: 'src/DubbingPlatform.Api/Auth/ProjectAuthorizationHandler.cs',
        text: 'public sealed class ProjectAuthorizationHandler { private readonly IOptions<FeatureOptions> _features; }',
      },
    ],
  });
  assert.ok(result.findings.some((item) => item.verdict === 'flag-in-authorization'));
});

test('an authorization file that does not mention a flag is silent', () => {
  const result = auditFlagRegister({
    register: registerWith([GOOD_FLAG]),
    files: FLAG_FILES,
    today: TODAY,
    authorizationSources: [{ path: 'src/DubbingPlatform.Api/Auth/Roles.cs', text: 'public static class Roles { }' }],
  });
  assert.deepEqual(result.findings, []);
});

test('an empty register is a finding: it is indistinguishable from an unfilled one', () => {
  const result = auditFlagRegister({ register: { flags: [] }, files: FLAG_FILES, today: TODAY });
  assert.ok(result.findings.some((item) => item.verdict === 'empty'));
});

// =============================================================================
// R5 - the rollback rehearsal record
// =============================================================================

function rehearsalWith(overrides = {}) {
  return {
    date: '2026-09-29',
    environment: 'kind v1.34.0, single node, ephemeral',
    result: 'PASS',
    targets: REQUIRED_ROLLBACK_TARGETS.map((name) => ({
      deployment: name,
      revisionBefore: 10,
      revisionAfter: 11,
      imageBefore: 'dubbing-rehearsal:rev2',
      imageAfter: 'dubbing-rehearsal:rev1',
      result: 'PASS',
      detail: `rollout undo moved the Deployment 10 -> 11 and the pod template rev2 -> rev1`,
    })),
    notCovered: ['the application', 'the database'],
    ...overrides,
  };
}

test('a complete rehearsal record passes', () => {
  const record = rehearsalWith();
  record.targets[0].servedBefore = 'rehearsal-rev2';
  record.targets[0].servedAfter = 'rehearsal-rev1';
  assert.deepEqual(auditRehearsalRecord({ record, today: TODAY, maxAgeDays: 90 }).findings, []);
});

test('the revision annotation INCREMENTS on an undo, so a decrement is not the success condition', () => {
  // `rollout undo` records the previous ReplicaSet's template as a NEW revision;
  // it does not move the number back. Reading the success condition as
  // "revisionAfter < revisionBefore" rejects every correct rehearsal, which is
  // what this module's first version did - found by running the rehearsal and
  // reading the record it produced, not by reading the code.
  const record = rehearsalWith();
  record.targets[0].servedBefore = 'rehearsal-rev2';
  record.targets[0].servedAfter = 'rehearsal-rev1';
  record.targets[0].revisionAfter = 9;
  const result = auditRehearsalRecord({ record, today: TODAY, maxAgeDays: 90 });
  assert.deepEqual(result.findings, [], 'a rollback that recorded revision 9 is a rollback');
});

test('an undo that left the revision annotation unchanged has undone nothing', () => {
  const record = rehearsalWith();
  record.targets[0].servedBefore = 'rehearsal-rev2';
  record.targets[0].servedAfter = 'rehearsal-rev1';
  record.targets[0].revisionAfter = record.targets[0].revisionBefore;
  const result = auditRehearsalRecord({ record, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.subject === 'dubbing-api' && item.verdict === 'no-revision-change'));
});

test('a revision of 0 is a failed read recorded as a measurement, not a revision', () => {
  // The real defect: a run wrote "revision 10 -> 0" into the record and reported
  // `ok`, because the success condition is "the number CHANGED" and 0 is not 10.
  const record = rehearsalWith();
  record.targets[0].servedBefore = 'rehearsal-rev2';
  record.targets[0].servedAfter = 'rehearsal-rev1';
  record.targets[0].revisionAfter = 0;
  const result = auditRehearsalRecord({ record, today: TODAY, maxAgeDays: 90 });
  const finding = result.findings.find((item) => item.subject === 'dubbing-api' && item.verdict === 'impossible-revision');
  assert.ok(finding, `expected impossible-revision, got ${JSON.stringify(result.findings)}`);
  assert.match(finding.reason, /positive integer/);
  // It must not be the ONLY thing wrong: a fabricated revision also means the
  // number changed for no reason, which is the same claim as no change at all.
  assert.equal(result.ok, false);
});

test('a negative or fractional revision is refused the same way', () => {
  for (const value of [-1, 1.5]) {
    const record = rehearsalWith();
    record.targets[0].revisionAfter = value;
    const result = auditRehearsalRecord({ record, today: TODAY, maxAgeDays: 90 });
    assert.ok(
      result.findings.some((item) => item.subject === 'dubbing-api' && item.verdict === 'impossible-revision'),
      `expected impossible-revision for ${value}`,
    );
  }
});

test('the real rehearsal record on disk passes the revision rules', () => {
  // The rule above was written after reading a committed record, so the committed
  // record has to satisfy it. This is the assertion that keeps the two honest.
  const record = JSON.parse(readFileSync(new URL('../deploy/rollout/rollback-rehearsal.json', import.meta.url), 'utf8'));
  for (const target of record.targets) {
    assert.ok(Number.isInteger(target.revisionBefore) && target.revisionBefore >= 1, `${target.deployment} revisionBefore`);
    assert.ok(Number.isInteger(target.revisionAfter) && target.revisionAfter >= 1, `${target.deployment} revisionAfter`);
  }
});

test('a new revision whose pod template is unchanged is a rollback to the same build', () => {
  const record = rehearsalWith();
  record.targets[0].servedBefore = 'rehearsal-rev2';
  record.targets[0].servedAfter = 'rehearsal-rev1';
  record.targets[0].imageAfter = record.targets[0].imageBefore;
  const result = auditRehearsalRecord({ record, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.subject === 'dubbing-api' && item.verdict === 'no-image-change'));
});

test('the API must be confirmed by what it SERVES, not by what the cluster believes', () => {
  const record = rehearsalWith();
  const result = auditRehearsalRecord({ record, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.subject === 'dubbing-api' && item.verdict === 'no-served-version'));
});

test('a rollback that moved the pointer and not the build is caught', () => {
  const record = rehearsalWith();
  record.targets[0].servedBefore = 'rehearsal-rev2';
  record.targets[0].servedAfter = 'rehearsal-rev2';
  const result = auditRehearsalRecord({ record, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.subject === 'dubbing-api' && item.verdict === 'served-unchanged'));
});

test('a rehearsal that skipped the frontend is a finding', () => {
  const record = rehearsalWith();
  record.targets[0].servedBefore = 'rehearsal-rev2';
  record.targets[0].servedAfter = 'rehearsal-rev1';
  record.targets = record.targets.filter((entry) => entry.deployment !== 'frontend');
  const result = auditRehearsalRecord({ record, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.subject === 'frontend' && item.verdict === 'not-rehearsed'));
});

test('a rehearsal with no record of what it did not cover is a finding', () => {
  const record = rehearsalWith({ notCovered: [] });
  const result = auditRehearsalRecord({ record, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.verdict === 'unqualified'));
});

test('a stale rehearsal is a finding', () => {
  const record = rehearsalWith({ date: '2025-01-01' });
  const result = auditRehearsalRecord({ record, today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.verdict === 'stale'));
});

test('a record whose result is not PASS is a finding', () => {
  const result = auditRehearsalRecord({ record: rehearsalWith({ result: 'NEVER_RUN' }), today: TODAY, maxAgeDays: 90 });
  assert.ok(result.findings.some((item) => item.subject === 'result'));
});

test("the committed rehearsal record is current, complete and qualified", () => {
  // The committed record, not a fixture: R5 is 'rehearsed and recorded', and a
  // record that drifts out of date has to fail the gate rather than sit there.
  const record = JSON.parse(read('deploy/rollout/rollback-rehearsal.json'));
  const result = auditRehearsalRecord({ record, today: TODAY, maxAgeDays: 90 });
  const message = result.findings.map((item) => `${item.verdict} ${item.subject}: ${item.reason}`).join('\n');
  assert.equal(message, '', message);
});

test('daysBetween is whole days and negative when the second precedes the first', () => {
  assert.equal(daysBetween('2026-09-01', '2026-09-30'), 29);
  assert.equal(daysBetween('2026-09-30', '2026-09-01'), -29);
  assert.equal(daysBetween('2026-09-30', '2026-09-30'), 0);
});

// =============================================================================
// THE REPOSITORY ITSELF
// =============================================================================

test("this repository's migrations are additive inside the window", () => {
  const sources = readMigrationSources(join(REPO_ROOT, 'src/DubbingPlatform.Infrastructure/Persistence/Migrations'));
  const result = auditMigrations({
    sources,
    ledger: JSON.parse(read('deploy/rollout/contract-ledger.json')),
    today: TODAY,
  });
  const message = result.findings.map((item) => `${item.migration}:${item.line} ${item.reason}`).join('\n');
  assert.equal(message, '', message);
  assert.equal(result.ledgerErrors.length, 0, result.ledgerErrors.join('\n'));
  assert.ok(result.stats.migrations >= 2, 'there must be a previous release to be compatible with');
});

test("the reader finds this repository's real operations, not zero of them", () => {
  const sources = readMigrationSources(join(REPO_ROOT, 'src/DubbingPlatform.Infrastructure/Persistence/Migrations'));
  const result = auditMigrations({ sources, ledger: JSON.parse(read('deploy/rollout/contract-ledger.json')), today: TODAY });
  // The vacuous-pass guard: an empty result because nothing was read is
  // indistinguishable from an empty result because nothing was wrong.
  assert.ok(result.stats.expand > 100, `only ${result.stats.expand} expand operations were read`);
  assert.equal(result.stats.contract, 2, 'exactly the two pre-baseline drops in AddProjectSoftDelete');
});

test("this repository's flag register matches every declaration", () => {
  const files = {
    'deploy/k8s/frontend/configmap.yaml': read('deploy/k8s/frontend/configmap.yaml'),
    'deploy/k8s/configmap.yaml': read('deploy/k8s/configmap.yaml'),
    'frontend/.env.example': read('frontend/.env.example'),
    'src/DubbingPlatform.Application/Options/FeatureOptions.cs': read('src/DubbingPlatform.Application/Options/FeatureOptions.cs'),
  };
  const authorizationSources = [];
  for (const prefix of AUTHORIZATION_SOURCE_GLOBS) {
    for (const name of walk(join(REPO_ROOT, prefix))) {
      authorizationSources.push({ path: name.replace(`${REPO_ROOT}/`, '').replace(/\\/g, '/'), text: readFileSync(name, 'utf8') });
    }
  }
  const result = auditFlagRegister({ register: JSON.parse(read('deploy/rollout/flags.json')), files, today: TODAY, authorizationSources });
  const message = result.findings.map((item) => `${item.verdict} ${item.key}: ${item.reason}`).join('\n');
  assert.equal(message, '', message);
});

/** Every file under a directory, recursively. A non-existent path yields nothing. */
function walk(absolute) {
  let entries;
  try {
    entries = readdirSync(absolute, { withFileTypes: true });
  } catch {
    return [];
  }
  return entries.flatMap((entry) => {
    const path = join(absolute, entry.name);
    if (entry.isDirectory()) return walk(path);
    return entry.isFile() && entry.name.endsWith('.cs') ? [path] : [];
  });
}
