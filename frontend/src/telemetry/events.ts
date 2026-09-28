import { tryGetEnv } from '../lib/env.js';
import { resolveTelemetryCorrelation, sanitizeRoute } from './correlation.js';
import { assertNoSensitive, scrubPayload } from './scrub.js';
import { assertAllowlisted, isTelemetryEnabled } from './telemetry.js';

/**
 * Structured frontend telemetry taxonomy (Task 038).
 *
 * Categories: page / api / ui / upload / sse. Every event carries the base
 * payload `{ app, env, appVersion, browser, os, route, correlationId }`:
 * path-only routes (query/hash stripped), opaque correlation IDs (no
 * tenant/user info), and scalar-only properties (codes, counts, latencies).
 * Builders scrub before returning; `emitStructuredEvent` re-scrubs, asserts
 * the Task 018 allowlist, honors the same kill-switch + opt-out, ring-buffers
 * (cap 100, drop oldest), and never throws for sink failures.
 */

export const TELEMETRY_APP_NAME = 'dubbing-platform';

export type TelemetryEnvironment = 'production' | 'development' | 'test';

export interface TelemetryBase {
  readonly app: string;
  readonly env: TelemetryEnvironment;
  readonly appVersion: string;
  readonly browser: string;
  readonly os: string;
  readonly route: string;
  readonly correlationId: string;
}

export type StructuredEventCategory = 'page' | 'api' | 'ui' | 'upload' | 'sse';

export type StructuredEventName =
  | 'page.view'
  | 'api.success'
  | 'api.failure'
  | 'ui.interaction'
  | 'upload.started'
  | 'upload.progress'
  | 'upload.completed'
  | 'upload.failed'
  | 'sse.connected'
  | 'sse.reconnected'
  | 'sse.failed'
  | 'sse.fallback';

export type ScalarProperty = string | number | boolean;

export interface StructuredTelemetryEvent {
  readonly category: StructuredEventCategory;
  readonly name: StructuredEventName;
  readonly base: TelemetryBase;
  readonly properties: Readonly<Record<string, ScalarProperty>>;
}

/** Best-effort browser family from the user agent; never empty. */
export function detectBrowser(userAgent?: string): string {
  const agent =
    userAgent ??
    (typeof navigator !== 'undefined' && typeof navigator.userAgent === 'string'
      ? navigator.userAgent
      : '');
  const lower = agent.toLowerCase();
  if (lower.includes('edg/')) {
    return 'Edge';
  }
  if (lower.includes('chrome/') && !lower.includes('chromium')) {
    return 'Chrome';
  }
  if (lower.includes('firefox/')) {
    return 'Firefox';
  }
  if (lower.includes('safari/') && lower.includes('version/')) {
    return 'Safari';
  }
  return agent === '' ? 'unknown' : 'other';
}

/** Best-effort OS family from the user agent/platform; never empty. */
export function detectOs(userAgent?: string): string {
  const agent =
    userAgent ??
    (typeof navigator !== 'undefined' && typeof navigator.userAgent === 'string'
      ? navigator.userAgent
      : '');
  const lower = agent.toLowerCase();
  if (lower.includes('windows')) {
    return 'Windows';
  }
  if (lower.includes('android')) {
    return 'Android';
  }
  if (lower.includes('iphone') || lower.includes('ipad')) {
    return 'iOS';
  }
  if (lower.includes('mac os')) {
    return 'macOS';
  }
  if (lower.includes('linux')) {
    return 'Linux';
  }
  return agent === '' ? 'unknown' : 'other';
}

/** Runtime environment bucket; test runners map to `test`. Safe in any host. */
export function resolveTelemetryEnvironment(): TelemetryEnvironment {
  try {
    const proc = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process;
    const nodeEnv = proc?.env?.['NODE_ENV'];
    const vitest = proc?.env?.['VITEST_WORKER_ID'];
    if (vitest !== undefined || nodeEnv === 'test') {
      return 'test';
    }
    if (nodeEnv === 'development') {
      return 'development';
    }
  } catch {
    // Host without process: fall through to production.
  }
  return 'production';
}

/** Reads the build app version without throwing outside the app guard. */
function resolveAppVersion(): string {
  try {
    const result = tryGetEnv();
    return result.ok ? result.env.appVersion : '0.0.0-dev';
  } catch {
    return '0.0.0-dev';
  }
}

/** Builds the base context for a route; all fields scalar and scrub-safe. */
export function getTelemetryBase(route?: string, correlationId?: string): TelemetryBase {
  return {
    app: TELEMETRY_APP_NAME,
    env: resolveTelemetryEnvironment(),
    appVersion: resolveAppVersion(),
    browser: detectBrowser(),
    os: detectOs(),
    route: sanitizeRoute(route ?? '/'),
    correlationId: correlationId ?? resolveTelemetryCorrelation(),
  };
}

function buildEvent(
  category: StructuredEventCategory,
  name: StructuredEventName,
  route: string | undefined,
  properties: Record<string, ScalarProperty>,
  correlationId?: string,
): StructuredTelemetryEvent {
  const event: StructuredTelemetryEvent = {
    category,
    name,
    base: getTelemetryBase(route, correlationId),
    properties: scrubPayload({ ...properties }),
  };
  assertNoSensitive(event);
  return event;
}

/** Page view for a path-only route. */
export function buildPageEvent(route: string, correlationId?: string): StructuredTelemetryEvent {
  return buildEvent('page', 'page.view', route, {}, correlationId);
}

export interface ApiTelemetryInput {
  readonly route: string;
  readonly code?: string;
  readonly status?: number;
  readonly latencyMs?: number;
  readonly correlationId?: string;
}

/** API outcome (success or failure) with codes/latency only. */
export function buildApiEvent(input: ApiTelemetryInput, failed: boolean): StructuredTelemetryEvent {
  const properties: Record<string, ScalarProperty> = {};
  if (input.code !== undefined) {
    properties['code'] = input.code;
  }
  if (input.status !== undefined) {
    properties['status'] = input.status;
  }
  if (input.latencyMs !== undefined) {
    properties['latencyMs'] = input.latencyMs;
  }
  return buildEvent(
    'api',
    failed ? 'api.failure' : 'api.success',
    input.route,
    properties,
    input.correlationId,
  );
}

export interface UiTelemetryInput {
  readonly route?: string;
  readonly element: string;
  readonly action: string;
  readonly correlationId?: string;
}

/** UI interaction: element + action identifiers only (never labels/content). */
export function buildUiEvent(input: UiTelemetryInput): StructuredTelemetryEvent {
  return buildEvent(
    'ui',
    'ui.interaction',
    input.route,
    { element: input.element, action: input.action },
    input.correlationId,
  );
}

export type UploadTelemetryName = 'upload.started' | 'upload.progress' | 'upload.completed' | 'upload.failed';

export interface UploadTelemetryInput {
  readonly stage: UploadTelemetryName;
  readonly route?: string;
  readonly bytesSent?: number;
  readonly bytesTotal?: number;
  readonly code?: string;
  readonly correlationId?: string;
}

/** Upload-funnel transition with byte counts/codes only (never file content). */
export function buildUploadEvent(input: UploadTelemetryInput): StructuredTelemetryEvent {
  const properties: Record<string, ScalarProperty> = {};
  if (input.bytesSent !== undefined) {
    properties['bytesSent'] = input.bytesSent;
  }
  if (input.bytesTotal !== undefined) {
    properties['bytesTotal'] = input.bytesTotal;
  }
  if (input.code !== undefined) {
    properties['code'] = input.code;
  }
  return buildEvent('upload', input.stage, input.route, properties, input.correlationId);
}

export type SseTelemetryName = 'sse.connected' | 'sse.reconnected' | 'sse.failed' | 'sse.fallback';

export interface SseTelemetryInput {
  readonly state: SseTelemetryName;
  readonly route?: string;
  readonly reconnectAttempt?: number;
  readonly code?: string;
  readonly correlationId?: string;
}

/** SSE lifecycle transition with attempt counts/codes only. */
export function buildSseEvent(input: SseTelemetryInput): StructuredTelemetryEvent {
  const properties: Record<string, ScalarProperty> = {};
  if (input.reconnectAttempt !== undefined) {
    properties['reconnectAttempt'] = input.reconnectAttempt;
  }
  if (input.code !== undefined) {
    properties['code'] = input.code;
  }
  return buildEvent('sse', input.state, input.route, properties, input.correlationId);
}

const STRUCTURED_BUFFER_CAP = 100;

let structuredBuffer: StructuredTelemetryEvent[] = [];
let structuredSink: ((event: StructuredTelemetryEvent) => void) | undefined;

/** Test/ops seam: replaces the structured-event sink (default buffers only). */
export function setStructuredEventSink(next: ((event: StructuredTelemetryEvent) => void) | undefined): void {
  structuredSink = next;
}

/** Test seam: observes buffered structured events without a sink. */
export function getBufferedStructuredEvents(): readonly StructuredTelemetryEvent[] {
  return structuredBuffer;
}

/** Test seam: clears the structured ring buffer. */
export function clearStructuredEvents(): void {
  structuredBuffer = [];
}

/**
 * Validates (always) then buffers + forwards (only when enabled). Never
 * throws for sink failures. Anything scrub-sensitive is rejected before it
 * can reach the buffer or sink.
 */
export function emitStructuredEvent(event: StructuredTelemetryEvent): void {
  const scrubbed = scrubPayload(event);
  assertNoSensitive(scrubbed);
  assertAllowlisted({
    type: scrubbed.name,
    category: scrubbed.category,
    route: scrubbed.base.route,
    correlationId: scrubbed.base.correlationId,
  });
  if (!isTelemetryEnabled()) {
    return;
  }
  structuredBuffer = [...structuredBuffer, scrubbed].slice(-STRUCTURED_BUFFER_CAP);
  if (structuredSink !== undefined) {
    try {
      structuredSink(scrubbed);
    } catch {
      // Endpoint down: drop silently, never block UI, never retry-loop.
    }
  }
}
