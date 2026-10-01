// The hard-coded-copy gate's own tests (Task 045, instruction 1 / R1 / Testing).
//
// THREE KINDS OF TEST, AND ALL THREE MATTER
// -----------------------------------------
//   1. Every rule has been seen to FIRE, on a synthetic source written the way
//      the mistake is actually written. A rule that has never matched anything
//      has not been tested, and a rule that matches nothing is indistinguishable
//      from a rule that is silently broken — which is the failure mode where a
//      gate reports PASS having asserted nothing.
//   2. Every rule has been seen to STAY SILENT on the code the task exists to
//      bless. A gate that fires on `t('nav:primary')` gets disabled within a
//      week, and a disabled gate is worse than no gate because it is believed.
//   3. The rules are then run over the REAL `frontend/src`, which is the
//      assertion that actually means anything: the tree as committed is clean
//      against the recorded ratchet, the ratchet is non-trivial, and the RTL
//      half of the gate holds a real zero rather than a baseline entry.
//
// This is the same two-hands structure as `topology.test.mjs`, and the reason
// is the same: 043C and 044 each found a gate that passed on a subject list
// derived from the thing it was checking.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  ARIA_NAME_ATTRS,
  BASELINE_PATH,
  EXEMPT_PATHS,
  FRONTEND_SRC,
  PHYSICAL_SIDE_CLASSES,
  PHYSICAL_SIDE_CSS,
  RULES,
  USER_FACING_PROPS,
  buildBaseline,
  diffAgainstBaseline,
  listScannableCssFiles,
  listScannableFiles,
  loadTypeScript,
  MIN_SCANNABLE_FILES,
  readBaseline,
  scanCss,
  scanSource,
  scanTree,
  tally,
} from '../../scripts/check-no-hardcoded-copy.mjs';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const ts = loadTypeScript();

assert.notEqual(ts, undefined, 'typescript must be installed; the gate fails closed without it');

const rulesFor = (source, name = 'probe.tsx') => scanSource(ts, source, name).map((finding) => finding.rule);
const findingsFor = (source, name = 'probe.tsx') => scanSource(ts, source, name);

// ---------------------------------------------------------------------------
// 1. every rule has been seen to fire
// ---------------------------------------------------------------------------

test('every declared rule fires on a source written the way the mistake is written', () => {
  const fixtures = {
    // `<button>Save</button>` — the single most common way copy escapes.
    'jsx-text': 'export const A = () => <button type="button">Save</button>;',
    // Multi-line prose, which is the dominant shape in this repo and the one a
    // `>text<` regex on one line never sees.
    'jsx-text:multiline':
      'export const A = () => (\n  <p>\n    The app is missing required configuration and cannot start.\n  </p>\n);',
    // `<Alert title="Activity unavailable" />`
    'user-facing-prop': 'export const A = () => <Alert title="Activity unavailable" />;',
    // An aria name is copy: `aria-label="Waveform"` is read aloud by a screen
    // reader and never reaches the screen visually.
    'user-facing-prop:aria': 'export const A = () => <section aria-label="Waveform" />;',
    // Prose composed inside the attribute.
    'user-facing-prop:template': 'export const A = (id: string) => <article aria-label={`Export ${id}`} />;',
    // Copy outside JSX entirely, the category a JSX-only gate cannot see.
    'copy-fallback': "const message = error?.message ?? 'Queue depths could not be loaded.';",
    // Copy wrapped around an interpolation. A `>text<` regex on one line sees
    // nothing here, and it is the most common shape in this repo.
    'jsx-text:interpolated': 'export const A = ({ id }: { id: string }) => <p>Segment {id} is missing.</p>;',
    // A physical side in a class list. Invisible in every LTR test.
    'physical-side': 'export const A = () => <div className="ml-4 pr-2" />;',
    // Tailwind's `!` modifier pins the same physical side.
    'physical-side:important': 'export const A = () => <div className="-ml-4" />;',
    // A physical side in an inline style object.
    'physical-side:style': "export const A = () => <div style={{ marginLeft: '4px' }} />;",
    // A physical side in a stylesheet.
    'physical-side:css': '.dp-row { margin-left: 4px; }',
  };

  for (const [id, source] of Object.entries(fixtures)) {
    const rule = id.split(':')[0];
    const name = id.endsWith('css') ? 'probe.css' : 'probe.tsx';
    const fired = id.endsWith('css') ? scanCss(source, name).map((f) => f.rule) : rulesFor(source, name);
    assert.ok(fired.includes(rule), `fixture '${id}' did not trip '${rule}' (got ${JSON.stringify(fired)})`);
  }

  // The cross-check: a fixture with no rule id would assert nothing.
  for (const id of Object.keys(fixtures)) {
    assert.ok(RULES.includes(id.split(':')[0]), `fixture '${id}' names a rule outside RULES`);
  }
});

test('a new file with copy is a violation, and raising a count is a violation', () => {
  const perFile = {
    'features/a/One.tsx': [],
    'features/b/Two.tsx': [
      { rule: 'jsx-text', line: 3, detail: 'Save' },
      { rule: 'jsx-text', line: 9, detail: 'Cancel' },
    ],
  };
  const baseline = { rules: { 'jsx-text': 2 }, files: { 'features/b/Two.tsx': { 'jsx-text': 2 } } };

  assert.deepEqual(diffAgainstBaseline(perFile, baseline).violations, []);

  const withNewFile = { ...perFile, 'features/c/Three.tsx': [{ rule: 'jsx-text', line: 1, detail: 'Save' }] };
  const newFileViolations = diffAgainstBaseline(withNewFile, baseline).violations;
  assert.equal(newFileViolations.length, 1);
  assert.equal(newFileViolations[0].file, 'features/c/Three.tsx');
  assert.equal(newFileViolations[0].allowed, 0);

  const raised = { ...perFile, 'features/b/Two.tsx': [...perFile['features/b/Two.tsx'], { rule: 'jsx-text', line: 12, detail: 'x' }] };
  const raisedViolations = diffAgainstBaseline(raised, baseline).violations;
  assert.equal(raisedViolations.length, 1);
  assert.equal(raisedViolations[0].found, 3);
  assert.equal(raisedViolations[0].allowed, 2);
});

test('a count that goes DOWN is progress, never a failure', () => {
  const perFile = { 'features/b/Two.tsx': [{ rule: 'jsx-text', line: 3, detail: 'Save' }] };
  const baseline = { rules: { 'jsx-text': 9 }, files: { 'features/b/Two.tsx': { 'jsx-text': 9 } } };
  const { violations } = diffAgainstBaseline(perFile, baseline);
  assert.deepEqual(violations, []);
});

test('strict mode ignores the baseline entirely', () => {
  const perFile = { 'features/b/Two.tsx': [{ rule: 'jsx-text', line: 3, detail: 'Save' }] };
  const baseline = { rules: { 'jsx-text': 9 }, files: { 'features/b/Two.tsx': { 'jsx-text': 9 } } };
  assert.deepEqual(diffAgainstBaseline(perFile, baseline, { strict: true }).violations.length, 1);
});

test('a baseline whose totals disagree with its per-file map is drift, not a pass', () => {
  // The green-from-nothing failure: the per-file map is fine, so a violations-only
  // check would report zero findings and exit 0 while the totals said something
  // else entirely.
  const perFile = { 'features/b/Two.tsx': [{ rule: 'jsx-text', line: 3, detail: 'Save' }] };
  const complete = Object.fromEntries(RULES.map((rule) => [rule, 0]));
  const baseline = { rules: { ...complete, 'jsx-text': 41 }, files: { 'features/b/Two.tsx': { 'jsx-text': 1 } } };
  const result = diffAgainstBaseline(perFile, baseline);
  assert.equal(result.violations.length, 0);
  assert.deepEqual(result.drift, ['per-rule total for jsx-text: baseline 41 vs scanned 1']);
});

// ---------------------------------------------------------------------------
// 2. every rule has been seen to stay silent on correct code
// ---------------------------------------------------------------------------

test('the correct patterns produce no findings', () => {
  const clean = [
    // The point of the whole task.
    'export const A = () => { const { t } = useTranslation(); return <button title={t("exports:actions.download")}>{t("exports:actions.download")}</button>; };',
    // A translation key still reads as letters. Without a JSX-text-aware scan
    // this is the shape that produces false positives everywhere.
    'export const A = () => <h1>{t("dashboard:title")}</h1>;',
    // Pluralised copy through i18next.
    'export const A = () => <span>{t("common:items", { count })}</span>;',
    // String-valued attributes that are NOT prose.
    'export const A = () => <input className="dp-input" data-testid="x" id="y" type="text" role="combobox" aria-expanded="false" aria-controls="listbox" aria-labelledby="label" />;',
    // Logical properties everywhere. `rounded-lg` must not match `rounded-l`.
    'export const A = () => <div className="ms-4 me-2 ps-4 pe-2 start-0 end-0 text-start border-x rounded-lg rounded-full float-none mx-auto" />;',
    // Tailwind's `!` modifier on a logical property is still logical.
    'export const A = () => <div className="-ms-4" />;',
    // A plain style object with no physical side.
    "export const A = () => <div style={{ marginInlineStart: '4px', textAlign: 'start' }} />;",
    // Domain values, not prose: identifiers, routes, codes.
    'export const A = () => <p>{row.code} {row.status} {row.entityId}</p>;',
    "const heading = 'Overview';",
    "const titleCount = 3;",
    // A fallback whose text is not prose (an enum value).
    "const message = error?.code ?? 'not_found';",
    // A two-word fallback is a status, not a sentence.
    "const message = error?.message ?? 'Not ready';",
  ];
  for (const source of clean) {
    const findings = findingsFor(source);
    assert.deepEqual(findings, [], `expected no findings for: ${source}`);
  }
});

test('a single word of JSX text is still copy', () => {
  // `On` / `Off` / `Save` are one word and one of the most commonly
  // untranslated strings in an operator UI, so `jsx-text` deliberately has no
  // word-count floor. The floor lives on `copy-fallback`, where a one-word
  // *fallback* is far more likely to be a status code.
  assert.deepEqual(rulesFor('export const A = () => <span>On</span>;'), ['jsx-text']);
  assert.deepEqual(rulesFor('export const A = () => <span>3 items</span>;'), ['jsx-text']);
});

test('the physical-side rules match the physical side and nothing adjacent to it', () => {
  const shouldMatch = ['ml-4', 'mr-2', 'pl-1', 'pr-8', 'left-0', 'right-full', 'border-l', 'border-r-2', 'rounded-l', 'rounded-r-lg', 'text-left', 'text-right', 'float-left', 'clear-right'];
  const shouldNotMatch = ['ms-4', 'me-2', 'ps-1', 'pe-8', 'start-0', 'end-full', 'border-x', 'rounded-lg', 'rounded-full', 'text-start', 'text-center', 'float-none', 'mx-auto', 'pl-[3px]'];
  for (const token of shouldMatch) {
    assert.ok(PHYSICAL_SIDE_CLASSES.some((re) => re.test(token)), `${token} should be gated`);
  }
  for (const token of shouldNotMatch) {
    assert.ok(!PHYSICAL_SIDE_CLASSES.some((re) => re.test(token)), `${token} should NOT be gated`);
  }
});

test('the css physical-side rules match declarations, not prose that mentions them', () => {
  assert.equal(scanCss('.a { margin-inline-start: 4px; }', 'a.css').length, 0);
  assert.equal(scanCss('.a { margin-left: 4px; }', 'a.css').length, 1);
  assert.equal(scanCss('.a { inset-inline-start: 0; }', 'a.css').length, 0);
  assert.equal(scanCss('.a { text-align: start; }', 'a.css').length, 0);
  assert.equal(scanCss('.a { text-align: right; }', 'a.css').length, 1);
  assert.equal(scanCss('.a { border-start-start-radius: 4px; }', 'a.css').length, 0);
  assert.equal(scanCss('.a { border-top-left-radius: 4px; }', 'a.css').length, 1);
  // A comment mentioning the property is not a declaration. (The `check:no-hex`
  // gate went red on exactly this class of mistake — a hex in a comment — so
  // the rule is asserted rather than assumed.)
  assert.equal(scanCss('/* we used to write margin-left here */\n.a { color: red; }', 'a.css').length, 0);
  assert.ok(PHYSICAL_SIDE_CSS.length >= 5);
});

test('the gated prop list excludes the high-volume string attributes', () => {
  for (const name of ['className', 'data-testid', 'id', 'type', 'role', 'value', 'name', 'to', 'htmlFor', 'href', 'placeholder-x']) {
    assert.ok(!USER_FACING_PROPS.includes(name), `${name} must not be gated`);
  }
  // The `aria-*` set is the name-carrying members only.
  assert.ok(ARIA_NAME_ATTRS.includes('aria-label'));
  for (const name of ['aria-hidden', 'aria-live', 'aria-expanded', 'aria-controls', 'aria-labelledby', 'aria-describedby']) {
    assert.ok(!ARIA_NAME_ATTRS.includes(name), `${name} must not be gated`);
  }
});

// ---------------------------------------------------------------------------
// 3. the real tree
// ---------------------------------------------------------------------------

test('the exemption list is exactly the documented one', () => {
  assert.deepEqual(Object.keys(EXEMPT_PATHS).sort(), ['api', 'components', 'config', 'i18n', 'mocks', 'telemetry', 'types']);
  for (const [key, reason] of Object.entries(EXEMPT_PATHS)) {
    assert.ok(reason.length > 20, `exemption '${key}' needs a reason, not a dash`);
  }
  // Every exemption must still be a directory that exists. An exemption for a
  // path nobody has is either a typo or a leftover, and both are how the scan
  // quietly stops covering something.
  for (const key of Object.keys(EXEMPT_PATHS)) {
    assert.ok(existsDirectory(join(FRONTEND_SRC, key)), `exempted path '${key}' does not exist in frontend/src`);
  }
});

test('the scan actually covers the tree it claims to', () => {
  const files = listScannableFiles();
  // The floor the CLI enforces, asserted here so the two cannot drift.
  assert.ok(files.length > MIN_SCANNABLE_FILES, `the CLI would report COPY_GATE_INPUT_MISSING at ${files.length} files`);
  // A subject list derived from the thing being checked is how a gate passes on
  // an empty set. These floors are the assertion that the list is not empty.
  assert.ok(files.length >= 150, `expected a real subject list, got ${files.length} files`);
  assert.ok(files.includes('features/enrichment/VideoIntelPanel.tsx'));
  assert.ok(files.includes('app/layouts/AppShell.tsx'));
  // And the exemptions must actually be excluded.
  assert.ok(!files.some((file) => file.startsWith('i18n/')));
  assert.ok(!files.some((file) => file.startsWith('components/')));
  assert.ok(!files.some((file) => file.startsWith('api/')));
  assert.ok(!files.some((file) => file.endsWith('.test.tsx') || file.endsWith('.spec.tsx')));
  assert.ok(listScannableCssFiles().includes('styles/rtl.css'));
});

test('a tree that cannot be read yields no subject list, not a clean one', () => {
  // `listScannableFiles` swallowing a readdir error and returning `[]` is the
  // behaviour the CLI's `COPY_GATE_INPUT_MISSING` guard exists for. Assert the
  // empty list so the guard's precondition is not a guess.
  assert.deepEqual(listScannableFiles('/nonexistent/frontend/src'), []);
  assert.deepEqual(listScannableCssFiles('/nonexistent/frontend/src'), []);
});

test('the committed tree is clean against the committed ratchet', () => {
  const { files, perFile } = scanTree(ts);
  const baseline = readBaseline();
  const { violations, drift, actualTally } = diffAgainstBaseline(perFile, baseline);
  assert.deepEqual(drift, [], `baseline drift: ${drift.join('; ')}`);
  assert.deepEqual(
    violations.map((violation) => `${violation.file} ${violation.found}>${violation.allowed}`),
    [],
  );
  assert.ok(files.length >= 150);

  // The ratchet must be non-trivial: a baseline of zero against a tree the
  // scanner reports hundreds of findings from would mean the comparison is not
  // doing what it says.
  const baselined = Object.values(baseline.files).reduce(
    (sum, perRule) => sum + Object.values(perRule).reduce((a, b) => a + b, 0),
    0,
  );
  assert.ok(baselined > 100, `expected real recorded debt, got ${baselined}`);
  assert.equal(baselined, actualTally.total);

  // Every rule id in the baseline is a rule this module implements.
  for (const rule of Object.keys(baseline.rules)) {
    assert.ok(RULES.includes(rule), `baseline names unknown rule '${rule}'`);
  }
});

test('the RTL half of the gate holds a REAL zero', () => {
  // `physical-side` is the only rule whose recorded baseline is zero. That is
  // the whole point of adding it: Task 016 wrote the stylesheet with logical
  // properties, and the single `text-left` in `ConfigErrorScreen.tsx` was fixed
  // by this task. If this test ever needs a baseline entry, the RTL foundation
  // has started to rot and the zero is no longer evidence of anything.
  const baseline = readBaseline();
  assert.deepEqual(baseline.rules['physical-side'], 0);
  const { perFile } = scanTree(ts);
  const offenders = Object.entries(perFile)
    .filter(([, findings]) => findings.some((finding) => finding.rule === 'physical-side'))
    .map(([file]) => file);
  assert.deepEqual(offenders, []);
});

test('the rtl stylesheet is loaded by the app, not just committed', () => {
  // An `@import` that nothing pulls in is a stylesheet that does not exist at
  // runtime, which is exactly how a committed-but-unused RTL layer passes.
  const index = readFileSync(join(FRONTEND_SRC, 'styles', 'index.css'), 'utf8');
  assert.match(index, /@import\s+"\.\/rtl\.css"/);
});

test('buildBaseline and the committed baseline describe the same tree', () => {
  // Guards against a hand-edited baseline drifting away from what the scanner
  // sees, in the direction that matters (an inflated count silently absorbing
  // new copy).
  const { perFile } = scanTree(ts);
  const rebuilt = buildBaseline(perFile);
  const committed = readBaseline();
  assert.deepEqual(rebuilt.rules, committed.rules);
  assert.deepEqual(rebuilt.files, committed.files);
  assert.equal(committed.$comment.includes('write-baseline'), true);
});

test('tally counts every rule this module declares', () => {
  const perFile = {
    'a.tsx': [{ rule: 'jsx-text' }, { rule: 'physical-side' }],
    'b.tsx': [{ rule: 'jsx-text' }, { rule: 'copy-fallback' }],
  };
  const counts = tally(perFile);
  assert.equal(counts.total, 4);
  for (const rule of RULES) {
    assert.equal(typeof counts.rules[rule], 'number', `rule ${rule} missing from tally`);
  }
  assert.equal(counts.rules['jsx-text'], 2);
});

test('the baseline file lives where the CLI reads it from', () => {
  assert.equal(BASELINE_PATH, join(REPO_ROOT, 'scripts', 'hardcoded-copy-baseline.json'));
});

// Small helper kept local so the exemption test above reads cleanly.
function existsDirectory(path) {
  try {
    return statSync(path).isDirectory();
  } catch {
    return false;
  }
}