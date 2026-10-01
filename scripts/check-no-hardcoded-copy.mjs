#!/usr/bin/env node
/**
 * Hard-coded-copy extraction gate (Task 045, instruction 1 / R1).
 *
 * WHAT IT DECIDES
 * ---------------
 * "Does any UI string reach a human outside `src/i18n/locales/`?"
 *
 * The interesting failure it prevents is not a missing translation. It is the
 * opposite: a screen that *looks* translated, passes every LTR test and every
 * visual baseline, and is still half English because somebody typed `<button>
 * Save</button>` instead of `{t('projects:actions.save')}`. Nothing detects that
 * until a translator opens the file, and by then it is 400 strings in one
 * feature area.
 *
 * WHY A GATE AND NOT A REVIEW RULE
 * ---------------------------------
 * Same argument as `scripts/check-frontend-topology.mjs`. Copy is invisible in
 * review precisely because it reads correctly — it is *supposed* to be English.
 * And the cost of getting it wrong is asymmetric: an untranslated string is a
 * support ticket ("the button says something my colleague's screen does not"),
 * not a crash. A check is the only thing that catches it.
 *
 * WHY A RATCHET, NOT A ZERO TOLERANCE GATE
 * ----------------------------------------
 * This gate landed on a tree with 939 pre-existing literals across the feature
 * areas owned by Tasks 019–036, whose own task files list "feature screen copy"
 * as *their* deliverable. Task 045's Scope explicitly excludes it. So:
 *
 *   - every NEW literal fails, in a NEW file or an existing one,
 *   - every file's count may only go DOWN, never up,
 *   - inflating the baseline is a visible two-place edit (per-file map *and*
 *     per-rule totals, cross-checked against each other).
 *
 * That is a ratchet, not an amnesty. `docs/i18n.md` carries the remaining
 * count, the baseline carries the per-file detail, and every task that touches a
 * baselined file is expected to move its numbers down.
 *
 * The alternative — migrating 939 strings here — would have rewritten 56 files
 * and several hundred test assertions owned by six completed tasks, and would
 * have produced a diff nobody could review.
 *
 * THE PROOF THAT IT WORKS
 * -----------------------
 * A gate that reports PASS having matched nothing is worse than no gate, so
 * `deploy/frontend/hardcoded-copy.test.mjs` drives every rule with a synthetic
 * source written the way the mistake is actually written, drives every rule
 * again with the correct code this task exists to produce, and runs the rules
 * over the real tree to assert the baseline is not simply a copy of whatever the
 * scanner currently reports. The `physical-side` rule is asserted to hold a real
 * ZERO across the tree, which is what makes the RTL half evidence rather than a
 * recorded debt line.
 *
 * MACHINE-READABLE OUTPUT
 * -----------------------
 *   COPY_GATE_RESULT reason=<REASON> status=<PASS|FAIL> files=<n> findings=<n> new=<n>
 *
 *   OK                          nothing outside the baseline
 *   COPY_GATE_VIOLATION         a literal outside the baseline (new, or a count up)
 *   COPY_GATE_INPUT_MISSING     frontend/src is absent, `typescript` is not
 *                               installed, or fewer than MIN_SCANNABLE_FILES were
 *                               scannable — a FAILURE, because a check that read
 *                               nothing has cleared nothing
 *   COPY_GATE_BASELINE_DRIFT    the baseline disagrees with itself (per-file map
 *                               vs per-rule totals vs the committed totals header)
 *
 * USAGE
 * -----
 *   node scripts/check-no-hardcoded-copy.mjs                 # the gate
 *   node scripts/check-no-hardcoded-copy.mjs --strict        # ignore the baseline
 *   node scripts/check-no-hardcoded-copy.mjs --write-baseline # re-record debt
 *
 * EXIT: 0 pass, 1 violation/drift, 2 could not run.
 */
import { readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

export const REASON_OK = 'OK';
export const REASON_VIOLATION = 'COPY_GATE_VIOLATION';
export const REASON_INPUT_MISSING = 'COPY_GATE_INPUT_MISSING';
export const REASON_BASELINE_DRIFT = 'COPY_GATE_BASELINE_DRIFT';

export const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const FRONTEND_SRC = join(REPO_ROOT, 'frontend', 'src');
export const BASELINE_PATH = join(REPO_ROOT, 'scripts', 'hardcoded-copy-baseline.json');

/** Rule ids this module implements. Asserted against `RULES` by the suite. */
export const RULES = ['jsx-text', 'user-facing-prop', 'copy-fallback', 'physical-side'];

/**
 * Paths excluded from the scan, each with its reason. A list with reasons
 * rather than a filter, because an unexplained exclusion is indistinguishable
 * from an exclusion added to make a failure go away. Keys are paths relative to
 * `frontend/src` — that is what `listScannableFiles` produces.
 *
 * `deploy/frontend/hardcoded-copy.test.mjs` asserts this contains exactly these
 * entries with exactly these reasons, so adding a second is a visible edit.
 */
export const EXEMPT_PATHS = Object.freeze({
  i18n: 'the translation bundles themselves, and the pseudo generator that reads them',
  components: 'the primitives layer (Task 016): it takes copy as props; only the documented fallback words carry literals, and Task 016 owns them',
  api: 'transport and generated shapes: error `defaultValue`s here are server-envelope defaults, not rendered copy',
  telemetry: 'allowlisted scalars, never rendered',
  types: 'a type-only module: no runtime values and nothing rendered',
  mocks: 'test fixtures, never bundled',
  config: 'env parsing, never rendered',
});

/**
 * Test and story files, matched by name anywhere under the scan root.
 * Asserted rather than filtered by directory, because a suite can live next to
 * the thing it tests.
 */
export const EXEMPT_FILE_RE = /\.(test|spec|stories)\.(ts|tsx)$/;

/**
 * The JSX attributes whose literal string value is copy a human reads.
 *
 * A deliberately short list. It is a list of *prop names*, not of "attributes
 * with a string", because `className`, `data-testid`, `id`, `type`, `role`,
 * `value`, `name`, `to` and `htmlFor` are string-valued, high-volume, and never
 * prose.
 *
 * `ARIA_NAME_ATTRS` is separate because "an attribute starting with `aria-`" is
 * the wrong rule: `aria-labelledby`, `aria-controls`, `aria-expanded`,
 * `aria-hidden` and `aria-live` all take enum values or element ids, and gating
 * them produces hundreds of findings that are all correct code. Only the members
 * that carry a *human-readable name* are copy.
 */
export const USER_FACING_PROPS = Object.freeze([
  'alt',
  'cancelLabel',
  'caption',
  'confirmLabel',
  'description',
  'emptyMessage',
  'heading',
  'helperText',
  'label',
  'legend',
  'message',
  'placeholder',
  'subtitle',
  'summary',
  'text',
  'title',
  'tooltip',
]);

/** The `aria-*` members whose value is prose rather than an id or an enum. */
export const ARIA_NAME_ATTRS = Object.freeze([
  'aria-description',
  'aria-label',
  'aria-placeholder',
  'aria-roledescription',
  'aria-valuetext',
]);

/**
 * Variable names that make a string literal copy. Used by `copy-fallback` for a
 * `const` initializer. Word-bounded so `titleize`/`labelCount` do not match.
 */
const COPYISH_NAME = /(^|[^a-z])(title|label|message|description|heading|summary|caption|tooltip|placeholder|emptyText|errorText|hint|note|helpText|actionLabel|statusText)([^a-z]|$)/i;

/** Minimum words before a bare string literal is treated as prose. */
const MIN_PROSE_WORDS = 3;

/**
 * Tailwind utilities that pin a *physical* side.
 *
 * `rtl` is a hard requirement (Task 045, instruction 3), and the reason this
 * belongs in the copy gate rather than a lint rule is that both are "this will
 * look fine in every test we run and be broken in Arabic". A `ml-4` in a
 * component renders identically in the LTR unit test, the LTR visual baseline
 * and the LTR a11y audit; it is only wrong under `dir="rtl"`.
 *
 * The patterns are anchored at the start of a class token on purpose: `ml-2`
 * must match and `rounded-lg` must not, which a prefix search gets backwards.
 * The optional leading `-` is Tailwind's `!` important modifier (`-ml-2`), which
 * pins the same physical side and must be caught with it.
 */
export const PHYSICAL_SIDE_CLASSES = Object.freeze([
  /^-?m[lr]-(?:\d+(?:\.\d+)?|auto|px)$/,
  /^-?p[lr]-(?:\d+(?:\.\d+)?|px)$/,
  /^-?left-(?:\d+(?:\.\d+)?|auto|px|full)$/,
  /^-?right-(?:\d+(?:\.\d+)?|auto|px|full)$/,
  /^-?border-[lr](?:-\d+)?$/,
  /^-?rounded-[lr](?:-[a-z0-9[\]()./%]+)?$/,
  /^-?text-(?:left|right)$/,
  /^-?float-(?:left|right)$/,
  /^-?clear-(?:left|right)$/,
]);

/** CSS declarations that pin a physical side, in `.css` and in `style={}`. */
export const PHYSICAL_SIDE_CSS = Object.freeze([
  /(?:^|[;{\s])(?:margin|padding|border)-(?:left|right)\s*:/,
  /(?:^|[;{\s])(?:left|right)\s*:/,
  /(?:^|[;{\s])text-align\s*:\s*(?:left|right)\b/,
  /(?:^|[;{\s])float\s*:\s*(?:left|right)\b/,
  /(?:^|[;{\s])clear\s*:\s*(?:left|right)\b/,
  /(?:^|[;{\s])border-(?:top|bottom)-(?:left|right)-radius\s*:/,
]);

/**
 * Inline-style *property names* that pin a physical side. React's `style` object
 * is camelCase, so it needs its own list rather than the CSS regexes above.
 *
 * `float` / `clear` / `textAlign` are value-dependent and are handled by
 * {@link PHYSICAL_SIDE_VALUE_PROPS} instead of being listed here — `textAlign:
 * 'start'` is correct code and `textAlign: 'right'` is not.
 */
export const PHYSICAL_SIDE_STYLE_PROPS = Object.freeze([
  'left',
  'right',
  'borderTopLeftRadius',
  'borderTopRightRadius',
  'borderBottomLeftRadius',
  'borderBottomRightRadius',
]);

/**
 * The longhand families: `borderLeft`, `marginRightWidth`, `paddingLeftColor`
 * and so on. A name list would miss one of the eight spellings of every family,
 * and a missed spelling is a gate reporting PASS on a broken layout.
 */
export const PHYSICAL_SIDE_STYLE_PROP_RE =
  /^(?:margin|padding|border|inset)(?:Top|Bottom)?(?:Left|Right)(?:Width|Color|Style)?$/;

/** Inline-style properties whose *value* names a physical side. */
export const PHYSICAL_SIDE_VALUE_PROPS = Object.freeze({
  textAlign: /^(?:left|right)$/,
  float: /^(?:left|right)$/,
  clear: /^(?:left|right)$/,
});

/**
 * The floor on "how many files did we actually read".
 *
 * `check-frontend-topology.mjs` fails with `*_INPUT_MISSING` rather than
 * reporting PASS on a tree it could not see, and the same failure mode is the
 * reason this constant exists: a scan that finds zero files because
 * `frontend/src` moved, because the exclusion list accidentally covered
 * everything, or because a bad glob matched nothing, is indistinguishable from a
 * clean tree at the exit code. 217 files are scannable today; 50 is a floor wide
 * enough that no legitimate layout change crosses it and narrow enough that a
 * broken path is caught.
 */
export const MIN_SCANNABLE_FILES = 50;

const require_ = createRequire(import.meta.url);

/**
 * The TypeScript compiler, or `undefined` when it is not installed.
 *
 * Deliberately resolved at runtime rather than imported statically: the gate has
 * to *fail closed* with `COPY_GATE_INPUT_MISSING` when the toolchain is absent,
 * not crash with a module-resolution stack trace that says nothing about what
 * was or was not checked.
 */
export function loadTypeScript() {
  try {
    return require_('typescript');
  } catch {
    return undefined;
  }
}

function isExemptFile(relPath) {
  if (EXEMPT_FILE_RE.test(relPath)) {
    return true;
  }
  const normalized = relPath.split(sep).join('/');
  for (const prefix of Object.keys(EXEMPT_PATHS)) {
    if (normalized === prefix || normalized.startsWith(`${prefix}/`)) {
      return true;
    }
  }
  return false;
}

/** Every scannable file under `root`, relative to `root`, sorted. */
export function listScannableFiles(root = FRONTEND_SRC) {
  const out = [];
  const walk = (dir) => {
    let entries = [];
    try {
      entries = readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const entry of entries) {
      const full = join(dir, entry.name);
      if (entry.isDirectory()) {
        if (entry.name === 'node_modules' || entry.name === 'dist' || entry.name === 'storybook-static') {
          continue;
        }
        walk(full);
        continue;
      }
      const ext = extname(entry.name);
      if (ext !== '.ts' && ext !== '.tsx') {
        continue;
      }
      const rel = relative(root, full).split(sep).join('/');
      if (isExemptFile(rel)) {
        continue;
      }
      out.push(rel);
    }
  };
  walk(root);
  return out.sort();
}

function hasLetter(text) {
  return /[\p{L}]/u.test(text);
}

/** A string literal the gate considers prose (as opposed to an identifier). */
function isProse(text) {
  if (!hasLetter(text)) {
    return false;
  }
  return text.trim().split(/\s+/).filter(Boolean).length >= MIN_PROSE_WORDS;
}

function attributeName(ts, node) {
  const name = node.name;
  if (ts.isIdentifier(name)) {
    return name.text;
  }
  if (ts.isJsxNamespacedName(name)) {
    return `${name.namespace.text}:${name.name.text}`;
  }
  return '';
}

function finding(rule, line, detail) {
  return { rule, line, detail };
}

/**
 * The scanner. `ts` is injected so the suite can pass a compiler it loaded
 * itself, and so this function stays synchronous and side-effect free apart from
 * reading the file.
 */
export function scanSource(ts, source, fileName, options = {}) {
  const findings = [];
  const kind = fileName.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS;
  const sourceFile = ts.createSourceFile(fileName, source, ts.ScriptTarget.ESNext, true, kind);
  const lineOf = (node) => sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile)).line + 1;
  const skipPhysical = options.skipPhysicalSide === true;

  const recordPhysicalSide = (text, line) => {
    if (skipPhysical) {
      return;
    }
    for (const token of String(text).split(/\s+/)) {
      if (PHYSICAL_SIDE_CLASSES.some((re) => re.test(token))) {
        findings.push(finding('physical-side', line, token));
        return;
      }
    }
  };

  const visit = (node) => {
    // Rule 1 — literal JSX text: `>Save<`.
    if (ts.isJsxText(node)) {
      const text = node.text;
      const trimmed = text.trim();
      if (trimmed !== '' && hasLetter(trimmed)) {
        findings.push(finding('jsx-text', lineOf(node), trimmed.slice(0, 60)));
      }
    }

    // Rule 2 — a user-facing prop carrying a string or template literal.
    if (ts.isJsxAttribute(node) && node.initializer !== undefined) {
      const name = attributeName(ts, node);
      const init = node.initializer;
      if (USER_FACING_PROPS.includes(name) || ARIA_NAME_ATTRS.includes(name)) {
        if (ts.isStringLiteral(init) && init.text.trim() !== '') {
          findings.push(finding('user-facing-prop', lineOf(node), `${name}="${init.text.slice(0, 60)}"`));
        } else if (ts.isNoSubstitutionTemplateLiteral(init) || ts.isTemplateExpression(init)) {
          findings.push(finding('user-facing-prop', lineOf(node), `${name}={…}`));
        } else if (ts.isJsxExpression(init)) {
          // A `{...}` initializer is reported only when the prose is written
          // *in the attribute* — a template literal. A bare pass-through
          // (`title={label}`) is correct code and reporting it would bury the
          // real findings; when that value came from a hard-coded literal it is
          // a `copy-fallback` (`const TITLE = '…'`) and is caught there instead.
          // A `{t('ns:key')}` initializer is the whole point of this task and is
          // never reported.
          const expression = init.expression;
          if (
            expression !== undefined &&
            (ts.isTemplateExpression(expression) || ts.isNoSubstitutionTemplateLiteral(expression))
          ) {
            findings.push(finding('user-facing-prop', lineOf(node), `${name}={…}`));
          }
        }
      }
    }

    // Rule 3 — copy that is not in JSX at all: a prose literal used as a `??`
    // / `||` fallback, or as a copy-named const.
    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
      if (isProse(node.text)) {
        const parent = node.parent;
        if (ts.isBinaryExpression(parent) && parent.right === node && parent.operatorToken !== undefined) {
          const operator = parent.operatorToken.kind;
          if (operator === ts.SyntaxKind.QuestionQuestionToken || operator === ts.SyntaxKind.BarBarToken) {
            findings.push(finding('copy-fallback', lineOf(node), node.text.slice(0, 60)));
          }
        } else if (ts.isVariableDeclaration(parent) && parent.initializer === node && ts.isIdentifier(parent.name)) {
          if (COPYISH_NAME.test(parent.name.text)) {
            findings.push(finding('copy-fallback', lineOf(node), `${parent.name.text} = "${node.text.slice(0, 50)}"`));
          }
        }
      }
    }

    // Rule 4 — a physical side pinned in a class list or an inline style.
    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node) || ts.isJsxText(node)) {
      if (node.text !== undefined && typeof node.text === 'string') {
        recordPhysicalSide(node.text, lineOf(node));
      }
    }
    if (ts.isJsxAttribute(node) && node.initializer !== undefined && ts.isJsxExpression(node.initializer)) {
      const expression = node.initializer.expression;
      if (expression !== undefined && ts.isObjectLiteralExpression(expression)) {
        for (const property of expression.properties) {
          if (!ts.isPropertyAssignment(property) || property.initializer === undefined) {
            continue;
          }
          const value = property.initializer;
          const name = ts.isIdentifier(property.name)
            ? property.name.text
            : ts.isStringLiteral(property.name)
              ? property.name.text
              : '';
          if (PHYSICAL_SIDE_STYLE_PROPS.includes(name) || PHYSICAL_SIDE_STYLE_PROP_RE.test(name)) {
            findings.push(finding('physical-side', lineOf(property), `${name}: …`));
            continue;
          }
          const valueRe = PHYSICAL_SIDE_VALUE_PROPS[name];
          if (
            valueRe !== undefined &&
            (ts.isStringLiteral(value) || ts.isNoSubstitutionTemplateLiteral(value)) &&
            valueRe.test(value.text)
          ) {
            findings.push(finding('physical-side', lineOf(property), `${name}: '${value.text}'`));
          }
          if (ts.isStringLiteral(value) || ts.isNoSubstitutionTemplateLiteral(value)) {
            recordPhysicalSide(value.text, lineOf(property));
          }
        }
      }
    }

    ts.forEachChild(node, visit);
  };

  visit(sourceFile);
  return findings;
}

/** CSS declarations with a physical side. Exported so `.css` is scanned too. */
export function scanCss(source, fileName) {
  const findings = [];
  const lines = source.split('\n');
  lines.forEach((line, index) => {
    const withoutComment = line.replace(/\/\*.*?\*\//g, '');
    for (const re of PHYSICAL_SIDE_CSS) {
      const match = re.exec(withoutComment);
      if (match !== null) {
        findings.push(finding('physical-side', index + 1, match[0].trim()));
        return;
      }
    }
  });
  return findings;
}

/** Runs the scanner over the whole real tree. Pure given `ts`. */
export function scanTree(ts, root = FRONTEND_SRC) {
  const files = listScannableFiles(root);
  const perFile = {};
  for (const rel of files) {
    const source = readFileSync(join(root, rel), 'utf8');
    perFile[rel] = scanSource(ts, source, rel);
  }
  // `.css` carries the other half of the physical-side rule: a stylesheet written
  // with logical properties can still be handed one `margin-left`, and Tailwind
  // class names are only half the surface a physical side can hide in.
  const cssFiles = listScannableCssFiles(root);
  for (const rel of cssFiles) {
    perFile[rel] = scanCss(readFileSync(join(root, rel), 'utf8'), rel);
  }
  return { files: [...files, ...cssFiles], perFile };
}

/** Every non-exempt `.css` file under `root`, relative to `root`, sorted. */
export function listScannableCssFiles(root = FRONTEND_SRC) {
  const out = [];
  const walk = (dir) => {
    let entries = [];
    try {
      entries = readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const entry of entries) {
      const full = join(dir, entry.name);
      if (entry.isDirectory()) {
        if (entry.name === 'node_modules' || entry.name === 'dist' || entry.name === 'storybook-static') {
          continue;
        }
        walk(full);
        continue;
      }
      if (extname(entry.name) !== '.css') {
        continue;
      }
      const rel = relative(root, full).split(sep).join('/');
      if (!isExemptFile(rel)) {
        out.push(rel);
      }
    }
  };
  walk(root);
  return out.sort();
}

/** Counts findings per rule across a `scanTree` result. */
export function tally(perFile) {
  const rules = {};
  for (const rule of RULES) {
    rules[rule] = 0;
  }
  let total = 0;
  for (const findings of Object.values(perFile)) {
    for (const item of findings) {
      rules[item.rule] = (rules[item.rule] ?? 0) + 1;
      total += 1;
    }
  }
  return { rules, total };
}

export function readBaseline(path = BASELINE_PATH) {
  return JSON.parse(readFileSync(path, 'utf8'));
}

export function buildBaseline(perFile) {
  const files = {};
  for (const rel of Object.keys(perFile).sort()) {
    const findings = perFile[rel];
    if (findings.length === 0) {
      continue;
    }
    const perRule = {};
    for (const item of findings) {
      perRule[item.rule] = (perRule[item.rule] ?? 0) + 1;
    }
    files[rel] = perRule;
  }
  return {
    $comment:
      'Task 045 hard-coded-copy ratchet. New literals fail the gate; a file may only go down. Regenerate with `node scripts/check-no-hardcoded-copy.mjs --write-baseline` and say why in the commit.',
    rules: tally(perFile).rules,
    files,
  };
}

/**
 * The comparison. Returns the violations (empty = pass) plus the bookkeeping the
 * caller prints.
 *
 * Drift is checked first and separately from violations: a baseline whose
 * per-file map and per-rule totals disagree is a broken gate, and reporting that
 * as "0 new findings" would be exactly the green-from-nothing failure this task
 * exists to prevent.
 */
export function diffAgainstBaseline(perFile, baseline, options = {}) {
  const strict = options.strict === true;
  const violations = [];
  const drift = [];

  const baseFiles = baseline.files ?? {};
  for (const rel of Object.keys(perFile).sort()) {
    const found = perFile[rel].length;
    const allowed = strict ? 0 : baseFiles[rel] ? Object.values(baseFiles[rel]).reduce((a, b) => a + b, 0) : 0;
    if (found > allowed) {
      violations.push({ file: rel, found, allowed });
    }
  }

  const actualTally = tally(perFile);
  if (baseline.rules !== undefined) {
    for (const rule of RULES) {
      if (baseline.rules[rule] !== actualTally.rules[rule]) {
        drift.push(`per-rule total for ${rule}: baseline ${String(baseline.rules[rule])} vs scanned ${String(actualTally.rules[rule])}`);
      }
    }
  }

  return { violations, drift, actualTally };
}

function existsDirectory(path) {
  try {
    return statSync(path).isDirectory();
  } catch {
    return false;
  }
}

function main(argv) {
  const ts = loadTypeScript();
  if (ts === undefined || !existsDirectory(FRONTEND_SRC)) {
    process.stderr.write(`${REASON_INPUT_MISSING}\n`);
    process.stderr.write(`  frontend/src is ${existsDirectory(FRONTEND_SRC) ? 'present' : 'ABSENT'}, typescript is ${ts === undefined ? 'NOT INSTALLED' : 'installed'}.\n`);
    process.stderr.write('  Run `npm ci` at the repository root. A gate that read nothing has cleared nothing.\n');
    return 2;
  }

  const { files, perFile } = scanTree(ts);
  if (files.length < MIN_SCANNABLE_FILES) {
    process.stderr.write(`${REASON_INPUT_MISSING}\n`);
    process.stderr.write(`  only ${String(files.length)} file(s) were scannable; the floor is ${String(MIN_SCANNABLE_FILES)}.\n`);
    process.stderr.write('  A gate that read nothing has cleared nothing.\n');
    return 2;
  }
  const baseline = readBaseline();

  if (argv.includes('--write-baseline')) {
    writeFileSync(BASELINE_PATH, `${JSON.stringify(buildBaseline(perFile), null, 2)}\n`, 'utf8');
    const counts = tally(perFile);
    process.stdout.write(`baseline written: ${String(files.length)} files scanned, ${String(counts.total)} findings\n`);
    return 0;
  }

  const strict = argv.includes('--strict');
  const { violations, drift, actualTally } = diffAgainstBaseline(perFile, baseline, { strict });

  const baselined = Object.values(baseline.files ?? {}).reduce(
    (sum, perRule) => sum + Object.values(perRule).reduce((a, b) => a + b, 0),
    0,
  );

  if (violations.length > 0) {
    process.stderr.write(`${REASON_VIOLATION}: hard-coded user-facing copy outside src/i18n/locales\n`);
    for (const violation of violations) {
      process.stderr.write(`  ${violation.file}: ${String(violation.found)} finding(s), baseline allows ${String(violation.allowed)}\n`);
      for (const item of perFile[violation.file].slice(0, 10)) {
        process.stderr.write(`    ${String(item.line)}  [${item.rule}] ${item.detail}\n`);
      }
    }
    process.stderr.write('  Move the copy into src/i18n/locales/en/<ns>.json and read it with t().\n');
    // Drift is printed WITH the violation rather than short-circuiting ahead of
    // it. A developer who added one literal also made the baseline totals stale,
    // and "the totals disagree" is not the thing they need to read - the file
    // and line above it are.
    if (drift.length > 0) {
      process.stderr.write(`  (also ${REASON_BASELINE_DRIFT}, a consequence of the above)\n`);
      for (const line of drift) {
        process.stderr.write(`  ${line}\n`);
      }
    }
    process.stderr.write(`COPY_GATE_RESULT reason=${REASON_VIOLATION} status=FAIL files=${String(files.length)} findings=${String(actualTally.total)} new=${String(violations.length)}\n`);
    return 1;
  }

  if (drift.length > 0) {
    // No violation, so drift here means the baseline was edited by hand into a
    // state the scanner cannot reproduce. That is the green-from-nothing failure
    // and it must not report a pass.
    process.stderr.write(`${REASON_BASELINE_DRIFT}\n`);
    for (const line of drift) {
      process.stderr.write(`  ${line}\n`);
    }
    process.stderr.write('  Regenerate with `node scripts/check-no-hardcoded-copy.mjs --write-baseline`.\n');
    process.stderr.write(`COPY_GATE_RESULT reason=${REASON_BASELINE_DRIFT} status=FAIL files=${String(files.length)} findings=${String(actualTally.total)} new=0\n`);
    return 1;
  }

  process.stdout.write(`COPY_GATE_RESULT reason=${REASON_OK} status=PASS files=${String(files.length)} findings=${String(actualTally.total)} new=0\n`);
  process.stdout.write(
    `  ratchet: ${String(baselined)} pre-existing literal(s) recorded in scripts/hardcoded-copy-baseline.json and may only decrease.\n`,
  );
  for (const rule of RULES) {
    process.stdout.write(`  ${rule}: ${String(actualTally.rules[rule])}\n`);
  }
  return 0;
}

if (process.argv[1] !== undefined && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) {
  process.exit(main(process.argv.slice(2)));
}