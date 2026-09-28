// Task 039B: telemetry state-matrix gap closure.
//
// Closes the branch gaps in `analytics.ts`, `events.ts`, and `telemetry.ts`:
// UA-family detection, environment buckets, builder optional-field matrices,
// sink-failure tolerance, dev-log gating, and the opt-out guard. Payloads
// stay allowlisted scalars with synthetic correlation ids (no PII, R3).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetEnvCache } from '../../lib/env.js';
import { useAppStore } from '../../stores/index.js';
import {
  clearAnalyticsBatch,
  flushAnalytics,
  getAnalyticsBatch,
  installAnalyticsOptOutGuard,
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
import {
  clearBufferedEvents,
  getBufferedEvents,
  isTelemetryEnabled,
  readTelemetryFlag,
  setTelemetrySink,
  trackApiFailure,
  trackMissingTranslation,
  trackPageView,
  trackRouteChange,
  trackUnknownStatus,
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
  vi.restoreAllMocks();
});

describe('UA family matrix', () => {
  it('identifies every browser family plus empty/foreign agents', () => {
    expect(detectBrowser('Mozilla/5.0 (Windows NT 10.0) Edg/120.0')).toBe('Edge');
    expect(detectBrowser('Mozilla/5.0 (Windows NT 10.0) Chrome/120.0')).toBe('Chrome');
    expect(detectBrowser('Mozilla/5.0 (Windows NT 10.0) Chrome/120.0 Chromium/120.0')).toBe('other');
    expect(detectBrowser('Mozilla/5.0 (X11; Linux) Firefox/120.0')).toBe('Firefox');
    expect(detectBrowser('Mozilla/5.0 (Macintosh) Version/17.0 Safari/605.1.15')).toBe('Safari');
    expect(detectBrowser('Mozilla/5.0 (compatible; Bot/1.0)')).toBe('other');
    expect(detectBrowser('')).toBe('unknown');
    expect(detectBrowser()).not.toBe('');
  });

  it('identifies every OS family plus empty/foreign agents', () => {
    expect(detectOs('Mozilla/5.0 (Windows NT 10.0; Win64; x64)')).toBe('Windows');
    expect(detectOs('Mozilla/5.0 (Linux; Android 14)')).toBe('Android');
    expect(detectOs('Mozilla/5.0 (iPhone; CPU iPhone OS 17_0)')).toBe('iOS');
    expect(detectOs('Mozilla/5.0 (iPad; CPU OS 17_0)')).toBe('iOS');
    expect(detectOs('Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0)')).toBe('macOS');
    expect(detectOs('Mozilla/5.0 (X11; Linux x86_64)')).toBe('Linux');
    expect(detectOs('CustomAgent/1.0')).toBe('other');
    expect(detectOs('')).toBe('unknown');
    expect(detectOs()).not.toBe('');
  });
});

describe('environment + version matrix', () => {
  const savedNodeEnv = process.env['NODE_ENV'];
  const savedVitest = process.env['VITEST_WORKER_ID'];

  afterEach(() => {
    if (savedNodeEnv === undefined) {
      delete process.env['NODE_ENV'];
    } else {
      process.env['NODE_ENV'] = savedNodeEnv;
    }
    if (savedVitest === undefined) {
      delete process.env['VITEST_WORKER_ID'];
    } else {
      process.env['VITEST_WORKER_ID'] = savedVitest;
    }
  });

  it('buckets test, development, and production hosts', () => {
    expect(resolveTelemetryEnvironment()).toBe('test');
    delete process.env['VITEST_WORKER_ID'];
    process.env['NODE_ENV'] = 'development';
    expect(resolveTelemetryEnvironment()).toBe('development');
    process.env['NODE_ENV'] = 'production';
    expect(resolveTelemetryEnvironment()).toBe('production');
    delete process.env['NODE_ENV'];
    expect(resolveTelemetryEnvironment()).toBe('production');
  });

  it('falls back to the dev version outside the app guard', () => {
    vi.stubEnv('VITE_API_BASE_URL', '');
    resetEnvCache();
    expect(getTelemetryBase('/x', 'corr-1').appVersion).toBe('0.0.0-dev');
    expect(readTelemetryFlag()).toBe(false);
    expect(isTelemetryEnabled()).toBe(false);
  });
});

describe('telemetry base + builder option matrix', () => {
  it('defaults route and correlation id', () => {
    const base = getTelemetryBase();
    expect(base.route).toBe('/');
    expect(base.correlationId.length).toBeGreaterThan(0);
    expect(base.app).toBe('dubbing-platform');
  });

  it('builds minimal api/ui/upload/sse events without optionals', () => {
    expect(buildApiEvent({ route: '/x' }, false).name).toBe('api.success');
    expect(buildApiEvent({ route: '/x' }, true).properties).toEqual({});
    expect(buildUiEvent({ element: 'b', action: 'click' }).base.route).toBe('/');
    expect(buildUploadEvent({ stage: 'upload.started' }).properties).toEqual({});
    expect(buildUploadEvent({ stage: 'upload.failed', code: 'PROVIDER_FAILED' }).properties['code']).toBe(
      'PROVIDER_FAILED',
    );
    expect(buildSseEvent({ state: 'sse.fallback' }).properties).toEqual({});
    expect(buildSseEvent({ state: 'sse.failed', code: 'X', reconnectAttempt: 3 }).properties).toEqual({
      reconnectAttempt: 3,
      code: 'X',
    });
  });
});

describe('telemetry track-option matrix', () => {
  it('records page views with locale + version options', () => {
    trackPageView('/dashboard', { locale: 'ar', appVersion: '0.1.0-dev' });
    const last = getBufferedEvents()[getBufferedEvents().length - 1];
    expect(last?.type).toBe('page_view');
    if (last?.type === 'page_view') {
      expect(last.locale).toBe('ar');
      expect(last.appVersion).toBe('0.1.0-dev');
    }
  });

  it('records api failures with latency and minted correlation ids', () => {
    trackApiFailure({ route: '/projects', code: 'RATE_LIMITED', latencyMs: 42, status: 429 });
    const last = getBufferedEvents()[getBufferedEvents().length - 1];
    expect(last?.type).toBe('api_failure');
    if (last?.type === 'api_failure') {
      expect(last.latencyMs).toBe(42);
      expect(last.correlationId.length).toBeGreaterThan(0);
    }
  });

  it('records unknown statuses and missing translations without throwing', () => {
    trackUnknownStatus('FUTURE_STATUS');
    trackMissingTranslation({ locale: 'ar', key: 'nav:future' });
    const events = getBufferedEvents();
    expect(events.some((e) => e.type === 'unknown_status')).toBe(true);
    expect(events.some((e) => e.type === 'missing_translation')).toBe(true);
    trackRouteChange('/a', '/b');
    expect(getBufferedEvents().some((e) => e.type === 'route_change')).toBe(true);
  });
});

describe('structured sink matrix', () => {
  it('swallows structured sink failures without blocking callers', () => {
    setStructuredEventSink(() => {
      throw new Error('endpoint down');
    });
    expect(() => emitStructuredEvent(buildPageEvent('/dashboard'))).not.toThrow();
    expect(getBufferedStructuredEvents().length).toBe(1);
  });
});

describe('analytics hardening matrix', () => {
  it('drops the batch when the sink throws mid-flush (never retry-storms)', () => {
    setAnalyticsSink(() => {
      throw new Error('endpoint down');
    });
    trackAnalytics('upload.started');
    expect(getAnalyticsBatch().length).toBe(1);
    expect(flushAnalytics()).toBe(0);
    expect(getAnalyticsBatch().length).toBe(0);
  });

  it('returns zero with no sink and a pending batch', () => {
    trackAnalytics('upload.started');
    expect(flushAnalytics()).toBe(0);
    expect(getAnalyticsBatch().length).toBe(0);
  });

  it('survives scrub failures without breaking the product flow', () => {
    const evil: Record<string, string | number | boolean> = {};
    Object.defineProperty(evil, 'boom', {
      get(): string {
        throw new Error('denied');
      },
      enumerable: true,
    });
    expect(() => trackAnalytics('upload.started', evil)).not.toThrow();
    expect(getAnalyticsBatch().length).toBe(0);
  });

  it('stays silent in production for dropped events (no dev log)', () => {
    const debug = vi.spyOn(console, 'debug').mockImplementation(() => {});
    process.env['NODE_ENV'] = 'production';
    trackAnalytics('project.deleted');
    expect(debug).not.toHaveBeenCalled();
    expect(getAnalyticsBatch().length).toBe(0);
    debug.mockRestore();
    process.env['NODE_ENV'] = 'test';
  });

  it('survives dev-log failures without breaking analytics', () => {
    const debug = vi.spyOn(console, 'debug').mockImplementation(() => {
      throw new Error('denied');
    });
    expect(() => trackAnalytics('project.deleted')).not.toThrow();
    debug.mockRestore();
  });

  it('installs the opt-out guard idempotently', () => {
    installAnalyticsOptOutGuard();
    installAnalyticsOptOutGuard();
    trackAnalytics('upload.started');
    expect(getAnalyticsBatch().length).toBe(1);
  });
});
