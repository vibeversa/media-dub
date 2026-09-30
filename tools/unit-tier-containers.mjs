// The basic-CI unit tier's container rule (Task 042A). Pure decision layer.
//
// WHAT THIS DECIDES
// -----------------
// `.github/workflows/basic-ci.yml` runs the unit tier with no Docker and no
// Testcontainers, on purpose: the early gate has to be fast enough to be worth
// waiting for, and a container-backed test in it would either be skipped or
// would start a container nobody asked for.
//
// The dangerous version of that is SILENT. A `[SkippableFact]` whose container
// cannot start reports `Skipped`, `dotnet test` exits 0, and the job is green
// having proved nothing. So the rule is fail-closed in both directions:
//
//   * a file with a container dependency must carry `[Trait("Category",
//     "Integration")]`, which is what excludes it from this tier -> otherwise
//     `TESTCONTAINERS_REQUIRED_BUT_UNAVAILABLE`;
//   * a unit project that declares no tests at all is not "nothing to fail",
//     it is a filter or a rename that stopped matching -> `UNIT_TIER_EMPTY`.
//
// WHY IT IS A MODULE AND NOT A SCRIPT
// -----------------------------------
// This is the decision that can quietly stop being enforced, so it is the part
// that gets unit tests. The CLI at the bottom of this file is the only I/O, and
// it is a Node CLI rather than a `scripts/*.sh` driver - see the note above it.
//
// SCOPE, STATED PRECISELY
// -----------------------
// A container dependency is a *code-shaped* marker - a `using Testcontainers`,
// a `*Container` builder, `IContainer<T>`, or the repository's container
// fixture base. The English word "container" is NOT one: this repository's
// media tests talk about "container normalization" and `AllowedContainers` in
// ordinary prose and in real identifiers, and a rule that matched the word
// would fail the build on correct code. Comments are stripped before matching
// for the same reason - a doc comment that says "no container, no network" is
// the *opposite* of a container dependency.
//
// MAY-SKIP IS REPORTED, NOT ENFORCED HERE
// ---------------------------------------
// `Skip.If(...)` is not a container marker (this repository also probes for
// ffmpeg that way). It is reported as an advisory so a test whose result
// depends on a host tool is visible, and the actual enforcement of "a skip is
// not a pass" is the runtime `--forbid-skipped` assertion over the TRX.
import { readdirSync, readFileSync } from 'node:fs';
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

/** Machine-readable reasons, shared with docs/ci-branch-protection.md §2. */
export const REASON_OK = 'OK';
export const REASON_UNAVAILABLE = 'TESTCONTAINERS_REQUIRED_BUT_UNAVAILABLE';
export const REASON_EMPTY = 'UNIT_TIER_EMPTY';
export const REASON_UNREADABLE = 'UNIT_TIER_UNREADABLE';

/** The xunit category that means "needs a container", so it is not this tier. */
export const INTEGRATION_CATEGORY = 'Integration';

/**
 * Code-shaped container markers. Every one is a token that only appears in code
 * that reaches for a container runtime; none of them is a word that also
 * appears in this repository's prose.
 */
export const CONTAINER_MARKERS = [
  // `[ \t]*`, never `\s*`: `\s` matches newlines, so `\s*` in a `/m`-anchored
  // pattern would let the match START on an earlier line and run through the
  // blanked-out comments above the directive. The reported line - the one a
  // developer is told to go and look at - would be the wrong line entirely.
  { id: 'using-testcontainers', label: 'a Testcontainers using directive', re: /^[ \t]*(?:global[ \t]+)?using[ \t]+Testcontainers\b/m },
  { id: 'qualified-testcontainers', label: 'a Testcontainers type reference', re: /\bTestcontainers\.[A-Za-z]/ },
  { id: 'postgresql-builder', label: 'PostgreSqlBuilder (Testcontainers.PostgreSql)', re: /\bPostgreSqlBuilder\b/ },
  { id: 'rabbitmq-builder', label: 'RabbitMqBuilder (Testcontainers.RabbitMq)', re: /\bRabbitMqBuilder\b/ },
  { id: 'redis-builder', label: 'RedisBuilder (Testcontainers.Redis)', re: /\bRedisBuilder\b/ },
  { id: 'minio-builder', label: 'MinioBuilder (Testcontainers.Minio)', re: /\bMinioBuilder\b/ },
  { id: 'container-interface', label: 'IContainer<T> (the Testcontainers lifecycle interface)', re: /\bIContainer\s*</ },
  { id: 'container-fixture-base', label: 'TestFixtureBase (this repository\'s container fixture base)', re: /\bTestFixtureBase\b/ },
];

/** Test attributes that make a method a test. `[Trait]` alone does not. */
const TEST_ATTRIBUTE = /\[\s*(?:Fact|Theory|SkippableFact|SkippableTheory)\b/;

/** A method or class that can report `Skipped` instead of running. */
const MAY_SKIP = /\bSkip\s*\.\s*(?:If|Unless)\s*\(|\[\s*Skippable(?:Fact|Theory)\b/;

const TYPE_DECLARATION =
  /^\s*(?:(?:public|internal|private|protected|abstract|sealed|static|partial|unsafe|file|new|readonly|ref)\s+)*(?:class|record|struct|interface)\s+([A-Za-z_]\w*)/;

/**
 * Blanks out comments while keeping every character position and every newline,
 * so a line number computed on the result is the line number in the original
 * file. Strings are tracked so that a `//` inside a URL, or a `/*` inside a
 * literal, does not start a comment.
 *
 * @param {string} text C# source
 * @returns {string} the same source with comment bodies replaced by spaces
 */
export function stripCommentsPreservingLines(text) {
  const out = new Array(text.length);
  let i = 0;
  let inLineComment = false;
  let inBlockComment = false;
  let inString = false;
  let inVerbatim = false;

  while (i < text.length) {
    const ch = text[i];
    const next = text[i + 1];

    if (inLineComment) {
      if (ch === '\n') {
        inLineComment = false;
        out[i] = ch;
      } else {
        out[i] = ' ';
      }
      i += 1;
      continue;
    }

    if (inBlockComment) {
      if (ch === '*' && next === '/') {
        out[i] = ' ';
        out[i + 1] = ' ';
        i += 2;
        inBlockComment = false;
        continue;
      }
      out[i] = ch === '\n' ? '\n' : ' ';
      i += 1;
      continue;
    }

    if (inString) {
      out[i] = ch;
      if (inVerbatim) {
        if (ch === '"') {
          if (next === '"') {
            out[i + 1] = '"';
            i += 2;
            continue;
          }
          inString = false;
          inVerbatim = false;
        }
      } else if (ch === '\\') {
        if (next !== undefined) {
          out[i + 1] = next;
          i += 2;
          continue;
        }
      } else if (ch === '"') {
        inString = false;
      } else if (ch === '\n') {
        // An unterminated literal must not swallow the rest of the file: the
        // next `"` on a later line would otherwise close it, and every marker
        // in between would be read as being inside a string.
        inString = false;
      }
      i += 1;
      continue;
    }

    if (ch === '/' && next === '/') {
      out[i] = ' ';
      out[i + 1] = ' ';
      i += 2;
      inLineComment = true;
      continue;
    }
    if (ch === '/' && next === '*') {
      out[i] = ' ';
      out[i + 1] = ' ';
      i += 2;
      inBlockComment = true;
      continue;
    }
    if (ch === '@' && next === '"') {
      out[i] = ch;
      out[i + 1] = next;
      i += 2;
      inString = true;
      inVerbatim = true;
      continue;
    }
    if (ch === '"') {
      out[i] = ch;
      i += 1;
      inString = true;
      inVerbatim = false;
      continue;
    }

    out[i] = ch;
    i += 1;
  }

  return out.join('');
}

/**
 * @param {string} text C# source
 * @returns {string} a 1-based line -> text map of the original, for messages
 */
function rawLines(text) {
  return text.split(/\r?\n/);
}

/**
 * Every type declaration in the file, with the categories its attribute block
 * declares. C# puts `[Trait(...)]` immediately above the declaration, so the
 * attributes belonging to a type are the contiguous attribute run above it.
 *
 * @param {string} stripped comment-free source
 * @returns {{name: string, line: number, categories: string[]}[]}
 */
export function findTypeScopes(stripped) {
  const lines = stripped.split(/\r?\n/);
  const scopes = [];
  // `pending` is the attribute run waiting for the declaration it decorates. It
  // is deliberately NOT cleared when a multi-line attribute closes: the closing
  // line is part of the run, and clearing there discards the whole attribute
  // (which is how a `[Trait(` written across four lines came to attribute
  // nothing). It is cleared when the declaration consumes it, and by the first
  // non-blank line that is neither code nor an attribute.
  let pending = [];
  let inAttribute = false;
  let depth = 0;

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    const trimmed = line.trim();

    if (inAttribute) {
      pending.push(line);
      for (const ch of line) {
        if (ch === '[') depth += 1;
        else if (ch === ']') depth -= 1;
      }
      if (depth <= 0) {
        inAttribute = false;
      }
      continue;
    }

    if (trimmed.startsWith('[')) {
      depth = 0;
      for (const ch of line) {
        if (ch === '[') depth += 1;
        else if (ch === ']') depth -= 1;
      }
      // Appended, not replaced: two stacked one-line attributes
      // (`[Trait] [Trait]`) are one run belonging to one type.
      pending.push(line);
      if (depth > 0) {
        inAttribute = true;
      }
      continue;
    }

    const declaration = TYPE_DECLARATION.exec(line);
    if (declaration) {
      const attributeBlock = pending.join('\n');
      const categories = [...attributeBlock.matchAll(/\[\s*Trait\s*\(\s*["']Category["']\s*,\s*["']([^"']+)["']\s*\)/g)]
        .map((match) => match[1]);
      scopes.push({ name: declaration[1], line: index + 1, categories });
      pending = [];
      continue;
    }

    // Any other non-blank line ends the attribute run: `[Trait]` decorates the
    // declaration below it, not whatever comes several lines later. A blank
    // line does not, which is why the attribute may be separated from its type
    // by formatting.
    if (trimmed !== '') {
      pending = [];
    }
  }

  return scopes;
}

/**
 * Container dependencies, one finding per line.
 *
 * The per-line granularity is deliberate. `using Testcontainers.PostgreSql;`
 * matches both the using-directive marker and the qualified-type marker, and
 * reporting it twice would make one problem read as two. When a line does match
 * several markers the FIRST in `CONTAINER_MARKERS` order wins, so the message
 * names the most specific thing on the line.
 *
 * @param {string} text C# source
 * @returns {{id: string, label: string, line: number, snippet: string}[]}
 */
export function findContainerDependencies(text) {
  const stripped = stripCommentsPreservingLines(text);
  const raw = rawLines(text);
  const found = [];
  const claimedLines = new Set();

  for (const marker of CONTAINER_MARKERS) {
    const global = new RegExp(marker.re.source, marker.re.flags.includes('g') ? marker.re.flags : `${marker.re.flags}g`);
    for (const match of stripped.matchAll(global)) {
      const line = stripped.slice(0, match.index).split('\n').length;
      if (claimedLines.has(line)) continue;
      claimedLines.add(line);
      found.push({ id: marker.id, label: marker.label, line, snippet: (raw[line - 1] ?? '').trim() });
    }
  }

  return found.sort((a, b) => a.line - b.line);
}

/**
 * @param {string} text C# source
 * @returns {number} how many test methods the file declares
 */
export function countTestAttributes(text) {
  const stripped = stripCommentsPreservingLines(text);
  return (stripped.match(new RegExp(TEST_ATTRIBUTE.source, 'g')) ?? []).length;
}

/**
 * @param {string} text C# source
 * @returns {{line: number, snippet: string}[]} every construct that can report `Skipped`
 */
export function findMaySkipSites(text) {
  const strippedLines = stripCommentsPreservingLines(text).split(/\r?\n/);
  const raw = rawLines(text);
  const sites = [];
  for (let index = 0; index < strippedLines.length; index += 1) {
    if (MAY_SKIP.test(strippedLines[index])) {
      sites.push({ line: index + 1, snippet: (raw[index] ?? '').trim() });
    }
  }
  return sites;
}

/**
 * The per-file verdict.
 *
 * @typedef {object} ContainerDependency
 * @property {string} id
 * @property {string} label
 * @property {number} line
 * @property {string} snippet
 * @property {string|null} scope the type it sits in, or null for a file-level one
 * @property {boolean} exempt whether the enclosing type carries the `Integration` trait
 *
 * @typedef {object} FileVerdict
 * @property {string} path
 * @property {string} name the file name, for messages
 * @property {'clean'|'excluded'|'violation'} verdict
 * @property {ContainerDependency[]} dependencies
 * @property {string[]} categories every category declared on any type in the file
 * @property {number} tests how many test methods it declares
 * @property {{line: number, snippet: string}[]} maySkip
 */

/**
 * @param {string} path repository-relative path, used only in messages
 * @param {string} text the file's contents
 * @returns {FileVerdict}
 */
export function classifyFile(path, text) {
  const stripped = stripCommentsPreservingLines(text);
  const scopes = findTypeScopes(stripped);
  const categories = [...new Set(scopes.flatMap((scope) => scope.categories))];
  const tests = countTestAttributes(text);
  const maySkip = findMaySkipSites(text);

  // The exemption is resolved PER DEPENDENCY, not per file.
  //
  // A file-level check (`does any type in this file carry the trait?`) is
  // exactly the bug a reviewer would not spot: tag one integration class in a
  // file and every other class in that file becomes exempt, including the
  // untagged one that would have been skipped. So each dependency is attributed
  // to the type it sits inside, and only that type's traits can excuse it.
  // A dependency outside every type (a `using` directive) falls back to the
  // file, because a using directive is genuinely file-scoped.
  const dependencies = findContainerDependencies(text).map((dependency) => {
    const enclosing = [...scopes].reverse().find((scope) => scope.line <= dependency.line) ?? null;
    const relevant = enclosing ?? { categories };
    return {
      ...dependency,
      scope: enclosing?.name ?? null,
      exempt: relevant.categories.includes(INTEGRATION_CATEGORY),
    };
  });

  let verdict = 'clean';
  if (dependencies.length > 0) {
    verdict = dependencies.every((dependency) => dependency.exempt) ? 'excluded' : 'violation';
  }

  return { path, name: basename(path), verdict, dependencies, categories, tests, maySkip };
}

/**
 * @typedef {object} TierVerdict
 * @property {boolean} ok
 * @property {string} reason one of the REASON_* values
 * @property {FileVerdict[]} files
 * @property {FileVerdict[]} violations files that must be fixed
 * @property {FileVerdict[]} excluded files correctly tagged `Integration`
 * @property {FileVerdict[]} maySkip files that can report `Skipped`
 * @property {number} filesScanned
 * @property {number} testsDeclared
 * @property {string[]} problems human-readable failures, in file order
 */

/**
 * Aggregates per-file verdicts into the gate's single answer.
 *
 * An unreadable input fails rather than passing: a gate that cannot read the
 * unit project has not verified it, and reporting that as a pass is the exact
 * bug this gate exists to prevent.
 *
 * @param {{path: string, text: string}[]} sources
 * @returns {TierVerdict}
 */
export function evaluateUnitTier(sources) {
  const empty = {
    files: [], violations: [], excluded: [], maySkip: [], filesScanned: 0, testsDeclared: 0, problems: [],
  };

  if (!Array.isArray(sources)) {
    return { ...empty, ok: false, reason: REASON_UNREADABLE, problems: ['the file list is not an array'] };
  }

  const usable = sources.filter(
    (source) => source && typeof source.text === 'string' && source.text.length > 0,
  );
  if (usable.length === 0) {
    return {
      ...empty,
      ok: false,
      reason: REASON_UNREADABLE,
      problems: ['no readable C# source files were supplied, so the unit tier was not inspected at all'],
    };
  }

  const files = usable.map((source) => classifyFile(source.path, source.text));
  const violations = files.filter((file) => file.verdict === 'violation');
  const excluded = files.filter((file) => file.verdict === 'excluded');
  const maySkip = files.filter((file) => file.maySkip.length > 0);
  const testsDeclared = files.reduce((total, file) => total + file.tests, 0);

  const problems = [];
  for (const file of violations) {
    const traits = file.categories.length > 0 ? file.categories.join(', ') : 'none';
    for (const dependency of file.dependencies) {
      if (dependency.exempt) continue;
      const where = dependency.scope === null
        ? 'at file scope'
        : `in type \`${dependency.scope}\``;
      problems.push(
        `${file.path}:${dependency.line}: ${dependency.label} ${where}, and that scope declares no ` +
          `[Trait("Category", "${INTEGRATION_CATEGORY}")]. This tier runs with no container runtime, so an ` +
          `untagged container dependency does not fail - it is SKIPPED, and a skipped test is a missing check. ` +
          `Either tag the enclosing type \`[Trait("Category", "${INTEGRATION_CATEGORY}")]\` (categories declared in this file: ${traits}) ` +
          `or move the test to tests/DubbingPlatform.IntegrationTests. Line: ${dependency.snippet}`,
      );
    }
  }

  if (violations.length > 0) {
    return { ok: false, reason: REASON_UNAVAILABLE, files, violations, excluded, maySkip, filesScanned: files.length, testsDeclared, problems };
  }

  if (testsDeclared === 0) {
    return {
      ok: false,
      reason: REASON_EMPTY,
      files,
      violations,
      excluded,
      maySkip,
      filesScanned: files.length,
      testsDeclared,
      problems: [
        `the unit project declares no [Fact]/[Theory] methods across ${files.length} file(s). ` +
          '`dotnet test --filter FullyQualifiedName~UnitTests` reporting "No test matches the given testcase filter" ' +
          'for every project still exits 0, so an empty tier is indistinguishable from a passing one.',
      ],
    };
  }

  return { ok: true, reason: REASON_OK, files, violations, excluded, maySkip, filesScanned: files.length, testsDeclared, problems: [] };
}

// ---------------------------------------------------------------------------
// The CLI. Deliberately the ONLY I/O in this file, and only when this file is
// the process entry point - every rule above is exported and covered by
// `tools/unit-tier-containers.test.mjs` without touching a disk.
//
// WHY THIS IS A NODE CLI AND NOT A `scripts/*.sh` DRIVER
// -------------------------------------------------------
// The repository's other gates are driven from bash. This one is not, and the
// reason is measured rather than stylistic: on a Windows development host the
// `bash` on PATH is WSL2, which does not carry the Windows Node installation on
// its PATH, so a `bash` driver that shells out to `node` fails on the machine
// that wrote the gate while passing in CI. The same class of problem is
// recorded in the 042 report (npm spawn, `$TMPDIR`, `mktemp -d`). Everything
// this gate does with the filesystem - walk a directory, read text files,
// print lines - Node does natively and identically on every platform, so there
// is nothing for a shell to contribute.
// ---------------------------------------------------------------------------

const EXIT_OK = 0;
const EXIT_FAIL = 1;
const EXIT_UNREADABLE = 2;

/** Directories that are never test source, whatever they are named. */
const SKIPPED_DIRECTORIES = new Set(['bin', 'obj', 'node_modules', '.git', 'TestResults', 'coverage', 'dist']);

/**
 * @param {string} dir absolute path to a directory
 * @param {string} base the directory paths are reported relative to
 * @param {string[]} found accumulator
 * @returns {void}
 */
function walk(dir, base, found) {
  const entries = readdirSync(dir, { withFileTypes: true }).sort((a, b) => (a.name < b.name ? -1 : 1));
  for (const entry of entries) {
    if (SKIPPED_DIRECTORIES.has(entry.name)) continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      walk(full, base, found);
    } else if (entry.name.toLowerCase().endsWith('.cs')) {
      found.push(full);
    }
  }
}

/**
 * @param {string} projectDir absolute path to the test project
 * @param {string} repoRoot used to produce repository-relative paths in messages
 * @returns {{path: string, text: string}[]}
 * @throws {Error} when the directory does not exist or holds no C# at all
 */
export function collectSources(projectDir, repoRoot) {
  const files = [];
  walk(projectDir, repoRoot, files);
  if (files.length === 0) {
    throw new Error(`no .cs files under ${projectDir}`);
  }
  return files.map((full) => ({
    // Repository-relative when the file is inside the repository, absolute
    // otherwise. A relative path that climbs out of the repo (`../../..`) is
    // technically correct and useless in a message a developer has to act on.
    path: (() => {
      const rel = relative(repoRoot, full);
      return rel.startsWith('..') ? full.split(sep).join('/') : rel.split(sep).join('/');
    })(),
    text: readFileSync(full, 'utf8'),
  }));
}

function usage(message) {
  process.stderr.write(`unit-tier-containers: ${message}\n`);
  process.stderr.write(
    'usage: node tools/unit-tier-containers.mjs [--project <dir>] [--manifest <file.json>]\n'
      + '       defaults: --project tests/DubbingPlatform.UnitTests\n',
  );
  process.exit(EXIT_UNREADABLE);
}

function main(argv) {
  const here = dirname(fileURLToPath(import.meta.url));
  const repoRoot = resolve(here, '..');
  let manifest = null;
  let project = 'tests/DubbingPlatform.UnitTests';
  let projectDir = join(repoRoot, project);

  for (let index = 0; index < argv.length; index += 1) {
    const flag = argv[index];
    if (flag === '--manifest') {
      index += 1;
      manifest = argv[index];
    } else if (flag === '--project') {
      index += 1;
      project = argv[index] ?? project;
      projectDir = isAbsolute(project) ? project : join(repoRoot, project);
    } else if (flag === '--self-test') {
      // A liveness answer, so a job that invokes the tool wrong says "that is
      // not a flag I know" rather than reporting a pass.
      process.stdout.write(
        'unit-tier-containers: --self-test does not apply; the rules are pure and are covered by '
          + 'tools/unit-tier-containers.test.mjs (`npm run test:tools`).\n',
      );
      return EXIT_OK;
    } else if (flag.startsWith('--')) {
      usage(`unknown flag ${flag}`);
    } else {
      usage(`unexpected argument ${flag}`);
    }
  }

  let sources;
  try {
    sources = manifest === null ? collectSources(projectDir, repoRoot) : JSON.parse(readFileSync(manifest, 'utf8'));
  } catch (error) {
    process.stderr.write(
      `unit-tier-containers: could not read the unit tier: ${error instanceof Error ? error.message : String(error)}\n`
        + 'A gate that could not read the project has not verified it, so this is a failure and not a skip.\n',
    );
    process.stderr.write(`CI_GATE_RESULT reason=${REASON_UNREADABLE} status=FAIL\n`);
    return EXIT_UNREADABLE;
  }

  const verdict = evaluateUnitTier(sources);

  process.stdout.write(
    `unit-tier-containers: ${verdict.filesScanned} file(s) under ${project}, ${verdict.testsDeclared} test method(s) declared, `
      + `${verdict.violations.length} untagged container dependency(ies), ${verdict.excluded.length} correctly tagged.\n`,
  );

  for (const file of verdict.maySkip) {
    for (const site of file.maySkip) {
      process.stdout.write(`  ADVISORY ${file.path}:${site.line}: can report Skipped, but is not a container dependency: ${site.snippet}\n`);
    }
  }
  if (verdict.maySkip.length > 0) {
    process.stdout.write(
      '  Not a container dependency, so it does not fail this gate. A test that actually reports Skipped fails the job:\n'
        + '  the workflow asserts it over the TRX with tools/trx-assert.mjs --forbid-skipped.\n',
    );
  }

  if (!verdict.ok) {
    for (const problem of verdict.problems) {
      process.stderr.write(`  ::error title=UNIT_TIER::${problem}\n`);
    }
    process.stderr.write(`CI_GATE_RESULT reason=${verdict.reason} status=FAIL\n`);
    return verdict.reason === REASON_UNREADABLE ? EXIT_UNREADABLE : EXIT_FAIL;
  }

  process.stdout.write(`CI_GATE_RESULT reason=${REASON_OK} status=PASS files=${verdict.filesScanned} tests=${verdict.testsDeclared}\n`);
  return EXIT_OK;
}

// Only the entry point runs the CLI; importing the rules above runs nothing.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exit(main(process.argv.slice(2)));
}
