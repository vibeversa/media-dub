import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetEnvCache } from '../../lib/env.js';
import { useAppStore } from '../../stores/index.js';
import { resolveTelemetryCorrelation, sanitizeRoute } from '../correlation.js';
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
  enableTelemetry();
});

afterEach(() => {
  vi.unstubAllEnvs();
  resetEnvCache();
  clearBufferedEvents();
  setTelemetrySink(undefined);
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
});
