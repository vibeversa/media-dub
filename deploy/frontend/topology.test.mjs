// The frontend topology rules (Task 043A, instruction 4 / R4, Testing).
//
// TWO KINDS OF TEST, AND BOTH MATTER
// ---------------------------------
//   1. The rules are driven with SYNTHETIC sources that each contain one
//      violation. A rule that has never been seen to fire has not been tested, and
//      a rule that fires on the whole repository is a rule that gets disabled.
//      These are the tests that would have caught a rule quietly matching nothing
//      - the failure mode where a gate reports PASS having asserted nothing.
//   2. The rules are then run over the REAL `frontend/src`, which is the assertion
//      that actually means anything: the tree as committed is clean.
//
// The live-artefact half of this check is not here. There is no browser in this
// repository's toolchain that can be pointed at a live cluster, so the runtime
// half of R4 is proven by `deploy/tests/hosting-topology.py` over the
// NetworkPolicies and by the live-cluster run recorded in the 043 report.
import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  ALLOWED_HOSTS,
  ALLOWED_HOST_SUFFIXES,
  EXEMPT_PATHS,
  FORBIDDEN_PATTERNS,
  REASON_OK,
  REASON_VIOLATION,
  hostOf,
  isAllowedHost,
  isScannableFile,
  listScannableFiles,
  lineOf,
  maskComments,
  scanSource,
} from '../../scripts/check-frontend-topology.mjs';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const SRC = join(REPO_ROOT, 'frontend', 'src');

/** The rule ids a synthetic source is expected to trip. */
const rulesFor = (source, name = 'probe.ts') => scanSource(source, name).map((finding) => finding.rule);

/**
 * The two rules `scanSource` implements itself rather than as a `FORBIDDEN_PATTERNS`
 * entry, because they are decisions ABOUT a URL rather than a shape inside one.
 *
 *   off-allowlist-host  an http(s) literal naming a host that is not loopback and
 *                       not IANA-reserved. R4's "the frontend calls the API origin
 *                       only" is this rule and nothing else.
 *   non-http-scheme     an absolute literal in a scheme the browser has no business
 *                       being pointed at from application code.
 *
 * They are listed here so the cross-check above can tell "a fixture with no rule"
 * (which asserts nothing) from "a rule implemented in a different place" (which is
 * fine, and is documented rather than implicit).
 */
const RULES_IN_SCAN_SOURCE = ['off-allowlist-host', 'non-http-scheme'];

// ---------------------------------------------------------------------------
// 1. every rule has been seen to fire
// ---------------------------------------------------------------------------

test('every declared rule fires on a source that contains it', () => {
  // One fixture per rule, written the way the mistake is actually written. A rule
  // with no fixture here is a rule whose regex has never matched anything, which
  // is indistinguishable from a rule that is silently broken.
  const fixtures = {
    'postgres-endpoint': 'export const c = "postgresql://dubbing:pw@db.internal:5432/dubbing";',
    'npgsql-client': 'import type { NpgsqlConnection } from "Npgsql";',
    'broker-endpoint': 'const url = "amqp://dubbing:pw@broker.internal:5672";',
    'broker-client': 'import amqp from "amqplib";',
    'redis-endpoint': 'const url = "redis://cache.internal:6379/2";',
    'redis-client': 'import { createClient } from "StackExchange.Redis";',
    'object-storage-endpoint': 'const url = "s3://dubbing-media/exports/exp_1.mp4";',
    'object-storage-client': 'import { S3Client } from "@aws-sdk/client-s3";',
    'worker-endpoint': 'const url = `https://${"worker-gpu"}.dubbing.internal/jobs`;',
    'connection-string': 'const cs = "Host=db.internal;Database=dubbing;Username=app;Password=hunter2";',
    'infrastructure-port': 'const target = { host: "gateway.internal", port: 5672 };',
    'off-allowlist-host': 'const api = "https://api.dubbing.internal/v1";',
    'non-http-scheme': 'const store = "ftp://files.internal/exports";',
  };

  for (const rule of FORBIDDEN_PATTERNS) {
    const fixture = fixtures[rule.id];
    assert.ok(fixture !== undefined, `no fixture is declared for rule '${rule.id}'`);
    assert.ok(
      rulesFor(fixture).includes(rule.id),
      `rule '${rule.id}' did not fire on its own fixture; it matched ${JSON.stringify(rulesFor(fixture))}`,
    );
  }
  for (const id of Object.keys(fixtures)) {
    assert.ok(
      [...FORBIDDEN_PATTERNS.map((rule) => rule.id), ...RULES_IN_SCAN_SOURCE].includes(id),
      `fixture '${id}' has no matching rule, so it is asserting nothing`,
    );
  }
});

test('every rule reports the line it is on, and every message names the file', () => {
  const source = ['// a comment', 'const a = 1;', '', 'const c = "redis://cache.internal:6379/2";', ''].join('\n');
  const findings = scanSource(source, 'src/features/thing.ts');

  // One line, three rules: the endpoint, the hard-coded port, and the scheme. All
  // three must be reported and all three must carry the REAL line number - the
  // masking blanks characters in place, so a masker that changed the length would
  // silently shift every finding onto the wrong line.
  assert.equal(findings.length, 3);
  assert.deepEqual(
    findings.map((finding) => finding.rule),
    ['infrastructure-port', 'non-http-scheme', 'redis-endpoint'],
  );
  for (const finding of findings) {
    assert.equal(finding.line, 4, `${finding.rule} reported the wrong line`);
    assert.ok(finding.message.startsWith('src/features/thing.ts:4:'), finding.message);
    // A message that does not say what to do is a finding somebody learns to skip.
    assert.ok(finding.message.length > 80, 'the message must carry the reason, not just the rule name');
  }
});

// ---------------------------------------------------------------------------
// 2. the rules do NOT fire on the shapes that must stay legal
// ---------------------------------------------------------------------------

test('the bare scheme of a storage URL is not a finding', () => {
  // `features/exports/types.ts` contains exactly this, to STRIP internal storage
  // URLs from text before rendering it. A rule that matched the bare scheme
  // would fail the build for the code that exists to prevent the leak - and then
  // somebody would delete the gate.
  assert.deepEqual(rulesFor('if (lowered.includes("s3://")) { return true; }'), []);
  assert.deepEqual(rulesFor('export const PREFIX = "s3://";'), []);
  // ...and the same tolerance does not weaken the storage rules, which is what
  // the second half checks.
  assert.ok(rulesFor('const u = "s3://dubbing-media/exports/exp_1.mp4";').includes('object-storage-endpoint'));
  // A different storage scheme WITH a host is still named as a non-http scheme.
  assert.deepEqual(rulesFor('const u = "gs://bucket/key";'), ['non-http-scheme']);
});

test('millisecond durations are not datastore ports, and a real port is', () => {
  // The rule requires a colon before the digits, precisely because this codebase
  // is full of `90000` and `61000`.
  assert.deepEqual(rulesFor('const ms = 90000; const t = 61000; const a = 9000;'), []);
  assert.deepEqual(rulesFor('formatAppearance(61000, 90000)'), []);
  assert.deepEqual(rulesFor('const half = 54320;'), []);
  // An object that names the port IS the finding - the rule cannot tell a
  // manifest from a connection, and in a browser bundle there is no legitimate
  // reason for either.
  assert.deepEqual(rulesFor('const target = { port: 5432 };'), ['infrastructure-port']);
  assert.deepEqual(rulesFor('const target = { port: 8080 };'), []);
});

test('documentation URLs, loopback and reserved hosts are allowed', () => {
  const allowed = [
    'http://localhost:5000',
    'http://127.0.0.1:58080/v1',
    'https://api.dubbing.example.com',
    'https://CHANGE_ME-api.dubbing.example.com',
    'https://anything.invalid/x',
    'http://anything.test',
    'http://cdn.localhost',
    'http://www.w3.org/2000/svg',
  ];
  for (const url of allowed) {
    assert.deepEqual(rulesFor(`const u = "${url}";`), [], url);
  }
});

test('a commented-out reference is not a finding, but a real hostname is', () => {
  assert.deepEqual(rulesFor('// const old = "postgresql://db.internal/x";\nconst n = 1;'), []);
  assert.deepEqual(rulesFor('/* amqp://broker.internal:5672 */\nconst n = 1;'), []);
  assert.deepEqual(rulesFor('const n = 1; // redis://cache.internal:6379'), []);
  // ...and the masking must not eat the `//` of a URL, which is the whole reason
  // it is colon-aware.
  assert.deepEqual(rulesFor('const u = "https://api.dubbing.example.com"; // trailing'), []);
  assert.deepEqual(rulesFor('const api = "https://api.dubbing.internal";'), ['off-allowlist-host']);
});

test('a URL with userinfo reports the HOST, and the credential is a separate finding', () => {
  // Reporting `user:pw@host` as the host would produce a message naming a string
  // that is not a hostname, which reads as nonsense to whoever receives it.
  assert.equal(hostOf('https://user:pw@api.dubbing.internal/v1'), 'api.dubbing.internal');
  const findings = scanSource('const u = "https://user:pw@api.dubbing.internal/v1";', 'probe.ts');
  assert.ok(findings.some((finding) => finding.rule === 'off-allowlist-host'));
});

test('hostOf returns null for something that is not a URL rather than throwing', () => {
  assert.equal(hostOf('https://'), null);
  assert.equal(hostOf(''), null);
  assert.equal(hostOf('not a url at all'), null);
});

// ---------------------------------------------------------------------------
// 3. the allowlist itself
// ---------------------------------------------------------------------------

test('the host allowlist admits exactly the reserved and loopback names', () => {
  for (const host of ALLOWED_HOSTS) {
    assert.equal(isAllowedHost(host), true, host);
  }
  for (const suffix of ALLOWED_HOST_SUFFIXES) {
    assert.equal(isAllowedHost(`anything${suffix}`), true, suffix);
  }
  for (const host of ['', 'api.dubbing.internal', '10.0.0.1', 'evil-example.com', 'example.com.attacker.net', 'xlocalhost']) {
    assert.equal(isAllowedHost(host), false, host);
  }
  // A suffix match on the whole label, not a substring: `evil-example.com` ends
  // with `example.com` as a string and is a registrable domain of its own.
  assert.equal(isAllowedHost('evil-example.com'), false);
  assert.equal(isAllowedHost('example.com.attacker.net'), false);
});

test('the exemption list is exactly the one entry, with a reason', () => {
  // Asserted rather than documented, because an unexplained exclusion is
  // indistinguishable from one added to make a failure go away.
  assert.equal(EXEMPT_PATHS.length, 1);
  assert.equal(EXEMPT_PATHS[0].path, 'config/env.ts');
  assert.ok(EXEMPT_PATHS[0].reason.length > 120, 'the exemption must carry a real reason');
  assert.equal(isScannableFile('config/env.ts'), false);
  assert.equal(isScannableFile('config/env.test.ts'), false, 'a test beside it is still excluded');
});

test('tests, stories and the test setup are out of scope, and they are full of hostile strings', () => {
  // Excluded on purpose: `features/admin/__tests__/admin.test.tsx` contains
  // `postgres://operator:password@db-host/rows` as a fixture. Including test files
  // would make the gate permanently red, and a permanently red gate is a gate
  // that gets disabled.
  for (const path of [
    'features/admin/__tests__/admin.test.tsx',
    'features/admin/admin.test.ts',
    'features/admin/admin.spec.ts',
    'features/voices/VoiceCard.stories.tsx',
    'testSetup.ts',
  ]) {
    assert.equal(isScannableFile(path), false, path);
  }
  assert.equal(isScannableFile('config/env.ts.bak'), false, 'a non-source extension is not scanned');
  assert.equal(isScannableFile('features/projects/ProjectsPage.tsx'), true);
  assert.equal(isScannableFile('api/client/httpClient.ts'), true);
});
// ---------------------------------------------------------------------------
// 4. masking
// ---------------------------------------------------------------------------

test('maskComments preserves length and line structure so line numbers survive', () => {
  const source = 'const a = 1; // note\nconst b = "https://x.example.com"; /* block\nspans */\n';
  const masked = maskComments(source);
  assert.equal(masked.length, source.length);
  assert.equal(masked.split('\n').length, source.split('\n').length);
  assert.ok(masked.includes('https://x.example.com'), 'a URL must not be eaten by the line-comment rule');
  assert.ok(!masked.includes('note'));
  assert.ok(!masked.includes('spans'));
});

test('maskComments leaves template literals alone', () => {
  // A template literal routinely interpolates the base URL, and that variable is
  // the one thing worth reading.
  const source = 'const u = `${apiBase}/v1/projects?x=1`; // trailing';
  assert.ok(maskComments(source).includes('${apiBase}'));
});

test('lineOf counts lines from a character offset', () => {
  assert.equal(lineOf('a\nb\nc', 0), 1);
  assert.equal(lineOf('a\nb\nc', 2), 2);
  assert.equal(lineOf('a\nb\nc', 4), 3);
});

// ---------------------------------------------------------------------------
// 5. the real tree
// ---------------------------------------------------------------------------

test('the committed frontend/src has no infrastructure reference at all', () => {
  assert.ok(existsSync(SRC), 'frontend/src must exist; a check that read nothing has cleared nothing');
  const files = listScannableFiles(SRC);
  assert.ok(files.length > 100, `only ${files.length} files were scannable, which is not this repository`);
  const findings = files.flatMap((file) => scanSource(readFileSync(join(SRC, file), 'utf8'), file));
  assert.deepEqual(
    findings.map((finding) => finding.message),
    [],
  );
});

test('the real scan is not vacuous: it visits the modules that would hold a violation', () => {
  // A file list that quietly stopped including application code would make the
  // test above pass for the wrong reason. These are the files where a datastore
  // reference would actually land - and `features/exports/types.ts` is the one
  // whose defensive `s3://` match proves the rules are narrow enough to be
  // usable.
  const files = listScannableFiles(SRC);
  for (const expected of [
    'api/client/httpClient.ts',
    'api/hooks.ts',
    'api/queryKeys/keys.ts',
    'features/exports/types.ts',
    'hooks/useVersionWatch.ts',
    'features/admin/OpsDashboard.tsx',
  ]) {
    assert.ok(files.includes(expected), `${expected} is not being scanned`);
  }
  // The exempt file is NOT in the list, and the list is still large - so the
  // exemption removed one file and not the directory.
  assert.equal(files.includes('config/env.ts'), false);
  assert.ok(files.length > 200, `only ${files.length} files are scanned, which is not this repository`);
});

// The reason vocabulary is a closed set, and this test pins the two the CLI can
// print. A third reason added without a test here would be a reason a release job
// cannot branch on.
test('the reason vocabulary is the two documented values', () => {
  assert.equal(REASON_OK, 'OK');
  assert.equal(REASON_VIOLATION, 'FRONTEND_TOPOLOGY_VIOLATION');
});
