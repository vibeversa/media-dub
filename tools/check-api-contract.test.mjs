// Tests for the contract-truth gate's comparison logic (Task 041A).
//
// The gate is only worth having if it is itself verified, so these cover the two
// ways it can silently become useless: resolving `$ref` chains, and aligning the
// committed bundle's prefix-less paths with the server document's prefixed ones.
// The real repository currently FAILS this gate by design - that divergence is the
// finding, not a test failure - so the cases here are driven by fixtures.
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { compareDocuments, formatReport, requestShape, resolveSchema } from './check-api-contract.mjs';

function documentWith(paths, schemas) {
  return { openapi: '3.0.1', paths, components: { schemas: schemas ?? {} } };
}

function operationWithBody(refName) {
  return {
    requestBody: {
      content: { 'application/json': { schema: { $ref: `#/components/schemas/${refName}` } } },
    },
  };
}

test('resolveSchema follows a $ref chain and stops on a cycle', () => {
  const document = documentWith({}, {
    A: { $ref: '#/components/schemas/B' },
    B: { type: 'object', required: ['x'], properties: { x: { type: 'string' } } },
    Loop1: { $ref: '#/components/schemas/Loop2' },
    Loop2: { $ref: '#/components/schemas/Loop1' },
  });

  assert.deepEqual(resolveSchema(document, { $ref: '#/components/schemas/A' }).required, ['x']);
  // A cyclic reference must not hang the gate.
  assert.equal(resolveSchema(document, { $ref: '#/components/schemas/Loop1' }), null);
});

test('requestShape reports no JSON body distinctly from a missing operation', () => {
  const document = documentWith(
    { '/api/v1/thing': { post: { requestBody: { content: { 'application/json': { schema: { $ref: '#/components/schemas/R' } } } } }, get: {} } },
    { R: { type: 'object', required: ['b', 'a'], properties: { b: {}, a: {} } } },
  );

  const withBody = requestShape(document, '/api/v1/thing', 'post');
  assert.deepEqual(withBody.required, ['a', 'b'], 'required fields are sorted for stable comparison');
  assert.deepEqual(withBody.properties, ['a', 'b']);

  assert.equal(requestShape(document, '/api/v1/thing', 'get'), null, 'an operation with no body is null');
  assert.equal(requestShape(document, '/api/v1/absent', 'post'), undefined, 'a missing operation is undefined');
});

test('identical documents compare clean, and order of required fields is not a difference', () => {
  const server = documentWith(
    { '/api/v1/auth/login': { post: operationWithBody('LoginRequest') } },
    { LoginRequest: { type: 'object', required: ['tenantId', 'externalSubject'], properties: { tenantId: {}, externalSubject: {} } } },
  );
  const bundle = documentWith(
    { '/auth/login': { post: operationWithBody('AuthLoginRequest') } },
    { AuthLoginRequest: { type: 'object', required: ['externalSubject', 'tenantId'], properties: { externalSubject: {}, tenantId: {} } } },
  );

  const result = compareDocuments(server, bundle);
  assert.equal(result.divergences.length, 0, formatReport(result));
  assert.equal(result.undeclared.length, 0);
  assert.equal(result.compared, 1, 'the route prefix is aligned, not treated as a difference');
});

test('the real login defect is detected: disjoint field sets', () => {
  const server = documentWith(
    { '/api/v1/auth/login': { post: operationWithBody('LoginRequest') } },
    { LoginRequest: { type: 'object', required: ['tenantId', 'externalSubject'], properties: { tenantId: {}, externalSubject: {} } } },
  );
  const bundle = documentWith(
    { '/auth/login': { post: operationWithBody('AuthLoginRequest') } },
    { AuthLoginRequest: { type: 'object', required: ['tenantSlug', 'email', 'password'], properties: { tenantSlug: {}, email: {}, password: {} } } },
  );

  const result = compareDocuments(server, bundle);
  assert.equal(result.divergences.length, 1);
  assert.match(result.divergences[0].operation, /POST \/api\/v1\/auth\/login/);
  assert.deepEqual(result.divergences[0].server.required, ['externalSubject', 'tenantId']);
  assert.deepEqual(result.divergences[0].bundle.required, ['email', 'password', 'tenantSlug']);
});

test('a different required set is reported even when properties match', () => {
  const server = documentWith(
    { '/api/v1/x': { post: operationWithBody('A') } },
    { A: { type: 'object', required: ['a'], properties: { a: {}, b: {} } } },
  );
  const bundle = documentWith(
    { '/x': { post: operationWithBody('B') } },
    { B: { type: 'object', required: ['a', 'b'], properties: { a: {}, b: {} } } },
  );

  const result = compareDocuments(server, bundle);
  assert.equal(result.divergences.length, 1);
  assert.equal(result.divergences[0].reason, 'different required fields');
});

test('an operation the bundle omits is reported as undeclared, not as a body mismatch', () => {
  const server = documentWith(
    { '/api/v1/only/on/server': { post: operationWithBody('A') } },
    { A: { type: 'object', properties: { a: {} } } },
  );
  const bundle = documentWith({}, {});

  const result = compareDocuments(server, bundle);
  assert.equal(result.divergences.length, 0);
  assert.equal(result.undeclared.length, 1);
  assert.match(result.undeclared[0], /POST \/api\/v1\/only\/on\/server/);
});

test('a body declared on only one side is reported as such', () => {
  const server = documentWith(
    { '/api/v1/x': { post: operationWithBody('A') } },
    { A: { type: 'object', properties: { a: {} } } },
  );
  const bundle = documentWith({ '/x': { post: {} } }, {});

  const result = compareDocuments(server, bundle);
  assert.equal(result.divergences.length, 1);
  assert.equal(result.divergences[0].reason, 'a JSON request body is declared on only one side');
});

test('operations with no body on either side are not compared', () => {
  const server = documentWith({ '/api/v1/x': { get: {}, post: {} } }, {});
  const bundle = documentWith({ '/x': { get: {}, post: {} } }, {});

  const result = compareDocuments(server, bundle);
  assert.equal(result.compared, 0);
  assert.equal(result.divergences.length, 0);
});

test('formatReport names the operation and both shapes', () => {
  const server = documentWith(
    { '/api/v1/auth/login': { post: operationWithBody('LoginRequest') } },
    { LoginRequest: { type: 'object', required: ['tenantId'], properties: { tenantId: {} } } },
  );
  const bundle = documentWith(
    { '/auth/login': { post: operationWithBody('AuthLoginRequest') } },
    { AuthLoginRequest: { type: 'object', required: ['email'], properties: { email: {} } } },
  );

  const report = formatReport(compareDocuments(server, bundle));
  assert.match(report, /POST \/api\/v1\/auth\/login/);
  assert.match(report, /LoginRequest/);
  assert.match(report, /AuthLoginRequest/);
});
