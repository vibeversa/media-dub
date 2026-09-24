#!/usr/bin/env node
// Hermetic TypeScript client generator (Task 014).
// Reads the versioned OpenAPI bundle and emits deterministic output to
// frontend/src/api/generated/ using Node stdlib only (no network).
// Output shape mirrors openapi-typescript-codegen (pinned in
// tools/api-generator.version): index.ts re-exports, schemas.ts holds all
// DTO/enum types, client.ts holds a typed fetch client + SSE helper.
// Any bundle validation failure exits non-zero with schema errors (never
// silent partial output). Two consecutive runs are byte-identical.
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HEADER = '/* auto-generated — do not edit; run make generate-api */';

const EXPECTED_SSE_TYPES = [
  'project.status_changed',
  'run.status_changed',
  'stage.started',
  'stage.progress',
  'stage.completed',
  'stage.failed',
  'stage.review_required',
  'review.created',
  'review.resolved',
  'export.created',
  'export.completed',
  'export.failed',
  'notification.created',
  'output.ready',
];

const REQUIRED_SCHEMAS = [
  'ErrorCode',
  'ErrorResponse',
  'PaginatedResult',
  'SseEnvelope',
  'SseEventType',
  'AdminUsageResponse',
  'AdminQuotasResponse',
];

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..');

function argValue(name, fallback) {
  const prefix = '--' + name + '=';
  for (const arg of process.argv.slice(2)) {
    if (arg.startsWith(prefix)) {
      return arg.slice(prefix.length);
    }
  }
  return fallback;
}

function fail(message) {
  console.error('generate-api: ' + message);
  process.exit(1);
}

function readManifest() {
  const text = fs.readFileSync(path.join(repoRoot, 'tools', 'api-generator.version'), 'utf8');
  const entries = {};
  for (const line of text.split('\n')) {
    const trimmed = line.trim();
    if (trimmed === '' || trimmed.startsWith('#')) {
      continue;
    }
    const eq = trimmed.indexOf('=');
    if (eq > 0) {
      entries[trimmed.slice(0, eq)] = trimmed.slice(eq + 1);
    }
  }
  return entries;
}

function loadBundle(bundlePath) {
  let raw;
  try {
    raw = fs.readFileSync(bundlePath, 'utf8');
  } catch (err) {
    fail('cannot read bundle at ' + bundlePath + ': ' + (err && err.message ? err.message : err));
  }
  let document;
  try {
    document = JSON.parse(raw);
  } catch (err) {
    fail('invalid OpenAPI bundle (JSON parse failed): ' + (err && err.message ? err.message : err));
  }
  return { raw, document };
}

function validateBundle(raw, document) {
  const problems = [];
  if (!document || typeof document !== 'object') {
    fail('invalid OpenAPI bundle: root must be an object.');
  }
  if (typeof document.openapi !== 'string' || !document.openapi.startsWith('3.')) {
    problems.push('openapi must be a 3.x version string.');
  }
  if (!document.info || document.info.version !== 'v1') {
    problems.push('info.version must be "v1".');
  }
  if (!Array.isArray(document.servers) || document.servers.length === 0) {
    problems.push('servers must list the versioned API root.');
  } else {
    for (const server of document.servers) {
      if (typeof server.url !== 'string' || !server.url.startsWith('/')) {
        problems.push('servers[].url must be relative (got ' + JSON.stringify(server.url) + ').');
      }
      if (/^https?:\/\//i.test(server.url)) {
        problems.push('servers[] must not contain absolute/internal URLs.');
      }
    }
  }
  if (!document.paths || typeof document.paths !== 'object' || Object.keys(document.paths).length === 0) {
    problems.push('paths must not be empty.');
  }
  const operationIds = new Set();
  const methods = ['get', 'post', 'put', 'patch', 'delete'];
  for (const [p, item] of Object.entries(document.paths || {})) {
    for (const m of methods) {
      const op = item[m];
      if (op === undefined) {
        continue;
      }
      if (!op.operationId || typeof op.operationId !== 'string') {
        problems.push('missing operationId for ' + m.toUpperCase() + ' ' + p + '.');
      } else if (operationIds.has(op.operationId)) {
        problems.push('duplicate operationId ' + op.operationId + '.');
      } else {
        operationIds.add(op.operationId);
      }
    }
  }
  const schemas = (document.components && document.components.schemas) || {};
  for (const name of REQUIRED_SCHEMAS) {
    if (!schemas[name]) {
      problems.push('missing required schema ' + name + '.');
    }
  }
  const sseEnum = schemas.SseEventType && schemas.SseEventType.enum;
  if (!Array.isArray(sseEnum) || sseEnum.length !== EXPECTED_SSE_TYPES.length) {
    problems.push('SseEventType must enumerate exactly 14 event types.');
  } else {
    for (const t of EXPECTED_SSE_TYPES) {
      if (!sseEnum.includes(t)) {
        problems.push('SseEventType missing ' + t + '.');
      }
    }
  }
  const errorEnum = schemas.ErrorCode && schemas.ErrorCode.enum;
  if (!Array.isArray(errorEnum) || errorEnum.length < 65) {
    problems.push('ErrorCode must enumerate the 65-code catalog.');
  }
  if (raw.includes('Bearer eyJ')) {
    problems.push('bundle contains a real-token example (Bearer eyJ forbidden; use "***" placeholders).');
  }
  if (problems.length > 0) {
    fail('schema validation errors:\n  - ' + problems.join('\n  - '));
  }
  return { operationIds };
}

function refName(ref) {
  const prefix = '#/components/schemas/';
  if (typeof ref === 'string' && ref.startsWith(prefix)) {
    return ref.slice(prefix.length);
  }
  return null;
}

function quote(value) {
  return JSON.stringify(value);
}

// Emitted named types for inline object schemas, keyed to keep output stable.
const inlineTypes = new Map();

function registerInline(baseName, schema) {
  let name = baseName;
  let counter = 2;
  while (inlineTypes.has(name)) {
    name = baseName + counter;
    counter += 1;
  }
  inlineTypes.set(name, schema);
  return name;
}

function tsType(schema, ownerName, prefix = '') {
  if (!schema || typeof schema !== 'object') {
    return 'unknown';
  }
  if (schema.$ref) {
    const name = refName(schema.$ref);
    return name ? prefix + name : 'unknown';
  }
  if (Array.isArray(schema.enum)) {
    if (schema.enum.length === 0) {
      return 'unknown';
    }
    return schema.enum.map((v) => (typeof v === 'string' ? quote(v) : String(v))).join(' | ');
  }
  if (Array.isArray(schema.oneOf)) {
    return schema.oneOf.map((s) => tsType(s, ownerName, prefix)).join(' | ');
  }
  if (Array.isArray(schema.anyOf)) {
    return schema.anyOf.map((s) => tsType(s, ownerName, prefix)).join(' | ');
  }
  if (Array.isArray(schema.allOf)) {
    return schema.allOf.map((s) => tsType(s, ownerName, prefix)).join(' & ');
  }
  const nullable = schema.nullable === true ? ' | null' : '';
  switch (schema.type) {
    case 'string':
      return 'string' + nullable;
    case 'integer':
    case 'number':
      return 'number' + nullable;
    case 'boolean':
      return 'boolean' + nullable;
    case 'array': {
      const item = tsType(schema.items || {}, ownerName, prefix);
      return '(' + item + ')[]' + nullable;
    }
    case 'object': {
      const props = schema.properties && typeof schema.properties === 'object' ? schema.properties : null;
      if (!props || Object.keys(props).length === 0) {
        if (schema.additionalProperties && typeof schema.additionalProperties === 'object') {
          return 'Record<string, ' + tsType(schema.additionalProperties, ownerName, prefix) + '>' + nullable;
        }
        return 'Record<string, unknown>' + nullable;
      }
      const required = new Set(Array.isArray(schema.required) ? schema.required : []);
      const parts = Object.keys(props)
        .sort()
        .map((key) => {
          const optional = required.has(key) ? '' : '?';
          return '  readonly ' + quote(key) + optional + ': ' + tsType(props[key], ownerName + '_' + key, prefix) + ';';
        });
      return '{\n' + parts.join('\n') + '\n}' + nullable;
    }
    default: {
      if (schema.properties && typeof schema.properties === 'object') {
        return tsType({ ...schema, type: 'object' }, ownerName, prefix);
      }
      return 'unknown';
    }
  }
}

function emitSchemas(document) {
  const schemas = document.components.schemas;
  const names = Object.keys(schemas).sort();
  const out = [HEADER, '', '// Bundle: openapi v1. Do not hand-edit; regenerate with make generate-api.', ''];
  const emitNamed = (name, schema) => {
    const comment = schema.description ? '/** ' + schema.description + ' */\n' : '';
    if (schema.$ref || Array.isArray(schema.enum)) {
      out.push(comment + 'export type ' + name + ' = ' + tsType(schema, name) + ';', '');
      return;
    }
    const rendered = tsType(schema, name);
    if (rendered.startsWith('{') && !rendered.includes('| null')) {
      out.push(comment + 'export interface ' + name + ' ' + rendered + '', '');
    } else {
      out.push(comment + 'export type ' + name + ' = ' + rendered + ';', '');
    }
  };
  for (const name of names) {
    emitNamed(name, schemas[name]);
  }
  for (const [name, schema] of [...inlineTypes.entries()].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))) {
    emitNamed(name, schema);
  }
  return out.join('\n');
}

function firstJsonSchema(content) {
  if (!content || typeof content !== 'object') {
    return null;
  }
  const json = content['application/json'];
  if (json && json.schema) {
    return json.schema;
  }
  return null;
}

function collectOperations(document) {
  const methods = ['get', 'post', 'put', 'patch', 'delete'];
  const ops = [];
  for (const pathName of Object.keys(document.paths).sort()) {
    const item = document.paths[pathName];
    for (const method of methods) {
      const op = item[method];
      if (!op) {
        continue;
      }
      ops.push({ pathName, method, op });
    }
  }
  return ops;
}

function resolveTypeName(schema, fallbackBase, prefix = '') {
  if (!schema) {
    return 'unknown';
  }
  const direct = schema.$ref ? refName(schema.$ref) : null;
  if (direct) {
    return prefix + direct;
  }
  if (schema.type === 'object' || (schema.properties && typeof schema.properties === 'object')) {
    return prefix + registerInline(fallbackBase, schema);
  }
  return tsType(schema, fallbackBase, prefix);
}

function emitClient(document) {
  const ops = collectOperations(document);
  const serverPrefix = (document.servers && document.servers[0] && document.servers[0].url) || '/api/v1';
  const lines = [
    HEADER,
    '',
    "import type * as S from './schemas.js';",
    '',
    '/** Typed error for non-2xx API responses (uniform envelope). */',
    'export class ApiError extends Error {',
    '  public readonly status: number;',
    '  public readonly code: string;',
    '  public readonly correlationId: string;',
    "  public readonly details: Record<string, unknown>;",
    '  public constructor(status: number, code: string, message: string, correlationId: string, details: Record<string, unknown>) {',
    '    super(message);',
    '    this.name = \'ApiError\';',
    '    this.status = status;',
    '    this.code = code;',
    '    this.correlationId = correlationId;',
    '    this.details = details;',
    '  }',
    '}',
    '',
    '/** Per-request overrides (headers the bundle documents per operation). */',
    'export interface RequestOptions {',
    '  /** Idempotency-Key header for safe retries. */',
    '  readonly idempotencyKey?: string;',
    '  /** If-Match header for optimistic concurrency (PATCH projects). */',
    '  readonly ifMatch?: string;',
    '  /** Last-Event-ID header for SSE resume. */',
    '  readonly lastEventId?: string;',
    '  /** Abort a request or close a stream. */',
    '  readonly signal?: AbortSignal;',
    '}',
    '',
    'export interface ApiClientOptions {',
    '  /** Origin, e.g. https://api.example.com (server prefix /api/v1 is appended). */',
    '  readonly baseUrl: string;',
    '  /** Resolves the current bearer token (never stored or logged by the client). */',
    '  readonly getToken?: () => string | undefined;',
    '}',
    '',
    '/** One parsed SSE frame (frozen Task 013 envelope). */',
    'export interface SseFrame {',
    '  readonly id: string;',
    '  readonly event: string;',
    '  readonly data: S.SseEnvelope;',
    '}',
    '',
    '/** Typed fetch client generated from openapi v1. */',
    'export class ApiClient {',
    '  private readonly baseUrl: string;',
    '  private readonly getToken?: () => string | undefined;',
    '',
    '  public constructor(options: ApiClientOptions) {',
    '    this.baseUrl = options.baseUrl.replace(/\\/$/, \'\');',
    '    this.getToken = options.getToken;',
    '  }',
    '',
    '  private buildUrl(path: string, query?: Record<string, string | number | boolean | undefined>): string {',
    '    let url = this.baseUrl + ' + quote(serverPrefix) + ' + path;',
    '    if (query) {',
    '      const params = new URLSearchParams();',
    '      for (const key of Object.keys(query).sort()) {',
    '        const value = query[key];',
    '        if (value !== undefined) {',
    '          params.append(key, String(value));',
    '        }',
    '      }',
    '      const text = params.toString();',
    '      if (text !== \'\') {',
    "        url += '?' + text;",
    '      }',
    '    }',
    '    return url;',
    '  }',
    '',
    '  private buildHeaders(options?: RequestOptions, hasBody?: boolean): Record<string, string> {',
    '    const headers: Record<string, string> = {};',
    '    if (hasBody) {',
    "      headers['Content-Type'] = 'application/json';",
    '    }',
    '    const token = this.getToken ? this.getToken() : undefined;',
    '    if (token) {',
    "      headers['Authorization'] = 'Bearer ' + token;",
    '    }',
    '    if (options?.idempotencyKey) {',
    "      headers['Idempotency-Key'] = options.idempotencyKey;",
    '    }',
    '    if (options?.ifMatch) {',
    "      headers['If-Match'] = options.ifMatch;",
    '    }',
    '    if (options?.lastEventId) {',
    "      headers['Last-Event-ID'] = options.lastEventId;",
    '    }',
    '    return headers;',
    '  }',
    '',
    '  private async throwForStatus(response: Response): Promise<never> {',
    '    let code = \'INTERNAL_ERROR\';',
    "    let message = 'An unexpected error occurred.';",
    "    let correlationId = '';",
    '    let details: Record<string, unknown> = {};',
    '    try {',
    '      const body = (await response.json()) as { error?: { code?: string; message?: string; correlationId?: string; details?: Record<string, unknown> } };',
    "      if (body && typeof body === 'object' && body.error) {",
    "        if (typeof body.error.code === 'string') { code = body.error.code; }",
    "        if (typeof body.error.message === 'string') { message = body.error.message; }",
    "        if (typeof body.error.correlationId === 'string') { correlationId = body.error.correlationId; }",
    "        if (body.error.details && typeof body.error.details === 'object') { details = body.error.details; }",
    '      }',
    '    } catch {',
    '      // Non-JSON failure: keep the generic envelope.',
    '    }',
    '    throw new ApiError(response.status, code, message, correlationId, details);',
    '  }',
    '',
    '  /** Low-level JSON request used by the generated methods. */',
    '  public async request<T>(method: string, path: string, args?: { query?: Record<string, string | number | boolean | undefined>; body?: unknown; options?: RequestOptions }): Promise<T> {',
    '    const response = await fetch(this.buildUrl(path, args?.query), {',
    '      method,',
    '      headers: this.buildHeaders(args?.options, args?.body !== undefined),',
    "      body: args?.body !== undefined ? JSON.stringify(args.body) : undefined,",
    '      signal: args?.options?.signal,',
    '    });',
    '    if (!response.ok) {',
    '      await this.throwForStatus(response);',
    '    }',
    '    if (response.status === 204) {',
    '      return undefined as T;',
    '    }',
    '    return (await response.json()) as T;',
    '  }',
    '',
    '  /** Low-level raw request (SSE streams, 302 downloads). Never throws for 302. */',
    '  public async requestRaw(method: string, path: string, args?: { query?: Record<string, string | number | boolean | undefined>; options?: RequestOptions }): Promise<Response> {',
    '    const response = await fetch(this.buildUrl(path, args?.query), {',
    '      method,',
    '      headers: this.buildHeaders(args?.options, false),',
    '      signal: args?.options?.signal,',
    '      redirect: \'manual\',',
    '    });',
    '    if (!response.ok && response.status !== 302 && response.type !== \'opaqueredirect\') {',
    '      await this.throwForStatus(response);',
    '    }',
    '    return response;',
    '  }',
    '',
    '  /** Open an SSE stream; resolves frames via onFrame (hint only, refetch HTTP APIs). */',
    '  public async openStream(path: string, args?: { query?: Record<string, string | number | boolean | undefined>; options?: RequestOptions; onFrame?: (frame: SseFrame) => void }): Promise<void> {',
    '    const headers = this.buildHeaders(args?.options, false);',
    "    headers['Accept'] = 'text/event-stream';",
    '    const response = await fetch(this.buildUrl(path, args?.query), { headers, signal: args?.options?.signal });',
    '    if (!response.ok || !response.body) {',
    '      await this.throwForStatus(response as Response);',
    '    }',
    '    const reader = (response.body as ReadableStream<Uint8Array>).getReader();',
    '    const decoder = new TextDecoder();',
    "    let buffer = '';",
    '    for (;;) {',
    '      const { done, value } = await reader.read();',
    '      if (done) {',
    '        break;',
    '      }',
    "      buffer += decoder.decode(value, { stream: true });",
    "      let boundary = buffer.indexOf('\\n\\n');",
    '      while (boundary >= 0) {',
    '        const chunk = buffer.slice(0, boundary);',
    "        buffer = buffer.slice(boundary + 2);",
    '        const frame = ApiClient.parseFrame(chunk);',
    '        if (frame && args?.onFrame) {',
    '          args.onFrame(frame);',
    '        }',
    "        boundary = buffer.indexOf('\\n\\n');",
    '      }',
    '    }',
    '  }',
    '',
    '  /** Parse one SSE id/event/data chunk into a typed frame. */',
    '  public static parseFrame(chunk: string): SseFrame | null {',
    "    let id = '';",
    "    let event = '';",
    "    let data = '';",
    "    for (const line of chunk.split('\\n')) {",
    "      if (line.startsWith('id:')) { id = line.slice(3).trim(); }",
    "      else if (line.startsWith('event:')) { event = line.slice(6).trim(); }",
    "      else if (line.startsWith('data:')) { data += line.slice(5).trim(); }",
    '    }',
    "    if (data === '') {",
    '      return null;',
    '    }',
    '    return { id, event, data: JSON.parse(data) as S.SseEnvelope };',
    '  }',
    '',
  ];

  for (const { pathName, method, op } of ops) {
    const opId = op.operationId;
    const rawParams = Array.isArray(op.parameters) ? op.parameters : [];
    const paramDefs = (document.components && document.components.parameters) || {};
    const params = rawParams.map((p) => {
      if (p && typeof p === 'object' && typeof p.$ref === 'string') {
        const match = /^#\/components\/parameters\/(.+)$/.exec(p.$ref);
        if (match && paramDefs[match[1]]) {
          return paramDefs[match[1]];
        }
      }
      return p;
    });
    const pathParams = params.filter((p) => p && p.in === 'path');
    const queryParams = params.filter((p) => p && p.in === 'query');
    const paramsType = opId[0].toUpperCase() + opId.slice(1) + 'Params';

    const pathFields = pathParams.map((p) => {
      const t = p.schema ? tsType(p.schema, opId + '_path_' + p.name, 'S.') : 'string';
      return '    readonly ' + quote(p.name) + ': ' + t + ';';
    });
    const queryFields = queryParams.map((p) => {
      const t = p.schema ? tsType(p.schema, opId + '_query_' + p.name, 'S.') : 'unknown';
      return '    readonly ' + quote(p.name) + '?: ' + t + ';';
    });
    // Params interface lives at module scope; collected below the class body.
    pendingParams.push({ name: paramsType, pathFields, queryFields });

    let bodyType = null;
    let bodyRequired = false;
    if (op.requestBody && op.requestBody.content) {
      const schema = firstJsonSchema(op.requestBody.content);
      if (schema) {
        bodyRequired = op.requestBody.required === true;
        bodyType = resolveTypeName(schema, opId[0].toUpperCase() + opId.slice(1) + 'Request', 'S.');
      }
    }

    const responses = op.responses || {};
    let returnType = 'unknown';
    let rawResponse = false;
    for (const status of ['200', '201', '202']) {
      const res = responses[status];
      if (res && res.content) {
        const schema = firstJsonSchema(res.content);
        if (schema) {
          returnType = resolveTypeName(schema, opId[0].toUpperCase() + opId.slice(1) + 'Response', 'S.');
        } else if (res.content['text/event-stream']) {
          rawResponse = true;
          returnType = 'Response';
        }
        break;
      }
    }
    if (returnType === 'unknown' && responses['204']) {
      returnType = 'void';
    }
    if (returnType === 'unknown' && responses['302']) {
      rawResponse = true;
      returnType = 'Response';
    }

    const summary = op.summary ? ' ' + op.summary : '';
    const paramsRequired = pathFields.length > 0 || bodyRequired;
    const paramsArg = paramsRequired ? 'params: ' + paramsType : 'params?: ' + paramsType;
    if (rawResponse) {
      lines.push(
        '  public async ' + opId + '(' + paramsArg + ', options?: RequestOptions): Promise<' + returnType + '> {',
        '    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});',
        '    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;',
        '    const filled = ' + quote(pathName) + '.replace(/\\{([^}]+)\\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));',
        "    return this.requestRaw('" + method.toUpperCase() + "', filled, { query, options });",
        '  }',
        '',
      );
    } else if (bodyType) {
      const bodyArg = bodyRequired ? 'body: ' + bodyType : 'body?: ' + bodyType;
      lines.push(
        '  /** ' + opId + ' —' + summary + ' */',
        '  public async ' + opId + '(' + paramsArg + ', ' + bodyArg + ', options?: RequestOptions): Promise<' + returnType + '> {',
        '    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});',
        '    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;',
        '    const filled = ' + quote(pathName) + '.replace(/\\{([^}]+)\\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));',
        "    return this.request<" + returnType + ">('" + method.toUpperCase() + "', filled, { query, body, options });",
        '  }',
        '',
      );
    } else {
      lines.push(
        '  /** ' + opId + ' —' + summary + ' */',
        '  public async ' + opId + '(' + paramsArg + ', options?: RequestOptions): Promise<' + returnType + '> {',
        '    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});',
        '    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;',
        '    const filled = ' + quote(pathName) + '.replace(/\\{([^}]+)\\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));',
        "    return this.request<" + returnType + ">('" + method.toUpperCase() + "', filled, { query, body: undefined, options });",
        '  }',
        '',
      );
    }
  }

  lines.push('}');
  // Module-scope params interfaces after the class.
  for (const p of pendingParams) {
    lines.push('', 'export interface ' + p.name + ' {');
    lines.push('  readonly path: {');
    if (p.pathFields.length === 0) {
      lines.push('  };');
    } else {
      lines.push(...p.pathFields, '  };');
    }
    if (p.queryFields.length > 0) {
      lines.push('  readonly query?: {');
      lines.push(...p.queryFields, '  };');
    }
    lines.push('}');
  }
  lines.push('');
  return lines.join('\n');
}

// Filled during emitClient; declared here for ordering clarity.
const pendingParams = [];

function emitIndex() {
  return [HEADER, '', "export * from './schemas.js';", "export * from './client.js';", ''].join('\n');
}

function sha256Hex(text) {
  return createHash('sha256').update(text, 'utf8').digest('hex');
}

function main() {
  const bundlePath = path.resolve(repoRoot, argValue('bundle', 'src/DubbingPlatform.Api/OpenApi/openapi.v1.json'));
  const outDir = path.resolve(repoRoot, argValue('out', 'frontend/src/api/generated'));
  const manifest = readManifest();
  const { raw, document } = loadBundle(bundlePath);
  validateBundle(raw, document);

  pendingParams.length = 0;
  const client = emitClient(document);
  const schemas = emitSchemas(document);
  const index = emitIndex();
  const stamp =
    HEADER +
    '\nopenapi.version=' +
    (manifest.openapiVersion || 'v1') +
    '\nbundle=sha256:' +
    sha256Hex(raw) +
    '\ngenerator=hermetic-generate-client@' +
    (manifest.localRunnerVersion || '1.0.0') +
    ' (mirrors ' +
    (manifest.canonicalGenerator || 'openapi-typescript-codegen') +
    '@' +
    (manifest.canonicalGeneratorVersion || '0.0.0') +
    ')\n';

  fs.mkdirSync(outDir, { recursive: true });
  fs.writeFileSync(path.join(outDir, 'schemas.ts'), schemas.endsWith('\n') ? schemas : schemas + '\n');
  fs.writeFileSync(path.join(outDir, 'client.ts'), client.endsWith('\n') ? client : client + '\n');
  fs.writeFileSync(path.join(outDir, 'index.ts'), index.endsWith('\n') ? index : index + '\n');
  fs.writeFileSync(path.join(outDir, 'OPENAPI_VERSION'), stamp);
  console.log('generate-api: wrote 4 files to ' + path.relative(repoRoot, outDir));
}

main();
