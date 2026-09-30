import { tryGetEnv } from '../lib/env.js';
import { useAppStore } from '../stores/index.js';
import { resolveTelemetryCorrelation, sanitizeRoute } from './correlation.js';

/**
 * Safe telemetry harness (Task 018).
 *
 * - Allowlist-only payloads: every event carries a fixed set of scalar
 *   fields (routes path-only, codes, counts, latencies, opaque correlation
 *   IDs). Anything resembling a token, URL query, media reference, or
 *   transcript/translation text is rejected by `assertAllowlisted` (throws in
 *   tests/dev) before it can reach the buffer or sink.
 * - Kill-switch: `VITE_TELEMETRY_ENABLED` plus a persisted user opt-out.
 *   Disabled telemetry drops events silently (still validated).
 * - Failure-proof: the sink runs inside try/catch with no retries — a dead
 *   endpoint never blocks UI and never retry-loops. Events also ring-buffer
 *   in memory (cap 100) so tests and diagnostics can observe them.
 * - No PII: emails are never attached (omitted, not hashed — hashing still
 *   fingerprints); only locale tags, app versions, and opaque IDs ship.
 */

export type TelemetryEventType =
  | 'page_view'
  | 'route_change'
  | 'api_failure'
  | 'unknown_status'
  | 'missing_translation'
  | 'version_mismatch';

export interface PageViewEvent {
  readonly type: 'page_view';
  readonly route: string;
  readonly locale?: string;
  readonly appVersion?: string;
  readonly correlationId: string;
}

export interface RouteChangeEvent {
  readonly type: 'route_change';
  readonly from: string;
  readonly to: string;
  readonly correlationId: string;
}

export interface ApiFailureEvent {
  readonly type: 'api_failure';
  readonly route: string;
  readonly code: string;
  readonly correlationId: string;
  readonly latencyMs?: number;
  readonly status?: number;
}

export interface UnknownStatusEvent {
  readonly type: 'unknown_status';
  readonly status: string;
  readonly correlationId: string;
}

export interface MissingTranslationEvent {
  readonly type: 'missing_translation';
  readonly locale: string;
  readonly key: string;
  readonly correlationId: string;
}

/**
 * A detected deploy skew (Task 043). Carries two build TAGS, both of which are
 * release identifiers and neither of which is a user, a route, or a URL. The
 * field names are `baked`/`served` rather than `version` because the value is
 * not the application's semantic version - it is which of two releases the
 * client and the CDN each think is current, and the ratio of the two is the
 * signal.
 */
export interface VersionMismatchEvent {
  readonly type: 'version_mismatch';
  readonly baked: string;
  readonly served: string;
  readonly correlationId: string;
}

export type TelemetryEvent =
  | PageViewEvent
  | RouteChangeEvent
  | ApiFailureEvent
  | UnknownStatusEvent
  | MissingTranslationEvent
  | VersionMismatchEvent;

/** Payload keys that must never reach telemetry (checked case-insensitively). */
const FORBIDDEN_KEYS: ReadonlySet<string> = new Set([
  'token',
  'accesstoken',
  'refreshtoken',
  'authorization',
  'bearer',
  'email',
  'password',
  'secret',
  'query',
  'search',
  'transcript',
  'translation',
  'edittext',
  'text',
  'media',
  'audio',
  'video',
  'url',
  'downloadurl',
  'signedurl',
  'body',
  'payload',
  'details',
]);

/**
 * Throws when a payload carries a forbidden key. Builders only ever set
 * allowlisted fields; this is the backstop the R3 test exercises directly.
 */
export function assertAllowlisted(payload: Record<string, unknown>): void {
  const offenders = Object.keys(payload).filter((key) => FORBIDDEN_KEYS.has(key.toLowerCase()));
  if (offenders.length > 0) {
    throw new Error(`Telemetry payload contains forbidden fields: ${offenders.join(', ')}`);
  }
}

/** Reads the build-time kill-switch without throwing outside the app guard. */
export function readTelemetryFlag(): boolean {
  try {
    const result = tryGetEnv();
    return result.ok && result.env.telemetryEnabled;
  } catch {
    return false;
  }
}

/** Effective switch: build flag on AND the user has not opted out. */
export function isTelemetryEnabled(): boolean {
  if (!readTelemetryFlag()) {
    return false;
  }
  try {
    return !useAppStore.getState().telemetryOptOut;
  } catch {
    return false;
  }
}

const BUFFER_CAP = 100;
let buffer: TelemetryEvent[] = [];
let sink: ((event: TelemetryEvent) => void) | undefined;

/** Test/ops seam: replaces the event sink (default buffers only). */
export function setTelemetrySink(next: ((event: TelemetryEvent) => void) | undefined): void {
  sink = next;
}

/** Test seam: observes buffered events without a sink. */
export function getBufferedEvents(): readonly TelemetryEvent[] {
  return buffer;
}

/** Test seam: clears the ring buffer. */
export function clearBufferedEvents(): void {
  buffer = [];
}

/**
 * Validates (always) then buffers + forwards (only when enabled). Never
 * throws for sink failures — drops them silently instead.
 */
export function emitTelemetryEvent(event: TelemetryEvent): void {
  assertAllowlisted(event as unknown as Record<string, unknown>);
  if (!isTelemetryEnabled()) {
    return;
  }
  buffer = [...buffer, event].slice(-BUFFER_CAP);
  if (sink !== undefined) {
    try {
      sink(event);
    } catch {
      // Endpoint down: drop silently, never block UI, never retry-loop.
    }
  }
}

/** Records a page view for a path-only route. */
export function trackPageView(route: string, options?: { locale?: string; appVersion?: string }): void {
  emitTelemetryEvent({
    type: 'page_view',
    route: sanitizeRoute(route),
    ...(options?.locale !== undefined ? { locale: options.locale } : {}),
    ...(options?.appVersion !== undefined ? { appVersion: options.appVersion } : {}),
    correlationId: resolveTelemetryCorrelation(),
  });
}

/** Records a client-side route transition (both ends path-only). */
export function trackRouteChange(from: string, to: string): void {
  emitTelemetryEvent({
    type: 'route_change',
    from: sanitizeRoute(from),
    to: sanitizeRoute(to),
    correlationId: resolveTelemetryCorrelation(),
  });
}

export interface ApiFailureInput {
  readonly route: string;
  readonly code: string;
  readonly correlationId?: string;
  readonly latencyMs?: number;
  readonly status?: number;
}

/** Records a normalized API failure (Task 017 `AppError` → telemetry join). */
export function trackApiFailure(input: ApiFailureInput): void {
  emitTelemetryEvent({
    type: 'api_failure',
    route: sanitizeRoute(input.route),
    code: input.code,
    correlationId: input.correlationId ?? resolveTelemetryCorrelation(),
    ...(input.latencyMs !== undefined ? { latencyMs: input.latencyMs } : {}),
    ...(input.status !== undefined ? { status: input.status } : {}),
  });
}

/** Records an unmapped backend status (replaces the console-only warning). */
export function trackUnknownStatus(status: string): void {
  try {
    emitTelemetryEvent({
      type: 'unknown_status',
      status,
      correlationId: resolveTelemetryCorrelation(),
    });
  } catch {
    // Telemetry must never break rendering.
  }
}

/** Records an i18n fallback (missing key in a non-`en` bundle). */
export function trackMissingTranslation(input: { locale: string; key: string }): void {
  try {
    emitTelemetryEvent({
      type: 'missing_translation',
      locale: input.locale,
      key: input.key,
      correlationId: resolveTelemetryCorrelation(),
    });
  } catch {
    // Telemetry must never break rendering.
  }
}

/**
 * Records a detected deploy skew (Task 043): the tag baked into the running
 * bundle against the tag the CDN reports serving. Two release tags and nothing
 * else, so it is safe to ship - the same reason `appVersion` is allowlisted on
 * `page_view`.
 */
export function trackVersionMismatch(input: { baked: string; served: string }): void {
  try {
    emitTelemetryEvent({
      type: 'version_mismatch',
      baked: input.baked,
      served: input.served,
      correlationId: resolveTelemetryCorrelation(),
    });
  } catch {
    // Telemetry must never break rendering, and never block the reload prompt.
  }
}
