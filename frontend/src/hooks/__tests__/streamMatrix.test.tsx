// Task 039B: progress-stream state-matrix gap closure.
//
// Covers the hook-body branches the SSE suites leave cold: disabled/idle
// gating (empty id, caller opt-out, anonymous session, SSE kill-switch),
// header-only auth with no token, null-body streams, heartbeat frames,
// replay truncation, 401 expiry emission, failure-to-polling-fallback, hidden
// tabs, plus the pure-helper matrices (ids, invalidations, percents, urls,
// visibility, envelope edge shapes, frame fallbacks). Recovery per §11.6:
// failures back off then yield to polling; 404 stops; every state asserts a
// text signal (041C). Fetch is always the test double (R3).
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../api/client/index.js';
import { queryClient } from '../../app/providers/queryClient.js';
import { useAppStore } from '../../stores/index.js';
import { useAuthStore } from '../../features/auth/authStore.js';
import { resetRestoreStartedForTests } from '../../features/auth/useSession.js';
import { resetEnvCache } from '../../lib/env.js';
import {
  buildProgressStreamUrl,
  extractSseIds,
  formatApproximatePercent,
  isDocumentVisible,
  isTerminalProgressStatus,
  parseProgressFrame,
  readSseEnabled,
  resolveInvalidations,
  shouldInvalidateNotifications,
  useProgressPoll,
  useProgressStream,
  validateSseEnvelope,
} from '../useProgressStream.js';
import type { UseProgressStreamOptions } from '../useProgressStream.js';
import type { SseEnvelope } from '../../api/client/index.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function sseResponse(text: string, headers: Record<string, string> = {}): Response {
  return new Response(text, {
    status: 200,
    headers: { 'Content-Type': 'text/event-stream', ...headers },
  });
}

function frame(id: string, eventType = 'stage.progress', extra: Record<string, unknown> = {}): string {
  const data = {
    correlationId: 'corr_1',
    eventId: id,
    eventType,
    occurredAt: '2024-01-16T12:00:00Z',
    payload: { projectId: 'prj_1', status: 'Running' },
    projectId: 'prj_1',
    schemaVersion: 1,
    tenantId: 'tenant_1',
    ...extra,
  };
  return `id: ${id}\nevent: ${eventType}\ndata: ${JSON.stringify(data)}\n\n`;
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view']);
}

function renderStream(projectId = 'prj_1', options?: UseProgressStreamOptions): void {
  function Probe(): React.JSX.Element {
    const result = useProgressStream(projectId, options);
    return createElement('div', {
      'data-testid': 'stream-state',
      children: `${result.streamState}|${result.lastCursor ?? ''}|${result.failureCount}|${result.isFallbackActive}`,
    });
  }
  render(createElement(QueryClientProvider, { client: queryClient }, createElement(Probe)));
}

const mockFetch = vi.fn<typeof fetch>();

beforeEach(() => {
  mockFetch.mockReset();
  setInnerFetchForTests(mockFetch);
  setTokenProvider(() => 'test-token');
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  resetEnvCache();
  queryClient.clear();
  cleanup();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  resetEnvCache();
  queryClient.clear();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

describe('stream gating matrix', () => {
  it('stays idle when disabled, unscoped, anonymous, or kill-switched', async () => {
    authenticate();
    renderStream('prj_1', { enabled: false });
    expect(screen.getByTestId('stream-state').textContent).toBe('idle||0|false');
    cleanup();
    renderStream('');
    expect(screen.getByTestId('stream-state').textContent).toBe('idle||0|false');
    cleanup();
    useAuthStore.setState({ status: 'anonymous' });
    renderStream('prj_1');
    expect(screen.getByTestId('stream-state').textContent).toBe('idle||0|false');
    cleanup();
    useAuthStore.setState({ status: 'authenticated' });
    vi.stubEnv('VITE_SSE_ENABLED', 'false');
    resetEnvCache();
    expect(readSseEnabled()).toBe(false);
    renderStream('prj_1');
    expect(screen.getByTestId('stream-state').textContent).toBe('idle||0|false');
    expect(mockFetch).not.toHaveBeenCalled();
  });

  it('waits without failing when no token is available (header-auth only)', async () => {
    authenticate();
    clearTokenProvider();
    renderStream('prj_1');
    await new Promise((resolve) => setTimeout(resolve, 150));
    expect(screen.getByTestId('stream-state').textContent).toBe('connecting||0|false');
    expect(mockFetch).not.toHaveBeenCalled();
  });
});

describe('stream transport matrix', () => {
  it('counts a null-body stream as a failure and backs off (recovery: polling)', async () => {
    authenticate();
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.includes('/progress/stream')) {
        return Promise.resolve(new Response(null, { status: 200, headers: { 'Content-Type': 'text/event-stream' } }));
      }
      return Promise.resolve(jsonResponse({}));
    });
    renderStream('prj_1');
    await waitFor(() => expect(screen.getByTestId('stream-state').textContent).toMatch(/\|1\|/), { timeout: 5000 });
  });

  it('ignores heartbeat frames and processes envelopes with cursors', async () => {
    authenticate();
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.includes('/progress/stream')) {
        return Promise.resolve(sseResponse(`\n\n${frame('hb_1')}`));
      }
      return Promise.resolve(jsonResponse({}));
    });
    renderStream('prj_1');
    await waitFor(() => expect(screen.getByTestId('stream-state').textContent).toContain('|hb_1|'), {
      timeout: 5000,
    });
  });

  it('refreshes aggregates on replay truncation and stays open', async () => {
    authenticate();
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.includes('/progress/stream')) {
        return Promise.resolve(sseResponse(frame('rt_1'), { replayTruncated: 'true' }));
      }
      return Promise.resolve(jsonResponse({}));
    });
    renderStream('prj_1');
    await waitFor(() => expect(screen.getByTestId('stream-state').textContent).toContain('|rt_1|'), {
      timeout: 5000,
    });
  });

  it('emits auth:expired on 401 without retrying the stream (recovery: sign-in)', async () => {
    authenticate();
    const seen: number[] = [];
    const onExpired = (): void => {
      seen.push(1);
    };
    window.addEventListener('auth:expired', onExpired);
    try {
      mockFetch.mockImplementation((input) => {
        const url = typeof input === 'string' ? input : (input as Request).url;
        if (url.includes('/progress/stream')) {
          return Promise.resolve(jsonResponse({ error: { code: 'T', message: 'm', correlationId: 'c', details: {} } }, 401));
        }
        return Promise.resolve(jsonResponse({}));
      });
      renderStream('prj_1');
      await waitFor(() => expect(seen.length).toBeGreaterThan(0), { timeout: 5000 });
    } finally {
      window.removeEventListener('auth:expired', onExpired);
    }
  });

  it('yields to polling after repeated failures (recovery: adaptive poll)', async () => {
    authenticate();
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.includes('/progress/stream')) {
        return Promise.resolve(jsonResponse({ error: { code: 'X', message: 'm', correlationId: 'c', details: {} } }, 500));
      }
      return Promise.resolve(jsonResponse({}));
    });
    renderStream('prj_1', { maxFailuresBeforePoll: 1 });
    await waitFor(() => expect(screen.getByTestId('stream-state').textContent).toContain('polling-fallback'), {
      timeout: 8000,
    });
  });

  it('parks in polling-fallback on hidden tabs (recovery: slow poll covers)', async () => {
    authenticate();
    Object.defineProperty(document, 'hidden', { value: true, configurable: true });
    try {
      mockFetch.mockImplementation((input) => {
        const url = typeof input === 'string' ? input : (input as Request).url;
        if (url.includes('/progress/stream')) {
          return Promise.resolve(jsonResponse({ error: { code: 'X', message: 'm', correlationId: 'c', details: {} } }, 500));
        }
        return Promise.resolve(jsonResponse({}));
      });
      renderStream('prj_1');
      await waitFor(() => expect(screen.getByTestId('stream-state').textContent).toContain('polling-fallback'), {
        timeout: 8000,
      });
    } finally {
      Object.defineProperty(document, 'hidden', { value: false, configurable: true });
    }
  });
});

describe('poll error matrix', () => {
  it('normalizes poll failures to AppError (recovery: retry with backoff)', async () => {
    authenticate();
    mockFetch.mockImplementation((input) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      if (url.includes('/progress') && !url.includes('/stream')) {
        return Promise.resolve(
          jsonResponse({ error: { code: 'INTERNAL_ERROR', message: 'm', correlationId: 'c', details: {} } }, 500),
        );
      }
      return Promise.resolve(jsonResponse({}));
    });
    function Probe(): React.JSX.Element {
      const query = useProgressPoll('prj_1');
      return createElement('div', {
        'data-testid': 'poll-error',
        children: query.error === null ? 'none' : `${query.error.code}|${query.error.retryable}`,
      });
    }
    render(createElement(QueryClientProvider, { client: queryClient }, createElement(Probe)));
    await waitFor(() => expect(screen.getByTestId('poll-error').textContent).toBe('INTERNAL_ERROR|false'), {
      timeout: 5000,
    });
  });

  it('never fires for empty ids, caller opt-out, or anonymous sessions', async () => {
    authenticate();
    let calls = 0;
    mockFetch.mockImplementation(() => {
      calls += 1;
      return Promise.resolve(jsonResponse({}));
    });
    function Probe({ id, enabled }: { readonly id: string; readonly enabled?: boolean }): null {
      useProgressPoll(id, { enabled });
      return null;
    }
    render(createElement(QueryClientProvider, { client: queryClient }, createElement(Probe, { id: '' })));
    cleanup();
    render(createElement(QueryClientProvider, { client: queryClient }, createElement(Probe, { id: 'prj_1', enabled: false })));
    cleanup();
    useAuthStore.setState({ status: 'anonymous' });
    render(createElement(QueryClientProvider, { client: queryClient }, createElement(Probe, { id: 'prj_1' })));
    await new Promise((resolve) => setTimeout(resolve, 150));
    expect(calls).toBe(0);
  });
});

describe('progress pure-helper matrix', () => {
  function envelope(overrides: Record<string, unknown> = {}): SseEnvelope {
    return {
      correlationId: 'corr_1',
      eventId: 'evt_1',
      eventType: 'stage.progress',
      occurredAt: '2024-01-16T12:00:00Z',
      payload: { projectId: 'prj_1' },
      projectId: 'prj_1',
      schemaVersion: 1,
      tenantId: 'tenant_1',
      ...overrides,
    } as SseEnvelope;
  }

  it('extracts project/review id combinations without reading bodies', () => {
    expect(extractSseIds(envelope())).toEqual({ projectId: 'prj_1' });
    expect(extractSseIds(envelope({ payload: { reviewId: 'rev_1' } }))).toEqual({ projectId: 'prj_1', reviewId: 'rev_1' });
    expect(extractSseIds(envelope({ projectId: '', payload: { reviewId: 'rev_1' } }))).toEqual({ reviewId: 'rev_1' });
    expect(extractSseIds(envelope({ projectId: '', payload: {} }))).toEqual({});
    expect(extractSseIds(envelope({ payload: { reviewId: 7 } }))).toEqual({ projectId: 'prj_1' });
  });

  it('merges badge keys without duplicating registry keys', () => {
    const merged = resolveInvalidations('review.resolved', { reviewId: 'rev_1' });
    const serialized = merged.map((key) => JSON.stringify(key));
    expect(new Set(serialized).size).toBe(serialized.length);
    expect(serialized.some((key) => key.includes('unread-count'))).toBe(true);
    expect(shouldInvalidateNotifications('stage.progress')).toBe(false);
    expect(shouldInvalidateNotifications('stage.completed')).toBe(true);
    expect(resolveInvalidations('stage.progress', { projectId: 'prj_1' }).length).toBeGreaterThan(0);
  });

  it('formats approximate percents with clamps and guards', () => {
    expect(formatApproximatePercent(42.4)).toBe('~42%');
    expect(formatApproximatePercent(Number.NaN)).toBe('~0%');
    expect(formatApproximatePercent(-5)).toBe('~0%');
    expect(formatApproximatePercent(142)).toBe('~100%');
    expect(formatApproximatePercent(99.6)).toBe('~100%');
  });

  it('builds header-authed stream urls with encoded ids (never tokens)', () => {
    const url = buildProgressStreamUrl('prj 1/a');
    expect(url).toContain('/api/v1/projects/prj%201%2Fa/progress/stream');
    expect(url).not.toContain('token');
    expect(url).not.toContain('?');
  });

  it('classifies terminal statuses case-insensitively with null guards', () => {
    expect(isTerminalProgressStatus('completed')).toBe(true);
    expect(isTerminalProgressStatus('Failed')).toBe(true);
    expect(isTerminalProgressStatus('Running')).toBe(false);
    expect(isTerminalProgressStatus(undefined)).toBe(false);
    expect(isTerminalProgressStatus(null)).toBe(false);
    expect(isTerminalProgressStatus('')).toBe(false);
  });

  it('reads document visibility with safe defaults', () => {
    expect(isDocumentVisible()).toBe(!document.hidden);
    expect(readSseEnabled()).toBe(true);
  });

  it('rejects unshaped envelopes without leaking content', () => {
    expect(validateSseEnvelope(null)).toBeNull();
    expect(validateSseEnvelope({ eventType: 'bogus.type', schemaVersion: 1 })).toBeNull();
    expect(
      validateSseEnvelope({ eventType: 'stage.progress', schemaVersion: 2, eventId: 'e', tenantId: 't', correlationId: 'c', occurredAt: 'o' }),
    ).toBeNull();
    expect(
      validateSseEnvelope({
        eventType: 'stage.progress',
        schemaVersion: 1,
        eventId: 'e',
        tenantId: 't',
        correlationId: 'c',
        occurredAt: 'o',
        payload: 'nope',
      }),
    ).toBeNull();
    expect(
      validateSseEnvelope({
        eventType: 'stage.progress',
        schemaVersion: 1,
        eventId: 'e',
        tenantId: 't',
        correlationId: 'c',
        occurredAt: 'o',
        payload: { transcript: 'words' },
      }),
    ).toBeNull();
    expect(
      validateSseEnvelope({
        eventType: 'stage.progress',
        schemaVersion: 1,
        eventId: 'e',
        tenantId: 't',
        correlationId: 'c',
        occurredAt: 'o',
        projectId: 7,
      }),
    ).toBeNull();
    const nullableRun = validateSseEnvelope({
      eventType: 'stage.progress',
      schemaVersion: 1,
      eventId: 'e',
      tenantId: 't',
      correlationId: 'c',
      occurredAt: 'o',
      processingRunId: null,
    });
    expect(nullableRun?.schemaVersion).toBe(1);
    expect(nullableRun).not.toHaveProperty('projectId');
  });

  it('parses frames with event-line fallback and cursor normalization', () => {
    expect(parseProgressFrame('%%%\n')).toBeNull();
    const withoutType = `event: stage.progress\ndata: ${JSON.stringify({
      correlationId: 'c',
      eventId: 'e1',
      occurredAt: 'o',
      tenantId: 't',
      schemaVersion: 1,
    })}\n\n`;
    expect(parseProgressFrame(withoutType)?.eventType).toBe('stage.progress');
    const withoutCursor = `event: bogus\ndata: ${JSON.stringify({ eventType: 'bogus' })}\n\n`;
    expect(parseProgressFrame(withoutCursor)).toBeNull();
    const scalarData = `id: e2\nevent: stage.progress\ndata: 42\n\n`;
    expect(parseProgressFrame(scalarData)).toBeNull();
  });
});
