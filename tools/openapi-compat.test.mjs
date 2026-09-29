// Tests for the contract-compatibility analysis (Task 042).
//
// The gate in `scripts/openapi-diff.sh` is I/O; these cover the two decisions it
// cannot be trusted to make itself, and they cover the failure modes that turn
// a contract gate green:
//
//   * a documentation-only edit must NOT demand a version bump (otherwise people
//     bump versions reflexively and the check stops carrying information);
//   * ANY structural edit with no bump MUST fail;
//   * a breaking change MUST fail even when the version was bumped correctly
//     (a bump does not buy permission to break v1 clients);
//   * an unparseable version MUST fail rather than degrade to "no opinion";
//   * the version comparison MUST be numeric (`v10` > `v9`);
//   * oasdiff's changing JSON envelope MUST keep parsing.
import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  REASON,
  canonicalContract,
  compareVersions,
  contractChanged,
  documentVersion,
  evaluateVersionPolicy,
  isBlockingLevel,
  levelName,
  normalizeChanges,
  parseOasdiffOutput,
  parseVersion,
  renderBreakingReport,
  renderChangeAnnotations,
  routeMajor,
  suggestVersion,
} from './openapi-compat.mjs';

/** A minimal but structurally faithful document. */
function doc(overrides = {}) {
  return {
    openapi: '3.0.3',
    info: { title: 'Dubbing Platform API', version: 'v1', description: 'long prose' },
    servers: [{ url: '/api/v1', description: 'Versioned API root' }],
    security: [{ Bearer: [] }],
    paths: {
      '/projects': {
        get: {
          operationId: 'listProjects',
          summary: 'List projects',
          description: 'Cross-tenant ids return 404.',
          responses: { '200': { description: 'ok' } },
        },
      },
    },
    components: {
      securitySchemes: { Bearer: { type: 'http', scheme: 'bearer', description: 'JWT' } },
      schemas: {
        ProjectStatus: { type: 'string', enum: ['draft', 'processing', 'completed'] },
        Project: {
          type: 'object',
          required: ['id', 'name'],
          properties: { id: { type: 'string' }, name: { type: 'string' } },
        },
      },
    },
    ...overrides,
  };
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

test('key order and documentation edits are not contract changes', () => {
  const reordered = doc();
  // Rebuild with every object key inserted in reverse order: a byte comparison
  // would call this a change and demand a version bump.
  const reverseKeys = (value) => {
    if (Array.isArray(value)) {
      return value.map(reverseKeys);
    }
    if (value === null || typeof value !== 'object') {
      return value;
    }
    const out = {};
    for (const key of Object.keys(value).sort().reverse()) {
      out[key] = reverseKeys(value[key]);
    }
    return out;
  };
  const same = reverseKeys(reordered);
  assert.equal(contractChanged(reordered, same), false, 'key order must not read as a change');

  const reworded = clone(reordered);
  reworded.info.description = 'a completely different paragraph';
  reworded.paths['/projects'].get.summary = 'Renamed summary';
  reworded.paths['/projects'].get.description = 'Reworded.';
  reworded.components.schemas.Project.properties.id.description = 'The id.';
  assert.equal(contractChanged(reordered, reworded), false, 'documentation is not the contract');

  assert.equal(canonicalContract(reordered), canonicalContract(same));
});

test('adding a path, a schema, a property and an enum value are all changes', () => {
  const base = doc();

  const addedPath = clone(base);
  addedPath.paths['/voices'] = { get: { operationId: 'listVoices', responses: { '200': { description: 'ok' } } } };
  assert.equal(contractChanged(base, addedPath), true, 'a new path is a change');

  const addedProperty = clone(base);
  addedProperty.components.schemas.Project.properties.status = { $ref: '#/components/schemas/ProjectStatus' };
  assert.equal(contractChanged(base, addedProperty), true, 'a new optional property is a change');

  const addedRequired = clone(base);
  addedRequired.components.schemas.Project.required = ['id', 'name', 'status'];
  assert.equal(contractChanged(base, addedRequired), true, 'a new required field is a change');

  const addedEnum = clone(base);
  addedEnum.components.schemas.ProjectStatus.enum = ['draft', 'processing', 'completed', 'failed'];
  assert.equal(contractChanged(base, addedEnum), true, 'a new enum value is a change');

  const addedSchema = clone(base);
  addedSchema.components.schemas.Voice = { type: 'object', properties: { id: { type: 'string' } } };
  assert.equal(contractChanged(base, addedSchema), true, 'a new schema is a change');
});

test('an x- extension is contract, not documentation', () => {
  const base = doc();
  const extended = clone(base);
  extended.components.schemas.Project['x-max-length'] = { name: 200 };
  assert.equal(contractChanged(base, extended), true, 'an x- extension can be a client-visible constraint');
});

test('reordering a required list is a change; reordering enum values is a change', () => {
  const base = doc();
  const reorderedRequired = clone(base);
  reorderedRequired.components.schemas.Project.required = ['name', 'id'];
  assert.equal(contractChanged(base, reorderedRequired), true, 'required is emitted in order by the generator');

  const reorderedEnum = clone(base);
  reorderedEnum.components.schemas.ProjectStatus.enum = ['completed', 'processing', 'draft'];
  assert.equal(contractChanged(base, reorderedEnum), true, 'enum order is client-visible');
});

test('versions parse and compare numerically, not lexically', () => {
  assert.deepEqual(parseVersion('v1'), { major: 1, minor: 0, patch: 0, raw: 'v1' });
  assert.deepEqual(parseVersion('v1.2.3'), { major: 1, minor: 2, patch: 3, raw: 'v1.2.3' });
  assert.deepEqual(parseVersion('1.4'), { major: 1, minor: 4, patch: 0, raw: '1.4' });
  assert.equal(parseVersion('vNext'), null);
  assert.equal(parseVersion('1.2.3-rc1'), null);
  assert.equal(parseVersion(undefined), null);
  assert.equal(parseVersion(7), null);

  assert.equal(compareVersions('v10', 'v9'), 1, 'lexical comparison would call this a downgrade');
  assert.equal(compareVersions('v9', 'v10'), -1);
  assert.equal(compareVersions('v1', 'v1'), 0);
  assert.equal(compareVersions('v1.1', 'v1.2'), -1);
  assert.equal(compareVersions('v2', 'v10'), -1);
  assert.equal(compareVersions('v1', 'nonsense'), null);
});

test('an unchanged contract at an unchanged version passes', () => {
  const base = doc();
  const result = evaluateVersionPolicy(base, clone(base));
  assert.equal(result.status, REASON.OK);
  assert.equal(result.changed, false);
  assert.deepEqual(result.annotations, []);
});

test('a contract change with no version bump fails, and names the version to use', () => {
  const base = doc();
  const head = clone(base);
  head.paths['/voices'] = { get: { operationId: 'listVoices', responses: { '200': { description: 'ok' } } } };
  const result = evaluateVersionPolicy(base, head);
  assert.equal(result.status, REASON.VERSION_BUMP_REQUIRED);
  assert.equal(result.annotations.length, 1);
  assert.match(result.annotations[0], /still v1/);
  assert.match(result.annotations[0], /v1\.1/);
  assert.equal(suggestVersion('v1', false), 'v1.1');
  assert.equal(suggestVersion('v1', true), 'v2');
});

test('a contract change with a bump passes, and a breaking change still fails elsewhere', () => {
  const base = doc();
  const head = clone(base);
  head.info.version = 'v1.1';
  head.paths['/voices'] = { get: { operationId: 'listVoices', responses: { '200': { description: 'ok' } } } };
  const result = evaluateVersionPolicy(base, head, { breaking: true });
  assert.equal(result.status, 'bumped', 'the version policy passes; the breaking diff is a separate verdict');
  assert.equal(result.breaking, true);
});

test('a version that moves with no contract change warns instead of blocking', () => {
  const base = doc();
  const head = clone(base);
  head.info.version = 'v1.1';
  const result = evaluateVersionPolicy(base, head);
  assert.equal(result.status, 'spurious');
  assert.match(result.annotations[0], /contract is unchanged/);
  // The asymmetry is the point and is asserted both ways: documentation is not
  // contract, so a reworded description is a "no change" - and a version that
  // moved on its own is a warning, not a block, because it breaks nobody.
  const reworded = clone(base);
  reworded.info.description = 'Reworded entirely.';
  assert.equal(evaluateVersionPolicy(base, reworded).status, REASON.OK);
});

test('an unparseable version fails closed rather than losing its opinion', () => {
  const base = doc();
  const head = clone(base);
  head.info.version = 'vNext';
  const result = evaluateVersionPolicy(base, head);
  assert.equal(result.status, REASON.VERSION_INVALID);
  assert.match(result.annotations[0], /must look like/);
});

test('a version downgrade fails', () => {
  const base = doc();
  base.info.version = 'v2';
  const head = doc();
  head.info.version = 'v1';
  const result = evaluateVersionPolicy(base, head);
  assert.equal(result.status, REASON.VERSION_REGRESSED);
  assert.match(result.annotations[0], /backwards/);
});

test('a new major must move the route prefix, and the route prefix must move the major', () => {
  const base = doc();
  const head = clone(base);
  head.info.version = 'v2';
  head.paths['/voices'] = {};
  const majorOnly = evaluateVersionPolicy(base, head);
  assert.equal(majorOnly.status, REASON.MAJOR_WITHOUT_ROUTE, 'a v2 document behind a v1 route is incoherent');
  assert.match(majorOnly.annotations[0], /new route prefix/);

  const headWithRoute = clone(head);
  headWithRoute.servers[0].url = '/api/v2';
  assert.equal(evaluateVersionPolicy(base, headWithRoute).status, 'bumped');

  // A moved route with no version bump at all is caught earlier and more
  // usefully: "you changed the contract and did not bump" names the fix
  // directly, and reporting only the incoherence would send the author looking
  // for a route problem they do not have.
  const routeOnly = clone(base);
  routeOnly.servers[0].url = '/api/v2';
  routeOnly.paths['/voices'] = {};
  const unbumped = evaluateVersionPolicy(base, routeOnly);
  assert.equal(unbumped.status, REASON.VERSION_BUMP_REQUIRED);
  assert.match(unbumped.annotations[0], /still v1/);

  // A minor bump alongside a route move is the genuinely confusing case: the
  // author did bump, so the generic advice is silent, but the document now
  // claims v1.5 while serving a v2 route.
  const minorBump = clone(routeOnly);
  minorBump.info.version = 'v1.5';
  const result = evaluateVersionPolicy(base, minorBump);
  assert.equal(result.status, REASON.ROUTE_WITHOUT_MAJOR);
  assert.match(result.annotations[0], /v1 -> v2/);
});

test('routeMajor reads the versioned server url and tolerates a missing one', () => {
  assert.equal(routeMajor(doc()), 1);
  const relative = doc({ servers: [{ url: '/' }] });
  assert.equal(routeMajor(relative), null);
  const trailing = doc({ servers: [{ url: '/api/v2/' }] });
  assert.equal(routeMajor(trailing), 2, 'a trailing slash must not hide the version segment');
  assert.equal(routeMajor(doc({ servers: [] })), null);
  assert.equal(routeMajor({}), null);
});

test('documentVersion reads info.version and survives a document without info', () => {
  assert.equal(documentVersion(doc()), 'v1');
  assert.equal(documentVersion({}), undefined);
});

test('oasdiff levels are numbers, and an unreadable level is treated as the worst', () => {
  // Verified against the pinned build (v1.11.7): a removed path serialises as
  // `"level":3` while the text formatter prints "error". A parser that looked
  // for the string would classify every real break as level-less.
  assert.equal(levelName(3), 'ERR');
  assert.equal(levelName(2), 'WARN');
  assert.equal(levelName(1), 'INFO');
  assert.equal(levelName('ERR'), 'ERR');
  assert.equal(levelName('warn'), 'WARN');

  // The defaults are all ERR on purpose: a severity this gate cannot read must
  // never be downgraded, because a breaking change quietly reclassified as
  // unclassified is the silent-green failure the tool pin exists to prevent.
  assert.equal(levelName(99), 'ERR', 'an unknown numeric level is not a warning');
  assert.equal(levelName('chatty'), 'ERR', 'an unknown string level is not a warning');
  assert.equal(levelName(undefined), 'ERR', 'a missing level is not a warning');
  assert.equal(levelName(null), 'ERR');

  assert.equal(isBlockingLevel('ERR'), true);
  assert.equal(isBlockingLevel('WARN'), true, 'oasdiff can emit warnings from `breaking`; a warning still blocks');
  assert.equal(isBlockingLevel('INFO'), false);
});

test('the real oasdiff payload for a removed path is understood', () => {
  // Captured verbatim from `oasdiff breaking base head --format json` on a
  // document with `DELETE|GET|PATCH /projects/{projectId}` removed. This is the
  // shape the pinned tool actually emits, which is a bare array of flat objects
  // with numeric levels - not the nested envelope the older releases used.
  const payload = [
    { id: 'api-path-removed-without-deprecation', text: 'api path removed without deprecation', level: 3, operation: 'DELETE', operationId: 'deleteProject', path: '/projects/{projectId}', source: 'head.json', section: 'paths' },
    { id: 'api-path-removed-without-deprecation', text: 'api path removed without deprecation', level: 3, operation: 'GET', operationId: 'getProject', path: '/projects/{projectId}', source: 'head.json', section: 'paths' },
    { id: 'api-path-removed-without-deprecation', text: 'api path removed without deprecation', level: 3, operation: 'PATCH', operationId: 'patchProject', path: '/projects/{projectId}', source: 'head.json', section: 'paths' },
  ];
  const changes = parseOasdiffOutput(JSON.stringify(payload)).changes;
  assert.equal(changes.length, 3, 'three methods on one removed path are three real breaks, not one');
  assert.equal(changes[0].level, 'ERR');
  assert.equal(changes[0].path, '/projects/{projectId}');
  assert.match(changes[0].operation, /^(DELETE|GET|PATCH) \/projects\/\{projectId\}$/);
});

test('the real oasdiff payload for a tightened request enum is understood', () => {
  const payload = [
    { id: 'request-property-enum-value-removed', text: "removed the enum value 'de' of the request property 'sourceLanguage'", level: 3, operation: 'POST', operationId: 'createProject', path: '/projects', source: 'b.json', section: 'paths' },
    { id: 'request-property-enum-value-removed', text: "removed the enum value 'es' of the request property 'sourceLanguage'", level: 3, operation: 'POST', operationId: 'createProject', path: '/projects', source: 'b.json', section: 'paths' },
  ];
  const changes = normalizeChanges(payload);
  assert.equal(changes.length, 2, 'the text differs, so these are not duplicates');
  assert.ok(changes.every((c) => isBlockingLevel(c.level)));
});

test('oasdiff JSON envelopes parse whichever shape the tool shipped', () => {
  // Shape A: the current envelope, a nested per-severity object.
  const nested = {
    diff: {
      breaking: {
        deleted: [
          { id: 'api-path-removed-without-deprecation', level: 'ERR', text: 'api path removed: /api/v1/voices', operationPath: 'GET /api/v1/voices' },
        ],
        err: [
          { id: 'request-became-enum', level: 'ERR', text: 'added the new required property status', component: 'Project' },
        ],
      },
    },
  };
  const fromNested = parseOasdiffOutput(JSON.stringify(nested));
  assert.equal(fromNested.format, 'json');
  assert.deepEqual(fromNested.changes.map((c) => c.id), ['api-path-removed-without-deprecation', 'request-became-enum']);
  assert.equal(fromNested.changes[0].operation, 'GET /api/v1/voices');
  assert.equal(fromNested.changes[1].component, 'Project');

  // Shape B: a bare array.
  const bare = parseOasdiffOutput(JSON.stringify([{ id: 'api-path-removed-without-deprecation', level: 'ERR', text: 'x' }]));
  assert.equal(bare.changes.length, 1);

  // Shape C: `operation` instead of `operationPath`, and a change with no `text`.
  const renamed = parseOasdiffOutput(JSON.stringify([{ id: 'some-id', level: 'ERR', operation: 'POST /api/v1/x' }]));
  assert.equal(renamed.changes[0].text, 'some-id', 'a change with no text still has an id to show');
  assert.equal(renamed.changes[0].operation, 'POST /api/v1/x');

  // Shape D: not JSON at all, e.g. `--format text`.
  const text = parseOasdiffOutput('incompatible change, removed path /api/v1/voices\nincompatible change, deleted schema');
  assert.equal(text.format, 'text');
  assert.equal(text.changes.length, 2);
  assert.match(text.changes[0].text, /removed path/);

  // Shape E: nothing found.
  assert.deepEqual(parseOasdiffOutput('').changes, []);
  assert.deepEqual(parseOasdiffOutput(undefined).changes, []);
});

test('the same change reported twice is annotated once', () => {
  const payload = {
    paths: [{ id: 'api-path-removed-without-deprecation', level: 'ERR', text: 'api path removed: /api/v1/voices' }],
    components: [{ id: 'api-path-removed-without-deprecation', level: 'ERR', text: 'api path removed: /api/v1/voices' }],
  };
  assert.equal(normalizeChanges(payload).length, 1);
});

test('changes are sorted so a reordering in the tool cannot reorder the report', () => {
  const payload = [
    { id: 'z-last', level: 'ERR', text: 'z' },
    { id: 'a-first', level: 'ERR', text: 'a' },
  ];
  assert.deepEqual(normalizeChanges(payload).map((c) => c.id), ['a-first', 'z-last']);
});

test('annotations escape the characters that would break a workflow command', () => {
  const lines = renderChangeAnnotations([
    { id: 'api-path-removed', level: 'ERR', text: 'removed: /api/v1/a,b:c\nsecond line', operation: 'GET /api/v1/a,b' },
  ]);
  assert.equal(lines.length, 1);
  assert.ok(lines[0].startsWith('::error file='));
  assert.ok(!lines[0].includes('\n'), 'an annotation must stay on one line');
  assert.match(lines[0], /title=api-path-removed%20\(GET%20\/api\/v1\/a%2Cb\)/, 'commas and spaces in the title are escaped');
  assert.match(lines[0], /%0A/, 'a newline in the message is percent-escaped');
  // The message body is escaped for newlines and percents only, so a route with
  // a comma stays readable - escaping it there would corrupt the path a reader
  // is meant to copy into a `curl`.
  assert.match(lines[0], /\[ERR\] removed: \/api\/v1\/a,b:c%0Asecond line$/);
});

test('annotations are capped, and the cap is itself visible', () => {
  const many = Array.from({ length: 60 }, (_, i) => ({ id: `id-${i}`, level: 'ERR', text: `change ${i}`, operation: null }));
  const lines = renderChangeAnnotations(many);
  assert.equal(lines.length, 51, '50 shown plus one truncation notice');
  assert.match(lines[50], /truncated/);
  assert.match(lines[50], /10 further change/);
});

test('the breaking report is a table, and pipes in a change cannot break it', () => {
  const report = renderBreakingReport([
    { id: 'api-path-removed', level: 'ERR', text: 'removed: /a|b', operation: 'GET /a', component: null },
  ]);
  const rows = report.split('\n').filter((line) => line.startsWith('| `') || line.startsWith('| api-path'));
  assert.equal(rows.length, 1);
  assert.match(rows[0], /\\\|/, 'a pipe inside a cell is escaped');
  assert.match(report, /new major route prefix/);
});
