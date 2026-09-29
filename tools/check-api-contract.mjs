#!/usr/bin/env node
// Contract-truth gate (Task 041A).
//
// WHY THIS EXISTS
// ---------------
// `check-api-drift.mjs` verifies that the generated TypeScript client matches the
// committed OpenAPI bundle. It does NOT verify that the bundle describes the API.
// The bundle at `src/DubbingPlatform.Api/OpenApi/openapi.v1.json` is hand-authored,
// so the two can - and do - disagree, and the drift check is perfectly happy while
// the frontend calls a contract the server does not implement.
//
// That is not hypothetical. `AuthLoginRequest` in the bundle is
// `{ tenantSlug, email, password }`; the API's `LoginRequest` is
// `{ tenantId, externalSubject }`. The generated client sends the former, the API
// answers 400, and no user can sign in - while every existing gate passes.
//
// This gate closes that hole: it fetches the API's OWN emitted document
// (`GET /openapi/v1.json`, mapped anonymously by Program.cs) and diffs it against
// the committed bundle, operation by operation.
//
// USAGE
//   node tools/check-api-contract.mjs [--url http://127.0.0.1:58080]
//   API_CONTRACT_URL=http://127.0.0.1:58080 node tools/check-api-contract.mjs
//
// Requires a running API. Fails closed when the API is unreachable: a contract
// gate that silently passes because it could not reach the server is worse than
// no gate, because it reports a clean bill of health for an unverified contract.
//
// NOT WIRED INTO A SUITE YET, deliberately. The repository is currently
// divergent, so turning this into a hard gate would turn every other task's
// suite red. The intended sequence is: regenerate the bundle from the server,
// confirm this gate is green, then wire it into CI. See the 041A report.
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { readFileSync } from 'node:fs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..');
const BUNDLE_PATH = path.join(repoRoot, 'src', 'DubbingPlatform.Api', 'OpenApi', 'openapi.v1.json');
const DEFAULT_URL = 'http://127.0.0.1:58080';
const ROUTE_PREFIX = '/api/v1';
const HTTP_METHODS = ['get', 'put', 'post', 'delete', 'patch', 'options', 'head', 'trace'];

/**
 * Resolves a schema `$ref` chain to its concrete schema.
 *
 * @param {object} document an OpenAPI document
 * @param {object|undefined} node a schema node, possibly a `$ref`
 * @returns {object|null} the resolved schema, or null when it cannot be resolved
 */
export function resolveSchema(document, node) {
  let current = node;
  const seen = new Set();
  while (current !== undefined && current !== null && typeof current === 'object' && '$ref' in current) {
    const name = String(current.$ref).split('/').pop();
    if (seen.has(name)) {
      return null;
    }
    seen.add(name);
    current = document.components?.schemas?.[name];
  }
  return current !== null && typeof current === 'object' ? current : null;
}

/**
 * Extracts the comparable shape of an operation's JSON request body.
 *
 * Returns null when the operation declares no JSON body, which is a meaningful
 * value: "takes no body" and "declares no body in the document" are different
 * facts and the caller compares them.
 *
 * @param {object} document an OpenAPI document
 * @param {string} pathKey a path key as spelled in this document
 * @param {string} method a lower-case HTTP method
 */
export function requestShape(document, pathKey, method) {
  const operation = document.paths?.[pathKey]?.[method];
  if (operation === undefined) {
    return undefined;
  }
  const media = operation.requestBody?.content?.['application/json'];
  if (media === undefined) {
    return null;
  }
  const schema = resolveSchema(document, media.schema);
  if (schema === null) {
    return { name: media.schema?.$ref !== undefined ? String(media.schema.$ref).split('/').pop() : '(inline)', required: null, properties: null };
  }
  return {
    name: media.schema?.$ref !== undefined ? String(media.schema.$ref).split('/').pop() : '(inline)',
    required: [...(schema.required ?? [])].sort(),
    properties: Object.keys(schema.properties ?? {}).sort(),
  };
}

/**
 * Compares the server's emitted document against the committed bundle.
 *
 * Two documents describing the same API legitimately differ in how they spell a
 * path, so the committed bundle is expected to omit the `/api/v1` route prefix
 * that the live document carries. Keys are aligned on the prefix-stripped form.
 *
 * @param {object} serverDocument the API's own emitted document
 * @param {object} bundleDocument the committed bundle
 */
export function compareDocuments(serverDocument, bundleDocument) {
  const divergences = [];
  const undeclared = [];
  let compared = 0;

  for (const serverPath of Object.keys(serverDocument.paths ?? {}).sort()) {
    const bundlePath = serverPath.startsWith(ROUTE_PREFIX)
      ? serverPath.slice(ROUTE_PREFIX.length) || '/'
      : serverPath;

    for (const method of HTTP_METHODS) {
      if (serverDocument.paths[serverPath][method] === undefined) {
        continue;
      }
      const serverShape = requestShape(serverDocument, serverPath, method);
      const bundleShape = requestShape(bundleDocument, bundlePath, method);
      if (serverShape === null && bundleShape === null) {
        continue;
      }
      if (serverShape === undefined && bundleShape === undefined) {
        continue;
      }
      if (bundleShape === undefined) {
        undeclared.push(`${method.toUpperCase()} ${serverPath} (not declared in the bundle)`);
        continue;
      }
      compared += 1;
      if (serverShape === null || bundleShape === null) {
        divergences.push({ operation: `${method.toUpperCase()} ${serverPath}`, reason: 'a JSON request body is declared on only one side', server: serverShape, bundle: bundleShape });
        continue;
      }
      const sameRequired = JSON.stringify(serverShape.required) === JSON.stringify(bundleShape.required);
      const sameProperties = JSON.stringify(serverShape.properties) === JSON.stringify(bundleShape.properties);
      if (!sameRequired || !sameProperties) {
        divergences.push({
          operation: `${method.toUpperCase()} ${serverPath}`,
          reason: !sameRequired && !sameProperties ? 'different required fields and different properties' : !sameRequired ? 'different required fields' : 'different properties',
          server: serverShape,
          bundle: bundleShape,
        });
      }
    }
  }

  return { compared, divergences, undeclared };
}

function describe(shape) {
  if (shape === null) {
    return 'no JSON body';
  }
  if (shape.required === null) {
    return `${shape.name} (unresolvable schema)`;
  }
  return `${shape.name} required=${JSON.stringify(shape.required)} properties=${JSON.stringify(shape.properties)}`;
}

export function formatReport(result) {
  const lines = [];
  lines.push(`operations with a JSON request body compared: ${result.compared}`);
  lines.push(`divergent: ${result.divergences.length}`);
  lines.push(`declared in the server but absent from the bundle: ${result.undeclared.length}`);
  for (const entry of result.divergences) {
    lines.push('');
    lines.push(`  ${entry.operation}`);
    lines.push(`    reason: ${entry.reason}`);
    lines.push(`    server: ${describe(entry.server)}`);
    lines.push(`    bundle: ${describe(entry.bundle)}`);
  }
  for (const entry of result.undeclared) {
    lines.push(`  MISSING ${entry}`);
  }
  return lines.join('\n');
}

function parseUrl(argv) {
  const flag = argv.find((a) => a.startsWith('--url='));
  if (flag !== undefined) {
    return flag.slice('--url='.length);
  }
  const index = argv.indexOf('--url');
  if (index >= 0 && argv[index + 1] !== undefined) {
    return argv[index + 1];
  }
  return process.env['API_CONTRACT_URL'] ?? DEFAULT_URL;
}

/**
 * Runs the gate and reports the result.
 *
 * Sets `process.exitCode` and returns rather than calling `process.exit()`.
 * A forced exit while an async handle is still closing trips a libuv assertion
 * on Windows (`Assertion failed: !(handle->flags & UV_HANDLE_CLOSING)`, exit
 * 0xC0000409) after the message has been printed - so the gate reports the
 * divergence and then dies of a native crash instead of exiting 1, which is a
 * failure mode a CI step cannot interpret.
 *
 * @returns {Promise<number>} 0 when the bundle matches the API, 1 otherwise
 */
async function main() {
  const baseUrl = parseUrl(process.argv.slice(2)).replace(/\/+$/, '');
  const url = `${baseUrl}/openapi/v1.json`;

  let serverDocument;
  try {
    // `AbortSignal.timeout` does not keep the event loop alive, so the gate exits
    // as soon as the comparison is done rather than lingering for the timeout.
    const response = await fetch(url, { signal: AbortSignal.timeout(30_000) });
    if (!response.ok) {
      console.error(`check-api-contract: ${url} returned ${response.status}.`);
      console.error('  The API must be running for this gate: it compares the bundle against the');
      console.error("  server's own emitted document, not against a checked-in copy.");
      return 1;
    }
    serverDocument = await response.json();
  } catch (error) {
    console.error(`check-api-contract: could not fetch ${url}: ${error instanceof Error ? error.message : String(error)}`);
    console.error('  Start the API, e.g. `docker compose -f tests/cross-layer/docker-compose.cross.yml up -d`.');
    console.error('  This gate fails closed on purpose: an unreachable server must not read as a pass.');
    return 1;
  }

  let bundleDocument;
  try {
    bundleDocument = JSON.parse(readFileSync(BUNDLE_PATH, 'utf8'));
  } catch (error) {
    console.error(`check-api-contract: could not read ${path.relative(repoRoot, BUNDLE_PATH)}: ${error instanceof Error ? error.message : String(error)}`);
    return 1;
  }

  const result = compareDocuments(serverDocument, bundleDocument);
  console.log(formatReport(result));

  if (result.divergences.length > 0 || result.undeclared.length > 0) {
    console.error('');
    console.error('API CONTRACT DIVERGENCE: the committed OpenAPI bundle does not describe this API.');
    console.error('  The generated client is faithful to the bundle, so every consumer of it inherits the');
    console.error('  divergence. Regenerate the bundle from the server document, then regenerate the client.');
    return 1;
  }
  console.log('');
  console.log('check-api-contract: the committed bundle matches the API on every JSON request body.');
  return 0;
}

if (process.argv[1] !== undefined && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = await main();
}
