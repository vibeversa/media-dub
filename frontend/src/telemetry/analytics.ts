import { useAppStore } from '../stores/index.js';
import { resolveTelemetryCorrelation } from './correlation.js';
import { scrubPayload } from './scrub.js';
import { isTelemetryEnabled } from './telemetry.js';

/**
 * Allowlisted product analytics (Task 038).
 *
 * Only the event names in {@link AnalyticsEventName} may be emitted; the
 * union rejects dynamic names at compile time and
 * {@link isAllowlistedAnalyticsEvent} drops anything else at runtime (logged
 * in dev). Payloads are scrubbed before buffering. The user opt-out lives in
 * the persisted preferences store (`dubbing.telemetry.optOut`, owned by the
 * Task 018 store slice over the Task 006 preferences surface): opting out
 * drops the in-flight batch and suppresses subsequent analytics while the
 * crash/error channel (Task 018 harness + backend error envelopes, which
 * always carry correlation IDs server-side) is unaffected.
 */

export type AnalyticsEventName =
  | 'project.created'
  | 'upload.started'
  | 'upload.progress'
  | 'upload.completed'
  | 'upload.failed'
  | 'processing.started'
  | 'processing.progress'
  | 'processing.completed'
  | 'processing.failed'
  | 'review.opened'
  | 'review.resolved'
  | 'review.commented'
  | 'translation.edited'
  | 'voice.changed'
  | 'export.requested'
  | 'export.completed'
  | 'export.failed';

const ANALYTICS_ALLOWLIST: ReadonlySet<string> = new Set<string>([
  'project.created',
  'upload.started',
  'upload.progress',
  'upload.completed',
  'upload.failed',
  'processing.started',
  'processing.progress',
  'processing.completed',
  'processing.failed',
  'review.opened',
  'review.resolved',
  'review.commented',
  'translation.edited',
  'voice.changed',
  'export.requested',
  'export.completed',
  'export.failed',
]);

export interface AnalyticsEvent {
  readonly name: AnalyticsEventName;
  readonly properties: Readonly<Record<string, string | number | boolean>>;
  readonly correlationId: string;
  readonly occurredAt: string;
}

/** Runtime allowlist check; dynamic event names are rejected by the type signature. */
export function isAllowlistedAnalyticsEvent(name: string): name is AnalyticsEventName {
  return ANALYTICS_ALLOWLIST.has(name);
}

/**
 * Analytics gate: build flag on AND the user has not opted out. Crash/error
 * telemetry is a separate channel and is not gated by this function.
 */
export function isAnalyticsEnabled(): boolean {
  return isTelemetryEnabled();
}

/** Dev-only log (eslint-clean: no `import.meta.env` outside `lib/env`). */
function devLog(message: string): void {
  try {
    const proc = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process;
    if (proc?.env?.['NODE_ENV'] === 'production') {
      return;
    }
    console.debug(message);
  } catch {
    // Logging must never break analytics.
  }
}

const ANALYTICS_BATCH_CAP = 100;

let batch: AnalyticsEvent[] = [];
let sink: ((event: AnalyticsEvent) => void) | undefined;

/** Test/ops seam: replaces the analytics sink (default batches only). */
export function setAnalyticsSink(next: ((event: AnalyticsEvent) => void) | undefined): void {
  sink = next;
}

/** Test seam: observes the pending analytics batch. */
export function getAnalyticsBatch(): readonly AnalyticsEvent[] {
  return batch;
}

/** Test seam: drops the pending analytics batch. */
export function clearAnalyticsBatch(): void {
  batch = [];
}

/**
 * Enqueues one allowlisted analytics event (scrubbed). Non-allowlisted names
 * are dropped + logged in dev; events while disabled (flag off or opted out)
 * are dropped silently. Never throws.
 */
export function trackAnalytics(
  name: string,
  properties?: Record<string, string | number | boolean>,
  correlationId?: string,
): void {
  if (!isAllowlistedAnalyticsEvent(name)) {
    devLog(`[analytics] dropped non-allowlisted event: ${name}`);
    return;
  }
  if (!isAnalyticsEnabled()) {
    return;
  }
  try {
    const event: AnalyticsEvent = {
      name,
      properties: scrubPayload({ ...(properties ?? {}) }),
      correlationId: correlationId ?? resolveTelemetryCorrelation(),
      occurredAt: new Date().toISOString(),
    };
    batch = [...batch, event].slice(-ANALYTICS_BATCH_CAP);
  } catch {
    // Analytics must never break the product flow.
  }
}

/**
 * Flushes the pending batch through the sink. Re-checks the gate at flush
 * time so an opt-out toggled mid-session drops the in-flight batch instead of
 * emitting it. Returns the number of events delivered. Never throws.
 */
export function flushAnalytics(): number {
  if (!isAnalyticsEnabled()) {
    batch = [];
    return 0;
  }
  const pending = batch;
  batch = [];
  if (sink === undefined) {
    return 0;
  }
  let delivered = 0;
  for (const event of pending) {
    try {
      sink(event);
      delivered += 1;
    } catch {
      // Endpoint down: drop silently, never block UI, never retry-storm.
    }
  }
  return delivered;
}

let optOutGuardInstalled = false;

/**
 * Subscribes to the persisted opt-out so a mid-session toggle drops the
 * in-flight batch immediately. Idempotent; auto-installed on module import.
 */
export function installAnalyticsOptOutGuard(): void {
  if (optOutGuardInstalled) {
    return;
  }
  optOutGuardInstalled = true;
  try {
    useAppStore.subscribe((state, previous) => {
      if (state.telemetryOptOut && !previous.telemetryOptOut) {
        batch = [];
      }
    });
  } catch {
    // Store unavailable (SSR/tests without DOM): flush-time gate still applies.
  }
}

installAnalyticsOptOutGuard();
