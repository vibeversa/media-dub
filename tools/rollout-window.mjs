// The expand/contract window contract (Task 043B).
//
// WHAT THIS FILE IS
// -----------------
// The PURE decision layer for the four things a rollout can get wrong that are
// not a manifest and not a running container:
//
//   R2  a migration in the compatibility window that is not additive
//   R3  a compatibility-matrix claim that is not backed by a run
//   R4  a feature flag with no default, no review date, or that has quietly
//       become an access control
//   R5  a rollback that is documented but has not been rehearsed
//
// Pure: no filesystem, no network, no environment. Every function takes the text
// it reasons about as an argument, so `tools/rollout-window.test.mjs` can prove
// each rule with a fixture that violates exactly that rule and nothing else. The
// I/O is `scripts/check-rollout-window.mjs`, which is a thin CLI over this file.
//
// The manifest half of R1 (the migration Job runs before the API, `backoffLimit:
// 3`, a failing gate blocks pod start) is NOT here. It is a YAML shape question,
// and `deploy/verify.sh`'s structural tier already has a parsed PyYAML view of
// every manifest. Two answers to the same question in two languages is a drift
// risk, and the parsed one is the one that cannot be fooled by formatting.
//
// WHY A STATIC MIGRATION CHECK AND NOT `dotnet ef database update`
// ----------------------------------------------------------------
// `scripts/migration-compat.sh` already proves the chain APPLIES and that
// nothing the previous release needed disappeared, against a real PostgreSQL.
// It cannot be the whole of R2 for two reasons, and both are about timing:
//
//   * it needs Docker, so it is a release-pipeline gate, not the always-on early
//     gate, and a destructive migration is cheapest to refuse in the PR that
//     introduces it;
//   * it compares the previous head against the CURRENT head. A migration that
//     is destructive is allowed exactly once - the contract phase - and the
//     whole point of the contract phase is that it is a DELIBERATE, RECORDED,
//     separate release. A diff against the previous head cannot tell
//     "this release contracted, deliberately, with an approval" from "this
//     release broke the rule on a Tuesday".
//
// So this file reads the migration SOURCE and classifies each operation. It is
// a different question from the one `migration-compat.sh` asks, and both are
// needed: "did it apply" and "was it allowed to".
//
// THE LEDGER, AND WHY AN ALLOWLIST
// --------------------------------
// A ledger is an allowlist of destructive migrations, and the reason it is not
// a denylist is the same reason the frontend env allowlist is not a denylist: a
// rule that only fires on operations somebody thought of is a rule that misses
// the operation nobody thought of. `Sql` is the proof - raw SQL can drop a
// table, and no operation-name list can see it. So the ledger is a list of
// migrations PERMITTED to contain contract operations, each with who approved
// it, when, why, and which follow-up task completes the pair. An unlisted
// contract operation is a finding, and the finding says which file and line.

import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

/** Where the EF migrations live. Asserted by `deploy/verify.sh`. */
export const MIGRATIONS_DIR = 'src/DubbingPlatform.Infrastructure/Persistence/Migrations';

/**
 * EF operations that only ADD to the schema. Every one of these is safe for the
 * previous release's code, which is the whole definition of the expand phase.
 *
 * `InsertData` is in here and is the arguable one. It changes rows, not schema,
 * and the alternative - requiring a ledger entry for every seeded lookup row -
 * would make the ledger a file nobody keeps up to date, which is worse than the
 * thing it prevents. The operation that actually rewrites data (`UpdateData`,
 * `DeleteData`) is a contract operation, and a seed row that a reader depends
 * on is removed by a later `DeleteData` in the same ledgered contract phase.
 */
export const EXPAND_OPERATIONS = Object.freeze([
  'AddColumn',
  'AddCheckConstraint',
  'AddForeignKey',
  'AddPrimaryKey',
  'AddUniqueConstraint',
  'CreateIndex',
  'CreateSequence',
  'CreateTable',
  'EnsureSchema',
  'EnsureSequence',
  'InsertData',
]);

/**
 * EF operations that REMOVE or REDEFINE something the previous release's code
 * may depend on. A contract phase only; each needs a ledger entry.
 *
 * `AlterColumn` is here rather than in EXPAND because EF emits it for both
 * directions, and "the type changed" is not distinguishable from "the type
 * changed in a way old code cannot read". The window rule is that a type change
 * is expand (add a new column) + contract (drop the old one) across two
 * releases, never an in-place alter.
 */
export const CONTRACT_OPERATIONS = Object.freeze([
  'AlterColumn',
  'AlterDatabase',
  'AlterSequence',
  'DeleteData',
  'DropCheckConstraint',
  'DropColumn',
  'DropForeignKey',
  'DropIndex',
  'DropPrimaryKey',
  'DropTable',
  'DropUniqueConstraint',
  'RenameColumn',
  'RenameIndex',
  'RenameTable',
  'UpdateData',
]);

/**
 * Raw SQL statements, classified by what they do to the schema.
 *
 * A closed set, and CLOSED DELIBERATELY: anything not matched is `unclassified`,
 * which is a finding. A gate that guesses at SQL it does not recognise reports
 * that a migration it has not read is fine, and the SQL in this repository is
 * already non-trivial - `ENABLE ROW LEVEL SECURITY` and `CREATE POLICY` appear
 * in four of the seven migrations and are neither obviously additive nor
 * obviously destructive to somebody who has not read them.
 *
 * `ENABLE ROW LEVEL SECURITY` is additive on a table the migration itself
 * created in the same `Up`, which is how it is used here. On a PRE-EXISTING
 * table it is a behaviour change that can break old code that does not set
 * `app.tenant_id` - so it is additive only when the same `Up` created the
 * table, and `auditMigrations` is what enforces that, because the check needs
 * the whole `Up` and not one statement.
 */
const SQL_RULES = Object.freeze([
  { id: 'rls', re: /\bENABLE\s+ROW\s+LEVEL\s+SECURITY\b/i, verdict: 'rls', label: 'row-level security enabled' },
  { id: 'create-policy', re: /\bCREATE\s+POLICY\b/i, verdict: 'additive', label: 'a policy is created' },
  { id: 'create', re: /^\s*CREATE\b/i, verdict: 'additive', label: 'a CREATE statement' },
  { id: 'alter-add', re: /^\s*ALTER\s+TABLE\b[\s\S]*?\bADD\b/i, verdict: 'additive', label: 'an ALTER TABLE ... ADD' },
  { id: 'alter-other', re: /^\s*ALTER\s+TABLE\b/i, verdict: 'contract', label: 'an ALTER TABLE that is not ADD' },
  { id: 'insert', re: /^\s*INSERT\b/i, verdict: 'additive', label: 'an INSERT' },
  { id: 'update', re: /^\s*UPDATE\b/i, verdict: 'contract', label: 'an UPDATE of existing rows' },
  { id: 'delete', re: /^\s*DELETE\b/i, verdict: 'contract', label: 'a DELETE of existing rows' },
  { id: 'drop', re: /^\s*DROP\b/i, verdict: 'contract', label: 'a DROP' },
  { id: 'truncate', re: /^\s*TRUNCATE\b/i, verdict: 'contract', label: 'a TRUNCATE' },
  { id: 'rename', re: /^\s*ALTER\s+[^;]*?\bRENAME\b/i, verdict: 'contract', label: 'a RENAME' },
]);

/** A finding. `verdict` is the closed set a caller may branch on. */
export function finding(migration, line, subject, verdict, reason) {
  return { migration, line, subject, verdict, reason };
}

// =============================================================================
// READING C#
// =============================================================================

/**
 * Replaces every comment, string literal and character literal with spaces,
 * preserving length and newlines exactly.
 *
 * Length preservation is the point: the caller brace-matches on the result and
 * then slices the ORIGINAL at the offsets it found, so a finding can still
 * quote the real text and report a real line number. A masker that shortened
 * the output would silently break every offset after the first string.
 *
 * `"""\n...\n"""` raw string literals are handled because this repository's
 * migrations use them for their `Sql(...)` payloads, and their contents
 * routinely contain semicolons, quotes and - in a trigger body - braces. Brace
 * matching over unmasked SQL counts a trigger's `{` as a block and lands the
 * end of the `Up` body in the wrong place, which is how a check like this one
 * ends up reporting "no operations found" and reading that as "clean".
 *
 * A raw-string terminator is matched as the same run of quotes that opened it,
 * per the C# specification. A `""""`-style empty raw string would need the run
 * length to be tracked rather than assumed to be three; that is a documented
 * limitation, not a handled case, and it is listed in the report.
 */
export function stripCSharp(source) {
  const out = source.split('');
  const blank = (from, to) => {
    for (let i = from; i < to; i += 1) {
      if (out[i] !== '\n' && out[i] !== '\r') out[i] = ' ';
    }
  };
  const n = source.length;
  let i = 0;
  while (i < n) {
    const ch = source[i];
    // line comment
    if (ch === '/' && source[i + 1] === '/') {
      const end = source.indexOf('\n', i);
      blank(i, end === -1 ? n : end);
      i = end === -1 ? n : end;
      continue;
    }
    // block comment
    if (ch === '/' && source[i + 1] === '*') {
      const end = source.indexOf('*/', i + 2);
      blank(i, end === -1 ? n : end + 2);
      i = end === -1 ? n : end + 2;
      continue;
    }
    if (ch === "'") {
      let j = i + 1;
      while (j < n && source[j] !== "'") {
        if (source[j] === '\\') j += 1;
        if (source[j] === '\n') break;
        j += 1;
      }
      blank(i, Math.min(j + 1, n));
      i = Math.min(j + 1, n);
      continue;
    }
    if (ch === '"') {
      // A run of three or more opens a raw string literal.
      let run = 0;
      while (source[i + run] === '"') run += 1;
      if (run >= 3) {
        const terminator = '"'.repeat(run);
        const end = source.indexOf(terminator, i + run);
        blank(i, end === -1 ? n : end + run);
        i = end === -1 ? n : end + run;
        continue;
      }
      // `@"..."` is verbatim: the only escape is a doubled quote, but masking
      // backslashes as well costs nothing and cannot produce a false negative.
      const verbatim = source[i - 1] === '@';
      let j = i + 1;
      while (j < n) {
        if (!verbatim && source[j] === '\\') j += 2;
        else if (source[j] === '"') {
          if (verbatim && source[j + 1] === '"') j += 2;
          else break;
        } else if (source[j] === '\n') break;
        else j += 1;
      }
      blank(i, Math.min(j + 1, n));
      i = Math.min(j + 1, n);
      continue;
    }
    i += 1;
  }
  return out.join('');
}

/**
 * The `Up(MigrationBuilder)` method body, or `null`.
 *
 * `start` / `end` delimit the braces, `masked` is the body with every literal
 * and comment blanked, and `text` is the body verbatim. Both are returned
 * because they answer different questions: `masked` is what the brace-matcher
 * and the operation scanner must read, and `text` is what a finding message
 * has to quote. Reading the arguments out of `masked` finds no `name:` values
 * and no SQL, and produces a finding that cannot say which column it is about -
 * which is the same as a finding nobody can act on.
 *
 * Only `Up` is read, and that is not a shortcut: EF GENERATES `Down`, and in
 * every migration in this repository `Down` is longer than `Up` and contains
 * `DropTable` for every table `Up` created. A check that scanned the file
 * would report 56 contract operations in the first migration and refuse the
 * repository's own history. The EF bundle is forward-only
 * (`dotnet ef database update` has no down path for a bundle), so `Down` is
 * dead code at deploy time and must not be part of the contract.
 *
 * Brace-matched over the MASKED text, so braces inside SQL bodies, strings and
 * comments cannot move the end.
 */
export function findUpBody(source) {
  const masked = stripCSharp(source);
  const signature = masked.search(/\bvoid\s+Up\s*\(\s*MigrationBuilder\b/);
  if (signature === -1) return null;
  const open = masked.indexOf('{', signature);
  if (open === -1) return null;
  let depth = 0;
  for (let i = open; i < masked.length; i += 1) {
    if (masked[i] === '{') depth += 1;
    else if (masked[i] === '}') {
      depth -= 1;
      if (depth === 0) {
        return {
          start: open,
          end: i + 1,
          masked: masked.slice(open + 1, i),
          text: source.slice(open + 1, i),
        };
      }
    }
  }
  return null;
}

/** 1-based line number of an offset. */
export function lineOf(source, offset) {
  let line = 1;
  for (let i = 0; i < offset && i < source.length; i += 1) {
    if (source[i] === '\n') line += 1;
  }
  return line;
}

/**
 * Every `migrationBuilder.<Operation>(...)` call in an `Up` body, with the
 * arguments' text, the line, and - for `Sql` - the raw statements.
 *
 * Takes the `findUpBody` result rather than loose strings, because the two forms
 * it carries are not interchangeable: the operation scanner and the brace-matcher
 * must read the masked body, and the argument text and the SQL statements must be
 * read from the original. Passing only one of them is a silent information loss
 * in one direction or the other.
 *
 * The generic type argument is consumed (`AddColumn<Guid>(` is one operation,
 * not a call to `<`), because eleven of this repository's additions are written
 * that way and a regex that stopped at `<` would find nothing.
 */
export function readOperations(upBody, source) {
  const masked = upBody.masked;
  const operations = [];
  const pattern = /migrationBuilder\s*\.\s*(\w+)\s*(?:<[^<>]*>)?\s*\(/g;
  let match;
  while ((match = pattern.exec(masked)) !== null) {
    const op = match[1];
    // Brace-match the argument list on the masked text.
    const open = match.index + match[0].length - 1;
    let depth = 0;
    let close = -1;
    for (let i = open; i < masked.length; i += 1) {
      if (masked[i] === '(') depth += 1;
      else if (masked[i] === ')') {
        depth -= 1;
        if (depth === 0) {
          close = i;
          break;
        }
      }
    }
    const argMasked = close === -1 ? masked.slice(open + 1) : masked.slice(open + 1, close);
    const argText = close === -1 ? upBody.text.slice(open + 1) : upBody.text.slice(open + 1, close);
    operations.push({
      op,
      line: lineOf(source, upBody.start + 1 + match.index),
      args: argText.replace(/\s+/g, ' ').trim().slice(0, 400),
      argMasked,
      statements: op === 'Sql' ? readSqlStatements(argText) : [],
    });
  }
  return operations;
}

/** The `name:` argument of a call, for a finding message that identifies the object. */
function namedArgument(args, key) {
  const match = new RegExp(`\\b${key}\\s*:\\s*"([^"]*)"`).exec(args);
  return match ? match[1] : null;
}

/** Split a `Sql(...)` payload into statements, dropping C# punctuation. */
function readSqlStatements(argText) {
  const withoutDelimiters = argText.replace(/"""/g, '"').replace(/\s+/g, ' ').trim();
  const body = withoutDelimiters.startsWith('"') && withoutDelimiters.endsWith('"')
    ? withoutDelimiters.slice(1, -1)
    : withoutDelimiters;
  return body
    .split(';')
    .map((statement) => statement.trim())
    .filter((statement) => statement.length > 0);
}

/** The table a raw `ALTER TABLE x` statement names, or `null`. */
export function sqlStatementTable(statement) {
  const match = /\bALTER\s+TABLE\s+(?:ONLY\s+)?"?([A-Za-z_][A-Za-z0-9_]*)"?/i.exec(statement);
  return match ? match[1] : null;
}

/** Classifies one raw SQL statement against `SQL_RULES`. */
export function classifySqlStatement(statement) {
  for (const rule of SQL_RULES) {
    if (rule.re.test(statement)) {
      return { id: rule.id, verdict: rule.verdict, label: rule.label };
    }
  }
  return {
    id: 'unclassified',
    verdict: 'unclassified',
    label: 'a statement this gate does not recognise',
  };
}

/**
 * Whether an `AddColumn` is safe for the previous release's writers.
 *
 * `nullable: false` with no default is the classic expand-phase break that no
 * schema diff catches: the column IS additive, so `migration-compat.sh` sees a
 * new column and passes, and the failure lands on the first INSERT from a pod
 * still running the previous release. Pure text, because the properties are
 * literal in the generated source.
 */
export function addColumnIsNullableOrDefaulted(args) {
  const nullable = /nullable\s*:\s*(true|false)/.exec(args);
  const isNullable = nullable !== null && nullable[1] === 'true';
  const hasDefault = /\bdefaultValue\s*:/.test(args) || /\bdefaultValueSql\s*:/.test(args);
  return isNullable || hasDefault;
}

// =============================================================================
// R2 - the migration window
// =============================================================================

/**
 * Audits every migration against the expand/contract rule and the ledger.
 *
 * @param {object} input
 * @param {Record<string,string>} input.sources  migration id -> file text
 * @param {object} input.ledger                  deploy/rollout/contract-ledger.json
 * @param {string} input.today                   YYYY-MM-DD, injected so the test
 *                                              does not depend on the clock
 * @returns {{ok: boolean, findings: object[], ledgerErrors: string[], stats: object}}
 */
export function auditMigrations({ sources, ledger, today }) {
  const findings = [];
  const ledgerErrors = [];
  const errors = validateLedger(ledger, { sources, today, findings });
  ledgerErrors.push(...errors);

  const baseline = ledger && ledger.baseline;
  const approved = new Map(
    (ledger && Array.isArray(ledger.approvedContracts) ? ledger.approvedContracts : []).map((entry) => [
      entry.migration,
      entry,
    ]),
  );

  const ids = Object.keys(sources).sort();
  const stats = { migrations: ids.length, expand: 0, contract: 0, ledgered: 0 };

  for (const id of ids) {
    const source = sources[id];
    const body = findUpBody(source);
    if (body === null) {
      findings.push(
        finding(id, 1, 'Up', 'unreadable', 'no `Up(MigrationBuilder)` method was found, so this migration has no readable forward operation. A gate that cannot read a migration has not checked it.'),
      );
      continue;
    }
    const operations = readOperations(body, source);
    // A migration that reads as EMPTY is almost always a brace-matching failure
    // rather than an empty migration, and an empty migration is green forever.
    if (operations.length === 0) {
      findings.push(
        finding(id, 1, 'Up', 'unreadable', 'the `Up` method contains no `migrationBuilder` call. Either the migration is empty or the method boundary was mis-detected; both need a human.'),
      );
      continue;
    }

    const createdTables = new Set(
      operations.filter((operation) => operation.op === 'CreateTable').map((operation) => namedArgument(operation.args, 'name')).filter(Boolean),
    );
    // The `Up` at or before the baseline has already been applied everywhere.
    // It is history, it is not in the window, and refusing it would be refusing
    // the repository's own past. The baseline is the most recent migration that
    // the ledger declares already deployed, so it is an assertion about reality
    // and the ledger's `baselineReviewedOn` date is what makes it checkable.
    const inWindow = baseline === undefined || id > baseline;

    for (const operation of operations) {
      const subject = namedArgument(operation.args, 'name') ?? namedArgument(operation.args, 'table') ?? operation.op;

      if (operation.op === 'AddColumn') {
        if (!addColumnIsNullableOrDefaulted(operation.args)) {
          findings.push(
            finding(
              id,
              operation.line,
              subject,
              'break',
              `AddColumn '${subject}' is NOT NULL with no default. It is additive, so a schema diff calls this clean, and the first INSERT from a pod still running the previous release fails. Make it nullable, or give it a default.`,
            ),
          );
        } else {
          stats.expand += 1;
        }
        continue;
      }

      if (operation.op === 'Sql') {
        for (const statement of operation.statements) {
          const classified = classifySqlStatement(statement);
          if (classified.verdict === 'rls') {
            // Additive ONLY on a table this same `Up` created. On a
            // pre-existing table it refuses every write from old code that does
            // not set the tenant GUC, which is a break wearing an additive
            // operation's name - and it is a break that only shows up in
            // production, where the old code is the code still running.
            //
            // Fail-closed on an unidentifiable table: if the statement does not
            // name one, or names one this migration did not create, the gate
            // cannot tell a new table from an old one and says so.
            const table = sqlStatementTable(statement);
            if (table === null || !createdTables.has(table)) {
              findings.push(
                finding(
                  id,
                  operation.line,
                  table ?? 'Sql',
                  'break',
                  `row-level security is enabled on '${table ?? 'a table this gate could not identify'}', which this migration does not create. Old code that does not set \`app.tenant_id\` then reads zero rows, which is silent. Enable RLS on a table created in the same Up, or record this in the contract ledger.`,
                ),
              );
            } else {
              stats.expand += 1;
            }
            continue;
          }
          if (classified.verdict === 'unclassified') {
            findings.push(
              finding(
                id,
                operation.line,
                'Sql',
                'unclassified',
                `raw SQL this gate cannot classify: ${statement.slice(0, 120)}. An operation nobody can read is an operation nobody has checked; put it in the contract ledger with its approval, or write it as a typed migrationBuilder operation.`,
              ),
            );
            continue;
          }
          if (classified.verdict === 'contract' && inWindow) {
            stats.contract += 1;
            const entry = approved.get(id);
            if (entry === undefined) {
              findings.push(
                finding(
                  id,
                  operation.line,
                  'Sql',
                  'contract',
                  `${classified.label} (${statement.slice(0, 120)}). A migration in the compatibility window may only expand. Split this into two releases: stop writing the old shape in release N, drop it in release N+1, and record the N+1 migration in deploy/rollout/contract-ledger.json.`,
                ),
              );
            } else {
              stats.ledgered += 1;
            }
            continue;
          }
          stats.expand += 1;
        }
        continue;
      }

      if (CONTRACT_OPERATIONS.includes(operation.op)) {
        if (!inWindow) {
          // Before the baseline. Not exempt from the record - it is the reason
          // the baseline is where it is - but not a finding.
          stats.contract += 1;
          continue;
        }
        stats.contract += 1;
        if (approved.has(id)) {
          stats.ledgered += 1;
          continue;
        }
        // An in-place alteration gets its own sentence, because the fix for it
        // is not the same two-release split: the expand step is a NEW column,
        // the contract step is dropping the old one, and both application
        // changes (read the new, write both) have to ship in the middle release.
        const extra = operation.op === 'AlterColumn'
          ? ' An in-place type change is never the expand step either: add the new column as nullable in this release, read the new and write both, and drop the old column in a later release.'
          : '';
        findings.push(
          finding(
            id,
            operation.line,
            subject,
            'contract',
            `${operation.op} on '${subject}' is a contract operation and '${id}' is inside the compatibility window (baseline ${baseline}). A release that both drops and stops writing in one change cannot be rolled back code-only. Split it: release N stops writing, release N+1 drops.${extra}`,
          ),
        );
        continue;
      }

      if (EXPAND_OPERATIONS.includes(operation.op)) {
        stats.expand += 1;
        continue;
      }

      findings.push(
        finding(
          id,
          operation.line,
          operation.op,
          'unclassified',
          `'${operation.op}' is in neither the expand nor the contract operation set, so this gate cannot say what it does to the schema. An unclassified EF operation fails closed; add it to one of the two sets in tools/rollout-window.mjs with the reasoning.`,
        ),
      );
    }
  }

  return { ok: findings.length === 0 && ledgerErrors.length === 0, findings, ledgerErrors, stats };
}

/**
 * The ledger's own invariants. These are checked separately from the migration
 * findings because a ledger that is malformed makes the FINDINGS meaningless:
 * a ledger that silently parsed as "no approved contracts" would turn every
 * approved contract into a violation, and a ledger that parsed as "approve
 * everything" would turn the gate off.
 */
export function validateLedger(ledger, { sources = {}, today = '1970-01-01', findings = [] } = {}) {
  const errors = [];
  if (ledger === null || typeof ledger !== 'object' || Array.isArray(ledger)) {
    return ['the contract ledger is not an object. deploy/rollout/contract-ledger.json is the allowlist a contract phase is permitted by; without it the gate has no rule to apply.'];
  }
  const ids = Object.keys(sources).sort();

  if (typeof ledger.baseline !== 'string' || ledger.baseline.length === 0) {
    errors.push('the ledger has no `baseline`: the last migration already deployed everywhere. Without it every migration is inside the window, including the repository history.');
  } else if (!ids.includes(ledger.baseline)) {
    errors.push(`the ledger's \`baseline\` is '${ledger.baseline}', which is not a migration in this repository. A baseline that names nothing is a window boundary that is nowhere.`);
  }
  if (typeof ledger.baselineReviewedOn !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(ledger.baselineReviewedOn)) {
    errors.push('the ledger has no `baselineReviewedOn` (YYYY-MM-DD). The baseline is an assertion about what is already deployed, and an assertion with no date is never re-examined.');
  }
  if (ledger.windowReleases !== 1) {
    errors.push('`windowReleases` must be exactly 1. One release is the frozen rule in docs/operations/migration-compat.md; a wider window is a policy change and belongs in that document, not in a data file.');
  }

  const approved = ledger.approvedContracts;
  if (approved !== undefined && !Array.isArray(approved)) {
    errors.push('`approvedContracts` must be an array (it is allowed to be empty; it is not allowed to be absent-and-unknown).');
    return errors;
  }
  for (const entry of approved ?? []) {
    if (typeof entry.migration !== 'string' || !ids.includes(entry.migration)) {
      errors.push(`an approved contract names migration '${entry?.migration}', which is not a migration in this repository. A ledger entry for a migration that does not exist is an approval of nothing.`);
    }
    for (const field of ['approvedBy', 'approvedOn', 'reason', 'followUpTask']) {
      if (typeof entry[field] !== 'string' || entry[field].trim().length === 0) {
        errors.push(`the approved contract for '${entry.migration}' has no '${field}'. A contract phase without a named approver, a date, a reason and a follow-up task is a deletion with extra steps.`);
      }
    }
    if (typeof entry.approvedOn === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(entry.approvedOn) && entry.approvedOn > today) {
      errors.push(`the approved contract for '${entry.migration}' is dated ${entry.approvedOn}, which is in the future.`);
    }
    if (typeof entry.supersededBy === 'string' && ids.includes(entry.supersededBy) && entry.supersededBy <= entry.migration) {
      findings.push(
        finding(entry.migration, 0, 'ledger', 'stale', `the contract entry is superseded by '${entry.supersededBy}', which does not sort after it. Remove the entry once the follow-up migration has shipped.`),
      );
    }
  }
  // An approved contract whose follow-up migration has NOT yet shipped is
  // exactly the edge case the task names: a flag - here, an approval - left on
  // forever. There is no expiry, so the only mechanism is a deadline.
  for (const entry of approved ?? []) {
    if (typeof entry.reviewBy !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(entry.reviewBy)) {
      errors.push(`the approved contract for '${entry.migration}' has no 'reviewBy' (YYYY-MM-DD). A recorded contract with no review date is a deletion nobody will ever revisit.`);
    } else if (entry.reviewBy < today) {
      findings.push(
        finding(entry.migration, 0, 'ledger', 'stale', `the approved contract expired on ${entry.reviewBy}. Ship the follow-up migration (${entry.followUpTask}) or withdraw the approval; an expired approval is an untracked contract.`),
      );
    }
  }
  return errors;
}

// =============================================================================
// R3 - the compatibility matrix
// =============================================================================

/** The three cells. A matrix missing any of them is not a matrix. */
export const REQUIRED_MATRIX_CELLS = Object.freeze(['old-api-new-db', 'new-api-old-db', 'new-api-new-db']);

/**
 * What each cell PROVES, and why it is that cell and not another.
 *
 * `old-api-new-db` is the rollback cell and the one that is easy to forget:
 * it is the only one that exercises the previous release's code, and after the
 * previous release is decommissioned it is the one that can no longer be run
 * at all. `new-api-old-db` is the cell that catches a new NOT NULL column
 * before it is added, and it is only meaningful while there IS an old schema to
 * point at - which is another reason it cannot be reconstructed later.
 */
export const MATRIX_CELL_PURPOSE = Object.freeze({
  'old-api-new-db': 'the previous release runs against the expanded schema: the rollback the window exists to permit',
  'new-api-old-db': 'the candidate runs against the schema it is not shipped with: a new column must be nullable or defaulted',
  'new-api-new-db': 'the candidate runs against the current schema: the ordinary case, and the only one that proves the candidate works at all',
});

/**
 * Validates the matrix definition and the runs recorded against it.
 *
 * @param {object} input
 * @param {object} input.matrix  deploy/rollout/compat-matrix.json
 * @param {object} input.openapi the committed OpenAPI document, parsed
 * @param {string} input.today
 * @param {number} input.maxAgeDays how stale a recorded run may be
 */
export function auditCompatMatrix({ matrix, openapi, today, maxAgeDays }) {
  const findings = [];
  const exemptions = [];
  if (matrix === null || typeof matrix !== 'object' || Array.isArray(matrix)) {
    return { ok: false, findings: [{ subject: 'compat-matrix.json', verdict: 'missing', reason: 'the compatibility matrix record is not an object' }], exemptions };
  }
  const cells = Array.isArray(matrix.matrix) ? matrix.matrix : [];
  const byId = new Map(cells.map((cell) => [cell.id, cell]));
  for (const id of REQUIRED_MATRIX_CELLS) {
    if (!byId.has(id)) {
      findings.push({
        subject: id,
        verdict: 'missing-cell',
        reason: `the matrix has no '${id}' cell. ${MATRIX_CELL_PURPOSE[id] ?? ''}`.trim(),
      });
    }
  }

  // A cell declares the statuses that constitute a pass, and the gate reads them
  // from HERE rather than from the run. A run that could declare its own
  // expectations would pass whatever it did, and a cell that quietly acquired a
  // new expectation would be a data change nobody reviewed as one.
  for (const cell of cells) {
    const expect = cell?.expect;
    if (expect === null || typeof expect !== 'object' || Array.isArray(expect)) {
      findings.push({ subject: cell?.id ?? '(unnamed)', verdict: 'no-expectations', reason: 'the cell declares no `expect` block, so there is nothing for the gate to compare a run against.' });
      continue;
    }
    for (const key of ['migrationCurrency', 'write', 'workspace']) {
      const value = expect[key];
      const wellShaped = key === 'migrationCurrency' ? typeof value === 'string' && value.length > 0 : Number.isInteger(value);
      if (!wellShaped) {
        findings.push({ subject: cell.id, verdict: 'no-expectations', reason: `the cell's \`expect.${key}\` is not the right shape: a check status for migrationCurrency, an integer status code otherwise.` });
      }
    }
    if (expect.migrationCurrency !== 'Healthy' && typeof cell.readinessNote !== 'string') {
      findings.push({
        subject: cell.id,
        verdict: 'unexpected-verdict-unexplained',
        reason: `the cell expects the migration-currency check to be '${expect.migrationCurrency}', and gives no \`readinessNote\` explaining why. An expectation that is not 'Healthy' is either a mistake or the most interesting thing in the matrix, and it has to say which.`,
      });
    }
  }

  // Every probe must be a route this build actually serves. A matrix whose
  // probe path has been renamed is a matrix that reports a 404 as a pass if
  // nobody checks the status code, and a 404 in a compat probe is exactly the
  // shape a false pass takes.
  const paths = new Set(Object.keys((openapi && openapi.paths) || {}));
  const base = serverBasePath(openapi);
  for (const cell of cells) {
    for (const probe of cell.probes ?? []) {
      const route = probeRoute(probe);
      if (route === null) continue; // a health path, checked against the API's own routes
      if (paths.size === 0) {
        findings.push({ subject: cell.id, verdict: 'no-openapi', reason: 'the OpenAPI document has no paths, so no matrix probe could be checked against a real route' });
        continue;
      }
      const key = stripBase(route, base);
      if (!paths.has(key)) {
        findings.push({
          subject: `${cell.id}:${probe}`,
          verdict: 'probe-not-a-route',
          reason: `'${key}' is not in the committed OpenAPI document. A compat probe against a route that does not exist returns 404, and a 404 read as "no rows" is the false pass this matrix exists to prevent.`,
        });
      }
    }
  }

  // --- declared exemptions ------------------------------------------------------
  // A cell that CANNOT be run, named and dated. Five fields are required and the
  // gate fails without each of them, because each missing one is a specific
  // failure: no `cell` is a hole with no address, no `reason` is a hole nobody
  // can check, no `unblockedWhen` is permanent, no `riskAccepted` is a
  // consequence nobody agreed to, and no `reviewBy` is a hole that never stops.
  //
  // This is NOT a suppression. A suppression hides a failure; a declaration says
  // "this specific check cannot be performed, here is why in words, and I stop
  // being accepted on this date". The exemptions are RETURNED as well as
  // honoured, so the CLI can print them - an exemption that is invisible in a
  // green run is a suppression with extra steps.
  const declared = Array.isArray(matrix.unavailable) ? matrix.unavailable : [];
  const exempt = new Set();
  for (const entry of declared) {
    const cell = entry?.cell;
    for (const field of ['reason', 'unblockedWhen', 'riskAccepted', 'reviewBy']) {
      if (typeof entry?.[field] !== 'string' || entry[field].trim().length === 0) {
        findings.push({ subject: cell ?? '(unnamed exemption)', verdict: 'exemption-incomplete', reason: `the exemption has no '${field}'. Every one of the five fields is required: an exemption without the reason cannot be checked, and one without a review date never stops applying.` });
      }
    }
    if (typeof cell === 'string' && !byId.has(cell)) {
      findings.push({ subject: cell, verdict: 'exemption-for-unknown-cell', reason: 'the exemption names a cell that is not in the matrix. An exemption for a cell that does not exist exempts nothing and looks like coverage.' });
      continue;
    }
    if (typeof entry?.reviewBy === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(entry.reviewBy) && entry.reviewBy < today) {
      findings.push({ subject: cell, verdict: 'exemption-expired', reason: `the exemption for '${cell}' expired on ${entry.reviewBy}. Either run the cell or re-justify it with a new date; an expired declaration is an untracked hole.` });
      continue;
    }
    if (typeof cell === 'string') {
      exempt.add(cell);
      exemptions.push({ cell, reason: entry.reason, unblockedWhen: entry.unblockedWhen, reviewBy: entry.reviewBy });
    }
  }

  // --- the runs ----------------------------------------------------------------
  const runs = Array.isArray(matrix.runs) ? matrix.runs : [];
  if (runs.length === 0) {
    const unrun = REQUIRED_MATRIX_CELLS.filter((id) => !exempt.has(id));
    if (unrun.length === 0) {
      // Every cell exempt: that is a matrix that measures nothing, and it is a
      // finding however well each exemption is written.
      findings.push({
        subject: 'runs',
        verdict: 'never-run',
        reason: 'every cell is exempt, so this matrix measures nothing. A record of why three checks cannot be run is not a record of a compatibility window.',
      });
    } else {
      findings.push({
        subject: 'runs',
        verdict: 'never-run',
        reason: `no compatibility run is recorded, so ${unrun.join(', ')} are unmeasured. R3 is a smoke result, not a definition: an unrun matrix proves nothing and reads exactly like a passing one.`,
      });
    }
  } else {
    const latest = [...runs].sort((a, b) => String(b.date).localeCompare(String(a.date)))[0];
    for (const id of REQUIRED_MATRIX_CELLS) {
      if (exempt.has(id)) continue; // declared; its absence from the run is the point
      const result = (latest.results ?? []).find((entry) => entry.id === id);
      if (result === undefined) {
        findings.push({ subject: id, verdict: 'cell-not-run', reason: `the latest run (${latest.date}) did not exercise '${id}'.` });
        continue;
      }
      if (result.result !== 'PASS') {
        findings.push({ subject: id, verdict: 'cell-failed', reason: `the latest run (${latest.date}) reports '${result.result}' for '${id}': ${result.detail ?? 'no detail recorded'}.` });
        continue;
      }
      // The recorded statuses are compared against the DECLARED expectations.
      const expect = byId.get(id)?.expect ?? {};
      for (const [key, recorded] of [['migrationCurrency', result.migrationCurrency], ['write', result.writeStatus], ['workspace', result.workspaceStatus]]) {
        if (expect[key] === undefined) continue;
        if (recorded !== expect[key]) {
          findings.push({
            subject: id,
            verdict: 'status-mismatch',
            reason: `the run recorded ${key}=${recorded} but the cell declares ${key}=${expect[key]}. A cell marked PASS while its recorded statuses do not match its own declaration is the false pass this matrix exists to prevent.`,
          });
        }
      }
    }
    if (typeof latest.date === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(latest.date)) {
      const age = daysBetween(latest.date, today);
      if (age > maxAgeDays) {
        findings.push({
          subject: 'runs',
          verdict: 'stale',
          reason: `the newest recorded run is ${age} days old (${latest.date}); the limit is ${maxAgeDays}. The old-API image and the old schema are only available while a previous release exists, so a stale matrix is not refreshable later - it is lost.`,
        });
      }
    } else {
      findings.push({ subject: 'runs', verdict: 'undated', reason: `the latest run has no YYYY-MM-DD date ('${latest.date}').` });
    }
    for (const entry of latest.results ?? []) {
      if (entry.result === 'PASS' && (entry.detail === undefined || String(entry.detail).trim().length === 0)) {
        findings.push({ subject: entry.id, verdict: 'undetailed', reason: `a PASS for '${entry.id}' with no detail. "It passed" is not a record: which image, which schema head, which status codes.` });
      }
    }
  }

  return { ok: findings.length === 0, findings, exemptions };
}

/**
 * The route a probe asserts, or `null` for a health probe.
 *
 * The path is returned verbatim - placeholders included - because the OpenAPI
 * document's `paths` keys use the SAME `{name}` syntax, so an exact comparison
 * is both possible and the strictest one available. Normalising `{projectId}`
 * to a placeholder on both sides would also work and would accept a probe whose
 * parameter had been RENAMED, which is precisely the rename a matrix has to
 * notice.
 */
export function probeRoute(probe) {
  if (typeof probe !== 'string') return null;
  const [method, template] = probe.trim().split(/\s+/, 2);
  if (method === undefined || template === undefined) return null;
  // The hosting endpoints are not in the OpenAPI document, by design: they are
  // registered by `MapHostingEndpoints`, they are `AllowAnonymous` because a
  // probe or a CDN cannot hold a token, and a document whose paths a load
  // balancer can call without credentials is a document that advertises them.
  // They are therefore exempt from the route check rather than special-cased
  // into it - there is nothing in the document to compare them against.
  if (template === '/version' || template.startsWith('/health')) return null;
  return template;
}

/**
 * The API's base path as the OpenAPI document declares it, or `''`.
 *
 * This repository's document puts the version prefix in `servers[0].url` and the
 * route in `paths`, so the document says `/projects/{id}/workspace` where an
 * operator (and a probe) says `/api/v1/projects/{id}/workspace`. Comparing the
 * two without accounting for it produces a finding on a route that exists, and
 * a gate that cries wolf on the first run gets switched off before the second.
 */export function serverBasePath(openapi) {
  const servers = (openapi && openapi.servers) || [];
  const url = typeof servers[0]?.url === 'string' ? servers[0].url : '';
  if (!url.startsWith('/')) return '';
  return url.replace(/\/+$/, '');
}

/** Removes the document's base path from a full request path, when present. */
export function stripBase(route, base) {
  if (base.length > 0 && route.startsWith(`${base}/`)) return route.slice(base.length);
  return route;
}

// =============================================================================
// R4 - the flag register
// =============================================================================

/**
 * What a flag is allowed to gate. A CLOSED set, so a new value is a review, not
 * a silent widening.
 *
 * `presentation` hides or shows a surface. `behaviour` changes what the
 * application does for a caller who is already authorized. `rollout` limits
 * which environment a build is used in. `inert` is the documented "off"
 * sentinel for a value that is really a URL.
 *
 * What is deliberately NOT in the set: anything naming access. R4 is the
 * requirement that a flag is config-driven rollout only and never
 * authorization, and a vocabulary that has no word for "permission" is how that
 * requirement is enforced rather than restated.
 */
export const FLAG_GATES = Object.freeze(['presentation', 'behaviour', 'rollout', 'inert']);

/** The files a flag may be declared in. A register entry may not invent one. */
export const FLAG_DECLARATION_FILES = Object.freeze([
  'deploy/k8s/frontend/configmap.yaml',
  'deploy/k8s/configmap.yaml',
  'frontend/.env.example',
  'src/DubbingPlatform.Application/Options/FeatureOptions.cs',
]);

/**
 * Source files that constitute the authorization decision. A flag referenced
 * from one of these is a flag that has become an access control, whatever the
 * register says it gates.
 */
export const AUTHORIZATION_SOURCE_GLOBS = Object.freeze([
  'src/DubbingPlatform.Api/Auth/',
  'src/DubbingPlatform.Api/Security/',
  'src/DubbingPlatform.Api/Policies/',
  'src/DubbingPlatform.Application/Authorization/',
  'src/DubbingPlatform.Infrastructure/Identity/',
]);

/**
 * Validates the flag register: every flag has a real declaration whose value
 * matches the recorded default, a review date, and a gate that is not access.
 *
 * @param {object} input
 * @param {object} input.register      deploy/rollout/flags.json
 * @param {Record<string,string>} input.files  declaredIn path -> text
 * @param {string} input.today
 * @param {Array<{path: string, text: string}>} input.authorizationSources
 */
export function auditFlagRegister({ register, files, today, authorizationSources = [] }) {
  const findings = [];
  if (register === null || typeof register !== 'object' || Array.isArray(register)) {
    return { ok: false, findings: [{ key: '(register)', verdict: 'missing', reason: 'deploy/rollout/flags.json is not an object' }] };
  }
  const flags = Array.isArray(register.flags) ? register.flags : [];
  if (flags.length === 0) {
    findings.push({ key: '(register)', verdict: 'empty', reason: 'the register has no flags. An empty register is indistinguishable from a register nobody has filled in.' });
  }

  const seen = new Set();
  for (const flag of flags) {
    const key = flag?.key;
    if (typeof key !== 'string' || key.length === 0) {
      findings.push({ key: '(unnamed)', verdict: 'nameless', reason: 'a register entry has no `key`' });
      continue;
    }
    if (seen.has(key)) {
      findings.push({ key, verdict: 'duplicate', reason: `'${key}' appears twice in the register. Two defaults for one flag is one of them wrong, and which is unknowable from the register.` });
    }
    seen.add(key);

    if (!FLAG_GATES.includes(flag.gates)) {
      findings.push({ key, verdict: 'bad-gate', reason: `'${flag.gates}' is not one of ${FLAG_GATES.join('/')}. A flag that gates anything else - access, permission, authorization - is not a rollout flag, and this vocabulary has no word for it on purpose.` });
    }
    if (flag.authorization !== false) {
      findings.push({ key, verdict: 'authorizes', reason: `the register does not assert 'authorization: false' for '${key}'. A rollout flag is configuration; if it is also the access control then turning it off is an outage and turning it on is a breach.` });
    }
    if (typeof flag.reviewBy !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(flag.reviewBy)) {
      findings.push({ key, verdict: 'no-review-date', reason: `'${key}' has no 'reviewBy' (YYYY-MM-DD). A flag with no expiry review is a flag that is on forever, which is the task's own edge case.` });
    } else if (flag.reviewBy < today) {
      findings.push({ key, verdict: 'review-overdue', reason: `'${key}' was due for expiry review on ${flag.reviewBy}. Delete it, or record why it is still the right shape.` });
    }
    if (typeof flag.default === 'undefined') {
      findings.push({ key, verdict: 'no-default', reason: `'${key}' has no recorded default state. "What is it when nobody has decided" is the only question that matters at 03:00.` });
    }
    if (!FLAG_DECLARATION_FILES.includes(flag.declaredIn)) {
      findings.push({ key, verdict: 'undeclared-file', reason: `'${key}' claims to be declared in '${flag.declaredIn}', which is not a configuration file this gate knows how to read. Allowed: ${FLAG_DECLARATION_FILES.join(', ')}.` });
    } else {
      // `declaredAs` is the token to look for, when it is not the key itself. A
      // backend flag is `Features__VideoIntelligenceEnabled` in the environment
      // and `VideoIntelligenceEnabled` in the C# options class, and looking for
      // the env-var spelling in a .cs file finds nothing - which is how a
      // register entry ends up looking verified because the absence of a token
      // was read as an absence of a problem.
      const token = typeof flag.declaredAs === 'string' && flag.declaredAs.length > 0 ? flag.declaredAs : key;
      const text = files[flag.declaredIn];
      if (text === undefined) {
        findings.push({ key, verdict: 'unreadable-declaration', reason: `'${flag.declaredIn}' was not supplied, so '${key}' could not be checked against its declaration` });
      } else if (!text.includes(token)) {
        findings.push({ key, verdict: 'not-declared', reason: `'${token}' is in the register but not in ${flag.declaredIn}. A flag that exists only in the register is documentation, and documentation does not gate a surface.` });
      } else {
        const declared = readDeclaredValue(text, token);
        if (declared === null) {
          findings.push({ key, verdict: 'unreadable-declaration', reason: `'${token}' appears in ${flag.declaredIn} but the gate could not read its declared value` });
        } else if (normaliseFlagValue(declared) !== normaliseFlagValue(flag.default)) {
          findings.push({ key, verdict: 'default-mismatch', reason: `'${key}' is recorded as defaulting to '${flag.default}' but ${flag.declaredIn} declares '${token}' as '${declared}'. One of the two is what a deploy will actually use.` });
        }
      }
    }
  }

  // R4's second half, checked in the source rather than asserted in the
  // register: a flag that has become an access control shows up as a reference
  // from the authorization code, and a register cannot see that.
  for (const source of authorizationSources) {
    if (!/\bFeatureOptions\b|Features__|VITE_ENABLE_/.test(source.text)) {
      continue;
    }
    findings.push({
      key: source.path,
      verdict: 'flag-in-authorization',
      reason: `${source.path} is part of the authorization decision and references a rollout flag. A flag is configuration; access control has to keep working when the flag service is unreachable, when a tenant is not in the rollout, and when somebody turns it off during an incident.`,
    });
  }

  return { ok: findings.length === 0, findings };
}

/** The declared value of a key in a YAML ConfigMap, a `.env` file, or a C# default. */
export function readDeclaredValue(text, key) {
  const env = new RegExp(`^\\s*${key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}=(.*)$`, 'm').exec(text);
  if (env !== null) return env[1].trim().replace(/^["']|["']$/g, '');
  const yaml = new RegExp(`^\\s{2}${key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}:\\s*(.*)$`, 'm').exec(text);
  if (yaml !== null) return yaml[1].trim().replace(/^["']|["']$/g, '');
  // C# property default: `public bool X { get; set; } = false;`
  const cs = new RegExp(`${key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}[^\\n=]*\\{[^\\n}]*\\}\\s*=\\s*([^;]+);`).exec(text);
  if (cs !== null) return cs[1].trim();
  return null;
}

/** `true`/`false` in any spelling, so a quoted YAML value and a bare one agree. */
export function normaliseFlagValue(value) {
  const text = String(value).trim().toLowerCase();
  if (text === 'true' || text === 'false') return text;
  return text;
}

// =============================================================================
// R5 - the rollback rehearsal record
// =============================================================================

/** The workloads `kubectl rollout undo` is the documented path for. */
export const REQUIRED_ROLLBACK_TARGETS = Object.freeze([
  'dubbing-api',
  'worker-control',
  'worker-media-prep',
  'worker-ai',
  'frontend',
]);

/**
 * Validates the recorded rehearsal. R5 is "rehearsed and recorded, not just
 * documented", so the checks are about EVIDENCE, not about prose: every target
 * undone, a date, a result, the pod template that changed - and, for the API,
 * what the pods actually answered.
 */
export function auditRehearsalRecord({ record, today, maxAgeDays }) {
  const findings = [];
  if (record === null || typeof record !== 'object' || Array.isArray(record)) {
    return { ok: false, findings: [{ subject: 'rollback-rehearsal.json', verdict: 'missing', reason: 'the rollback rehearsal record is missing' }] };
  }
  if (typeof record.date !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(record.date)) {
    findings.push({ subject: 'date', verdict: 'undated', reason: 'the rehearsal record has no YYYY-MM-DD date' });
  } else {
    const age = daysBetween(record.date, today);
    if (age > maxAgeDays) {
      findings.push({ subject: 'date', verdict: 'stale', reason: `the last rehearsal was ${age} days ago (${record.date}); the limit is ${maxAgeDays}. A rollback path nobody has exercised in ${maxAgeDays} days is a path nobody has exercised.` });
    }
  }
  if (record.result !== 'PASS') {
    findings.push({ subject: 'result', verdict: 'not-passed', reason: `the recorded rehearsal result is '${record.result}'.` });
  }
  if (typeof record.environment !== 'string' || record.environment.trim().length === 0) {
    findings.push({ subject: 'environment', verdict: 'no-environment', reason: 'the record does not say where it ran. A rehearsal result without an environment is a claim.' });
  }

  const targets = new Map((Array.isArray(record.targets) ? record.targets : []).map((entry) => [entry.deployment, entry]));
  for (const name of REQUIRED_ROLLBACK_TARGETS) {
    const entry = targets.get(name);
    if (entry === undefined) {
      findings.push({ subject: name, verdict: 'not-rehearsed', reason: `'${name}' was not in the rehearsal. The rollback procedure covers the frontend and every worker, not only the API.` });
      continue;
    }
    if (entry.result !== 'PASS') {
      findings.push({ subject: name, verdict: 'target-failed', reason: `the undo of '${name}' did not pass: ${entry.detail ?? 'no detail recorded'}` });
    }
    // The revision annotation INCREMENTS on an undo. `rollout undo` does not
    // move the Deployment back to revision N-1; it records the previous
    // ReplicaSet's pod template as a NEW revision N+1, because a rollback is a
    // change like any other and the history has to be able to show it happened.
    //
    // Reading `revisionAfter < revisionBefore` as the success condition is the
    // natural mistake and it rejects every correct rehearsal - the one this
    // module's own first version made, caught by running the rehearsal and
    // reading its record rather than by reading the code.
    if (typeof entry.revisionBefore === 'number' && typeof entry.revisionAfter === 'number') {
      if (entry.revisionAfter === entry.revisionBefore) {
        findings.push({ subject: name, verdict: 'no-revision-change', reason: `rolling back '${name}' left the revision annotation at ${entry.revisionBefore}. The annotation INCREMENTS on an undo, so an unchanged number means nothing happened - most often a garbage-collected ReplicaSet, so there was nothing to roll back to.` });
      }
      // A revision is a positive integer the controller wrote. Anything else is a
      // read that failed and was recorded anyway, and `0 !== 10` satisfies the
      // check above - which is how a real run wrote "revision 10 -> 0" into this
      // record's own predecessor and the rehearsal reported `ok`. The record is
      // evidence, so a number no controller can produce is not evidence.
      if (!Number.isInteger(entry.revisionBefore) || !Number.isInteger(entry.revisionAfter)
          || entry.revisionBefore < 1 || entry.revisionAfter < 1) {
        findings.push({ subject: name, verdict: 'impossible-revision', reason: `'${name}' recorded revisionBefore=${entry.revisionBefore} revisionAfter=${entry.revisionAfter}. A Deployment revision is a positive integer written by the controller, so this is a failed read recorded as a measurement rather than a revision.` });
      }
    } else {
      findings.push({ subject: name, verdict: 'no-revisions', reason: `the rehearsal of '${name}' recorded no revisionBefore/revisionAfter, so there is no evidence the undo was recorded at all.` });
    }

    // The revision moving is the API accepting a command. The IMAGE in the pod
    // template changing is the thing a user would notice, and the two are
    // different claims: a Deployment can record a new revision whose template is
    // identical to the one it had.
    if (typeof entry.imageBefore === 'string' && typeof entry.imageAfter === 'string') {
      if (entry.imageBefore === entry.imageAfter) {
        findings.push({ subject: name, verdict: 'no-image-change', reason: `the undo of '${name}' recorded a new revision whose pod template still names '${entry.imageAfter}'. A revision that changes nothing is a rollback to the same build.` });
      }
    } else {
      findings.push({ subject: name, verdict: 'no-images', reason: `the rehearsal of '${name}' recorded no imageBefore/imageAfter, so there is no evidence the pod template changed.` });
    }
  }

  // The one target that can be checked end to end: the API serves its own
  // identity, so a rollback can be confirmed against what the pods ANSWER rather
  // than against what the cluster BELIEVES. The rehearsal's own header says the
  // same thing - a tag that was re-pushed makes the two differ - and this is the
  // only assertion in the file that notices.
  const api = targets.get('dubbing-api');
  if (api !== undefined) {
    if (typeof api.servedAfter !== 'string' || api.servedAfter.length === 0) {
      findings.push({ subject: 'dubbing-api', verdict: 'no-served-version', reason: "the rehearsal did not record what the API pods SERVED after the undo. '/version' is the check the runbook relies on, because 'rollout undo' moving the Deployment does not prove the pods are the previous build." });
    } else if (api.servedBefore !== undefined && api.servedBefore === api.servedAfter) {
      findings.push({ subject: 'dubbing-api', verdict: 'served-unchanged', reason: `after the undo the API pods still serve '${api.servedAfter}'. The Deployment pointer moved and the running build did not, which is what a re-pushed tag looks like.` });
    }
  }

  if (!Array.isArray(record.notCovered) || record.notCovered.length === 0) {
    findings.push({
      subject: 'notCovered',
      verdict: 'unqualified',
      reason: 'the record does not say what the rehearsal did NOT cover. An unqualified pass is read as a complete one, and the difference between "rollout undo works in a kind cluster" and "a production rollback is safe" is everything.',
    });
  }
  return { ok: findings.length === 0, findings };
}

/** Whole days from `from` to `to`, both YYYY-MM-DD. Negative when `to` precedes. */
export function daysBetween(from, to) {
  const a = Date.parse(`${from}T00:00:00Z`);
  const b = Date.parse(`${to}T00:00:00Z`);
  if (Number.isNaN(a) || Number.isNaN(b)) return Number.NaN;
  return Math.round((b - a) / 86_400_000);
}

// =============================================================================
// CONVENIENCE - the I/O the CLI and the tests share
// =============================================================================

/** Migration id (`20260922082522_AddRefreshSessions`) -> file text, for a directory. */
export function readMigrationSources(directory) {
  const sources = {};
  for (const name of readdirSync(directory)) {
    if (!name.endsWith('.cs')) continue;
    // `.Designer.cs` is the compiled model snapshot each migration carries and
    // `*ModelSnapshot.cs` is the current model. Neither is a migration, and
    // `findUpBody` returns null for them, so including them would report every
    // snapshot as an unreadable migration. This is the same exclusion
    // `scripts/migration-compat.sh` documents, for the same reason.
    if (name.endsWith('.Designer.cs') || name.endsWith('ModelSnapshot.cs')) continue;
    sources[name.replace(/\.cs$/, '')] = readFileSync(join(directory, name), 'utf8');
  }
  return sources;
}
