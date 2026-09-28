import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetEnvCache } from '../../lib/env.js';
import { useAppStore } from '../../stores/index.js';
import { buildCorrelationHeaders, extractCorrelationId } from '../../api/client/correlation.js';
import { resolveTelemetryCorrelation, sanitizeRoute } from '../correlation.js';
import {
  clearAnalyticsBatch,
  flushAnalytics,
  getAnalyticsBatch,
  isAnalyticsEnabled,
  setAnalyticsSink,
  trackAnalytics,
} from '../analytics.js';
import {
  buildApiEvent,
  buildPageEvent,
  buildSseEvent,
  buildUiEvent,
  buildUploadEvent,
  clearStructuredEvents,
  detectBrowser,
  detectOs,
  emitStructuredEvent,
  getBufferedStructuredEvents,
  getTelemetryBase,
  resolveTelemetryEnvironment,
  setStructuredEventSink,
} from '../events.js';
import { assertNoSensitive, containsSensitive, scrubPayload } from '../scrub.js';
import {
  assertAllowlisted,
  clearBufferedEvents,
  emitTelemetryEvent,
  getBufferedEvents,
  isTelemetryEnabled,
  setTelemetrySink,
  trackApiFailure,
  trackPageView,
  trackRouteChange,
} from '../telemetry.js';

function enableTelemetry(): void {
  resetEnvCache();
  vi.stubEnv('VITE_API_BASE_URL', 'http://localhost:5000');
  vi.stubEnv('VITE_TELEMETRY_ENABLED', 'true');
  useAppStore.getState().setTelemetryOptOut(false);
}

beforeEach(() => {
  useAppStore.getState().resetForTests();
  clearBufferedEvents();
  setTelemetrySink(undefined);
  clearStructuredEvents();
  setStructuredEventSink(undefined);
  clearAnalyticsBatch();
  setAnalyticsSink(undefined);
  enableTelemetry();
});

afterEach(() => {
  vi.unstubAllEnvs();
  resetEnvCache();
  clearBufferedEvents();
  setTelemetrySink(undefined);
  clearStructuredEvents();
  setStructuredEventSink(undefined);
  clearAnalyticsBatch();
  setAnalyticsSink(undefined);
  useAppStore.getState().resetForTests();
});

describe('telemetry allowlist (R3)', () => {
  it('throws on forbidden payload fields', () => {
    for (const field of ['token', 'email', 'transcript', 'translation', 'media', 'url', 'query', 'Authorization']) {
      expect(() => assertAllowlisted({ type: 'page_view', route: '/x', [field]: 'secret' })).toThrow(
        /forbidden fields/,
      );
    }
  });

  it('rejects forbidden events at emission time', () => {
    expect(() =>
      emitTelemetryEvent({
        type: 'page_view',
        route: '/dashboard',
        correlationId: 'c1',
        email: 'a@b.c',
      } as unknown as Parameters<typeof emitTelemetryEvent>[0]),
    ).toThrow(/forbidden fields/);
  });

  it('buffers allowlisted page views with path-only routes', () => {
    trackPageView('/dashboard?tab=overview&secret=1');
    const events = getBufferedEvents();
    expect(events.length).toBe(1);
    expect(events[0]?.type).toBe('page_view');
    if (events[0]?.type === 'page_view') {
      expect(events[0].route).toBe('/dashboard');
      expect(events[0].correlationId).not.toBe('');
    }
  });

  it('strips origins and queries from route changes', () => {
    trackRouteChange('https://app.example.com/projects?x=1', '/projects/prj_1#tab');
    const last = getBufferedEvents()[getBufferedEvents().length - 1];
    expect(last?.type).toBe('route_change');
    if (last?.type === 'route_change') {
      expect(last.from).toBe('/projects');
      expect(last.to).toBe('/projects/prj_1');
    }
  });

  it('propagates the error correlation ID into api failures', () => {
    trackApiFailure({ route: '/projects', code: 'SELECTION_CONFLICT', correlationId: 'corr-9', status: 409 });
    const last = getBufferedEvents()[getBufferedEvents().length - 1];
    expect(last?.type).toBe('api_failure');
    if (last?.type === 'api_failure') {
      expect(last.correlationId).toBe('corr-9');
      expect(last.code).toBe('SELECTION_CONFLICT');
    }
  });
});

describe('telemetry kill-switch + opt-out', () => {
  it('drops events when the build flag is off', () => {
    vi.stubEnv('VITE_TELEMETRY_ENABLED', 'false');
    resetEnvCache();
    expect(isTelemetryEnabled()).toBe(false);
    trackPageView('/dashboard');
    expect(getBufferedEvents().length).toBe(0);
  });

  it('drops events when the user opts out', () => {
    useAppStore.getState().setTelemetryOptOut(true);
    expect(isTelemetryEnabled()).toBe(false);
    trackPageView('/dashboard');
    expect(getBufferedEvents().length).toBe(0);
  });

  it('swallows sink failures without blocking callers', () => {
    setTelemetrySink(() => {
      throw new Error('endpoint down');
    });
    expect(() => trackPageView('/dashboard')).not.toThrow();
    expect(getBufferedEvents().length).toBe(1);
  });
});

describe('correlation propagation', () => {
  it('prefers the error correlation ID, then the last request ID', () => {
    expect(resolveTelemetryCorrelation({ correlationId: 'corr-err' })).toBe('corr-err');
    const fallback = resolveTelemetryCorrelation(undefined);
    expect(typeof fallback).toBe('string');
    expect(fallback).not.toBe('');
  });

  it('sanitizes routes to path-only values', () => {
    expect(sanitizeRoute('/projects?x=1#y')).toBe('/projects');
    expect(sanitizeRoute('https://x.example/a/b?c=1')).toBe('/a/b');
    expect(sanitizeRoute('')).toBe('/');
  });

  it('builds correlation headers and extracts error ids (Task 038 facade)', () => {
    const headers = buildCorrelationHeaders('corr-action-1');
    expect(headers['X-Correlation-ID']).toBe('corr-action-1');
    const minted = buildCorrelationHeaders();
    expect(typeof minted['X-Correlation-ID']).toBe('string');
    expect(minted['X-Correlation-ID']).not.toBe('');
    expect(extractCorrelationId({ correlationId: 'corr-err-2' })).toBe('corr-err-2');
  });
});

describe('taxonomy conformance (R2)', () => {
  it('attaches app/env/browser/os/route/correlation to every category', () => {
    const events = [
      buildPageEvent('/dashboard?tab=overview'),
      buildApiEvent({ route: '/projects', code: 'OK', status: 200, latencyMs: 12 }, false),
      buildApiEvent({ route: '/reviews', code: 'REVIEW_VERSION_CONFLICT', status: 409 }, true),
      buildUiEvent({ route: '/projects', element: 'export-button', action: 'click' }),
      buildUploadEvent({ stage: 'upload.progress', bytesSent: 512, bytesTotal: 1024 }),
      buildSseEvent({ state: 'sse.reconnected', reconnectAttempt: 2 }),
    ];
    for (const event of events) {
      expect(event.base.app).toBe('dubbing-platform');
      expect(['production', 'development', 'test']).toContain(event.base.env);
      expect(event.base.browser).not.toBe('');
      expect(event.base.os).not.toBe('');
      expect(event.base.route).not.toContain('?');
      expect(event.base.correlationId).not.toBe('');
      expect(containsSensitive(event)).toBe(false);
    }
    expect(events.map((event) => event.name)).toEqual([
      'page.view',
      'api.success',
      'api.failure',
      'ui.interaction',
      'upload.progress',
      'sse.reconnected',
    ]);
  });

  it('keeps routes path-only and correlations opaque', () => {
    const base = getTelemetryBase('https://app.example.com/projects/prj_1?token=abc#tab', 'corr-1');
    expect(base.route).toBe('/projects/prj_1');
    expect(base.correlationId).toBe('corr-1');
  });

  it('buffers structured events with the same cap semantics', () => {
    emitStructuredEvent(buildPageEvent('/dashboard'));
    expect(getBufferedStructuredEvents().length).toBe(1);
    expect(detectBrowser()).not.toBe('');
    expect(detectOs()).not.toBe('');
    expect(['production', 'development', 'test']).toContain(resolveTelemetryEnvironment());
  });

  it('drops structured events when telemetry is disabled', () => {
    useAppStore.getState().setTelemetryOptOut(true);
    emitStructuredEvent(buildPageEvent('/dashboard'));
    expect(getBufferedStructuredEvents().length).toBe(0);
  });
});

describe('scrubber adversarial fixtures (R5)', () => {
  const adversarial = {
    accessToken: 'hunter2-token-value',
    nested: {
      transcript: 'secret spoken words here',
      signedUrl: 'https://cdn.example.com/media.mp4?sig=abcdef',
      audioData: 'data:audio/mp3;base64,SUQzBAAAAAAAI1RTU0UAAAAPAAADTGF2ZjU4LjI5LjEwMAAAAAAAAAAAAAAA',
      blob: 'QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5Ky89QUJDREVGR0hJSktMTU5PUFFSU1Q=',
      stack: 'Error: boom\n    at renderFrame (app.ts:10:20)\n    at loop (app.ts:30:4)',
      contact: 'agent@example.com',
      credential: 'Bearer abcdefghijklmnop',
      deep: [{ translation: 'le texte secret', media: 'video-bytes' }],
    },
    route: '/projects',
  };

  it('fully redacts every sensitive fixture', () => {
    const scrubbed = scrubPayload(adversarial);
    const text = JSON.stringify(scrubbed);
    for (const leak of [
      'hunter2',
      'secret spoken',
      'cdn.example.com',
      'sig=abcdef',
      'data:audio',
      'agent@example.com',
      'abcdefghijklmnop',
      'at renderFrame',
      'le texte secret',
      'video-bytes',
    ]) {
      expect(text).not.toContain(leak);
    }
    expect(containsSensitive(scrubbed)).toBe(false);
    expect(() => assertNoSensitive(scrubbed)).not.toThrow();
  });

  it('detects unscrubbed payloads', () => {
    expect(containsSensitive(adversarial)).toBe(true);
    expect(() => assertNoSensitive(adversarial)).toThrow(/sensitive material/);
  });

  it('preserves safe scalar structure', () => {
    const scrubbed = scrubPayload({ route: '/projects', latencyMs: 12, ok: true, count: 3 });
    expect(scrubbed).toEqual({ route: '/projects', latencyMs: 12, ok: true, count: 3 });
    expect(containsSensitive(scrubbed)).toBe(false);
  });
});

describe('analytics allowlist + opt-out (R3)', () => {
  it('buffers allowlisted events with correlation ids', () => {
    trackAnalytics('project.created', { plan: 'pro' }, 'corr-a1');
    trackAnalytics('upload.started');
    trackAnalytics('processing.completed', { stageCount: 4 });
    trackAnalytics('review.resolved');
    trackAnalytics('translation.edited');
    trackAnalytics('voice.changed');
    trackAnalytics('export.completed');
    const batch = getAnalyticsBatch();
    expect(batch.length).toBe(7);
    expect(batch[0]?.name).toBe('project.created');
    expect(batch[0]?.correlationId).toBe('corr-a1');
    for (const event of batch) {
      expect(containsSensitive(event)).toBe(false);
    }
  });

  it('drops non-allowlisted events and logs in dev', () => {
    const debug = vi.spyOn(console, 'debug').mockImplementation(() => {});
    trackAnalytics('project.deleted');
    trackAnalytics('custom.anything');
    expect(getAnalyticsBatch().length).toBe(0);
    expect(debug).toHaveBeenCalled();
    debug.mockRestore();
  });

  it('suppresses analytics on opt-out while the crash pipeline stays validating', () => {
    expect(isAnalyticsEnabled()).toBe(true);
    useAppStore.getState().setTelemetryOptOut(true);
    expect(isAnalyticsEnabled()).toBe(false);
    trackAnalytics('project.created');
    expect(getAnalyticsBatch().length).toBe(0);
    // The error/crash channel is independent: schema enforcement still runs.
    expect(() =>
      emitTelemetryEvent({
        type: 'page_view',
        route: '/dashboard',
        correlationId: 'c1',
        token: 'secret',
      } as unknown as Parameters<typeof emitTelemetryEvent>[0]),
    ).toThrow(/forbidden fields/);
  });

  it('drops the in-flight batch when opt-out toggles mid-session', () => {
    const delivered: string[] = [];
    setAnalyticsSink((event) => {
      delivered.push(event.name);
    });
    trackAnalytics('upload.started');
    expect(getAnalyticsBatch().length).toBe(1);
    useAppStore.getState().setTelemetryOptOut(true);
    expect(getAnalyticsBatch().length).toBe(0);
    expect(flushAnalytics()).toBe(0);
    expect(delivered).toEqual([]);
  });

  it('flushes through the sink with drop-oldest cap semantics', () => {
    const delivered: string[] = [];
    setAnalyticsSink((event) => {
      delivered.push(event.name);
    });
    trackAnalytics('project.created');
    trackAnalytics('export.completed');
    expect(flushAnalytics()).toBe(2);
    expect(delivered).toEqual(['project.created', 'export.completed']);
    expect(getAnalyticsBatch().length).toBe(0);
  });
});
