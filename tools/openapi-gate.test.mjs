// Tests for the contract-gate decision layer (Task 042).
//
// `tools/openapi-compat.test.mjs` covers the individual rules. These cover the
// *precedence* between them, which is where a gate most often goes quietly
// wrong: a PR that is both breaking and unbumped must report the breaking part,
// and a tool whose output stopped parsing must fail the gate rather than be
// treated as "found nothing".
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { EXIT, decide } from './openapi-gate.mjs';
import { REASON, contractChanged } from './openapi-compat.mjs';

function doc(overrides = {}) {
  return {
    openapi: '3.0.3',
    info: { title: 'Dubbing Platform API', version: 'v1', description: 'prose' },
    servers: [{ url: '/api/v1', description: 'root' }],
    paths: { '/projects': { get: { operationId: 'listProjects', responses: { '200': { description: 'ok' } } } } },
    components: {
      schemas: {
        Project: { type: 'object', required: ['id'], properties: { id: { type: 'string' } } },
      },
    },
    ...overrides,
  };
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

const clean = { changes: [], format: 'json' };
const broken = {
  format: 'json',
  changes: [
    { id: 'api-path-removed-without-deprecation', level: 'ERR', text: 'api path removed: /api/v1/voices', operation: 'GET /api/v1/voices', component: null },
  ],
};

test('an unchanged document passes with a PASS line and no annotations', () => {
  const result = decide(doc(), clone(doc()), clean);
  assert.equal(result.exitCode, EXIT.OK);
  assert.equal(result.reason, REASON.OK);
  assert.deepEqual(result.annotations, []);
  assert.match(result.report, /The contract is unchanged/);
  assert.equal(result.summary.contractChanged, false);
});

test('an additive change with a version bump passes', () => {
  const head = clone(doc());
  head.info.version = 'v1.1';
  head.paths['/voices'] = { get: { operationId: 'listVoices', responses: { '200': { description: 'ok' } } } };
  const result = decide(doc(), head, clean);
  assert.equal(result.exitCode, EXIT.OK);
  assert.equal(result.reason, REASON.OK);
  assert.match(result.report, /The contract changed\. Version v1 -> v1\.1\./);
  assert.equal(contractChanged(doc(), head), true);
});

test('an additive change with NO version bump fails and says what to do', () => {
  const head = clone(doc());
  head.paths['/voices'] = { get: { operationId: 'listVoices', responses: { '200': { description: 'ok' } } } };
  const result = decide(doc(), head, clean);
  assert.equal(result.exitCode, EXIT.BUMP_REQUIRED);
  assert.equal(result.reason, REASON.VERSION_BUMP_REQUIRED);
  assert.equal(result.annotations.length, 1);
  assert.match(result.annotations[0], /::error/);
  assert.match(result.annotations[0], /v1\.1/);
  // The fix must name the file, because "bump the version" without a path
  // produces a second CI run.
  assert.match(result.annotations[0], /openapi\.v1\.json/);
});

test('breaking outranks an unbumped version: a PR that is both reports the break', () => {
  // No version bump AND a removed path. Both are true; only one can be the
  // headline, and it is the one that breaks clients.
  const head = clone(doc());
  delete head.paths['/projects'];
  const result = decide(doc(), head, broken);
  assert.equal(result.exitCode, EXIT.BREAKING);
  assert.equal(result.reason, REASON.BREAKING);
  assert.ok(result.annotations.length >= 2, 'a summary annotation plus the change itself');
  assert.match(result.annotations[0], /1 breaking change\(s\) between main and HEAD/);
  assert.ok(result.annotations.some((line) => line.includes('api-path-removed-without-deprecation')));
  assert.equal(result.report.includes('## OpenAPI breaking changes'), true);
});

test('a bumped version does not buy permission to break v1 clients', () => {
  const head = clone(doc());
  head.info.version = 'v2';
  head.servers[0].url = '/api/v2';
  delete head.paths['/projects'];
  const result = decide(doc(), head, broken);
  assert.equal(result.exitCode, EXIT.BREAKING, 'the version policy passed; the break still fails');
  assert.equal(result.version.status, 'bumped');
});

test('oasdiff output that is not the pinned JSON format fails the gate', () => {
  // The dangerous case: a tool that stopped emitting parseable JSON used to mean
  // "zero changes" to a naive parser, and a breaking merge went green.
  const result = decide(doc(), clone(doc()), { changes: broken.changes, format: 'text' });
  assert.equal(result.exitCode, EXIT.GATE_ERROR);
  assert.equal(result.reason, REASON.DIFF_UNREADABLE);
  assert.match(result.annotations[0], /did not return the pinned --format json/);
  assert.match(result.annotations[0], /before trusting this run/);
});

test('an unparseable version fails the gate even with no breaking change', () => {
  const head = clone(doc());
  head.info.version = 'vNext';
  head.paths['/voices'] = {};
  const result = decide(doc(), head, clean);
  assert.equal(result.exitCode, EXIT.GATE_ERROR);
  assert.equal(result.reason, REASON.VERSION_INVALID);
});

test('a version downgrade fails', () => {
  const base = doc();
  base.info.version = 'v2';
  base.servers[0].url = '/api/v2';
  const result = decide(base, doc(), clean);
  assert.equal(result.exitCode, EXIT.BUMP_REQUIRED);
  assert.equal(result.reason, REASON.VERSION_REGRESSED);
  assert.match(result.annotations[0], /backwards/);
});

test('a spurious bump warns and does not block', () => {
  const head = clone(doc());
  head.info.version = 'v1.1';
  const result = decide(doc(), head, clean);
  assert.equal(result.exitCode, EXIT.OK, 'a bump with no change breaks nobody');
  assert.equal(result.reason, REASON.OK);
  assert.equal(result.annotations.length, 1);
  assert.match(result.annotations[0], /^::warning/);
});

test('the fingerprint changes when the verdict changes and is stable otherwise', () => {
  const head = clone(doc());
  head.info.version = 'v1.1';
  head.paths['/voices'] = {};
  const first = decide(doc(), head, clean);
  const second = decide(doc(), clone(head), clean);
  assert.equal(first.summary.fingerprint, second.summary.fingerprint, 'the same inputs give the same fingerprint');
  assert.match(first.summary.fingerprint, /^[0-9a-f]{64}$/);

  const brokenRun = decide(doc(), head, broken);
  assert.notEqual(brokenRun.summary.fingerprint, first.summary.fingerprint, 'a break must not share a fingerprint with a pass');
});

test('a truncated annotation set is bounded but the report is not', () => {
  const many = Array.from({ length: 200 }, (_, i) => ({ id: `id-${i}`, level: 'ERR', text: `change ${i}`, operation: null, component: null }));
  const head = clone(doc());
  head.info.version = 'v2';
  head.servers[0].url = '/api/v2';
  delete head.paths['/projects'];
  const result = decide(doc(), head, { changes: many, format: 'json' });
  const errorLines = result.annotations.filter((line) => line.startsWith('::error'));
  assert.equal(errorLines.length, 52, '50 changes, one summary, one truncation notice');
  const tableRows = result.report.split('\n').filter((line) => line.startsWith('| `id-'));
  assert.equal(tableRows.length, 200, 'the attached report still names every change');
});

test('every failure reason is a documented constant, so a workflow can key on it', () => {
  assert.equal(REASON.OK, 'OK');
  assert.equal(REASON.BREAKING, 'CONTRACT_BREAKING');
  assert.equal(REASON.VERSION_BUMP_REQUIRED, 'CONTRACT_VERSION_BUMP_REQUIRED');
  assert.equal(REASON.DIFF_UNREADABLE, 'CONTRACT_DIFF_UNREADABLE');
  assert.equal(EXIT.OK, 0);
  assert.equal(EXIT.BREAKING, 1);
  assert.equal(EXIT.BUMP_REQUIRED, 2);
  assert.equal(EXIT.GATE_ERROR, 3);
});
