import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, waitFor } from '@testing-library/react';
import { createElement, useEffect } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { queryKeys } from '../../../api/queryKeys/index.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../../features/auth/authStore.js';
import { resetRestoreStartedForTests } from '../../../features/auth/useSession.js';
import { useWorkspaceStore } from '../../../features/processing/workspaceStore.js';
import { useProgressStream } from '../../useProgressStream.js';

function jsonResponse(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  });
}

function sseResponse(text: string): Response {
  return new Response(text, {
    status: 200,
    headers: { 'Content-Type': 'text/event-stream' },
  });
}

function envelopeFrame(id: string, eventType: string, projectId = 'prj_1'): string {
  const data = {
    correlationId: 'corr_1',
    eventId: id,
    eventType,
    occurredAt: '2024-01-16T12:00:00Z',
    payload: { projectId, status: 'Running' },
    projectId,
    schemaVersion: 1,
    tenantId: 'tenant_1',
  };
  return `id: ${id}\nevent: ${eventType}\ndata: ${JSON.stringify(data)}\n\n`;
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view']);
}

function renderStream(projectId = 'prj_1'): void {
  function Probe(): null {
    useProgressStream(projectId);
    return null;
  }
  render(
    createElement(QueryClientProvider, { client: queryClient }, createElement(Probe)),
  );
}

beforeEach(() => {
  setTokenProvider(() => 'test-token');
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  useWorkspaceStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  cleanup();
  vi.restoreAllMocks();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  useWorkspaceStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  vi.restoreAllMocks();
  try {
    Object.defineProperty(document, 'hidden', { value: false, configurable: true });
  } catch {
    // jsdom may not allow reassignment; ignore.
  }
});

describe('event to invalidation map', () => {
  it('invalidates workspace and progress on stage events', async () => {
    authenticate();
    const seen: string[] = [];
    const mockFetch = async (input: RequestInfo | URL): Promise<Response> => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
      if (url.includes('/progress/stream')) {
        seen.push(url);
        return sseResponse(envelopeFrame('evt_1', 'stage.progress') + envelopeFrame('evt_2', 'stage.completed'));
      }
      return jsonResponse({});
    };
    setInnerFetchForTests(mockFetch as typeof fetch);
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');

    renderStream();

    await waitFor(
      () => {
        expect(invalidate.mock.calls.length).toBeGreaterThan(0);
      },
      { timeout: 3000 },
    );
    const keys = invalidate.mock.calls.map((call) => (call[0] as { queryKey?: unknown }).queryKey);
    expect(keys).toContainEqual(queryKeys.progress.detail('prj_1'));
    expect(keys).toContainEqual(queryKeys.workspace.detail('prj_1'));
    expect(seen.length).toBeGreaterThanOrEqual(1);
  });

  it('invalidates notifications on completion events for the shell badge', async () => {
    authenticate();
    const mockFetch = async (input: RequestInfo | URL): Promise<Response> => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
      if (url.includes('/progress/stream')) {
        return sseResponse(envelopeFrame('evt_done', 'run.status_changed'));
      }
      return jsonResponse({});
    };
    setInnerFetchForTests(mockFetch as typeof fetch);
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');

    renderStream();

    await waitFor(
      () => {
        const keys = invalidate.mock.calls.map((call) => (call[0] as { queryKey?: unknown }).queryKey);
        expect(keys).toContainEqual(queryKeys.notifications.unreadCount());
      },
      { timeout: 3000 },
    );
    const keys = invalidate.mock.calls.map((call) => (call[0] as { queryKey?: unknown }).queryKey);
    expect(keys).toContainEqual(queryKeys.notifications.list());
  });

  it('coalesces rapid bursts through the 500ms debounce', async () => {
    authenticate();
    const mockFetch = async (input: RequestInfo | URL): Promise<Response> => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
      if (url.includes('/progress/stream')) {
        return sseResponse(
          envelopeFrame('b1', 'stage.progress') + envelopeFrame('b2', 'stage.progress') + envelopeFrame('b3', 'stage.progress'),
        );
      }
      return jsonResponse({});
    };
    setInnerFetchForTests(mockFetch as typeof fetch);
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');

    renderStream();

    // Bursts flush once after the debounce window, not once per frame.
    await new Promise((resolve) => setTimeout(resolve, 700));
    const progressCalls = invalidate.mock.calls.filter((call) => {
      const key = (call[0] as { queryKey?: unknown }).queryKey;
      return JSON.stringify(key) === JSON.stringify(queryKeys.progress.detail('prj_1'));
    });
    expect(progressCalls.length).toBe(1);
  });
});

describe('unknown-type tolerance', () => {
  it('ignores unknown event types without crashing', async () => {
    authenticate();
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const mockFetch = async (input: RequestInfo | URL): Promise<Response> => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
      if (url.includes('/progress/stream')) {
        const data = {
          correlationId: 'corr_1',
          eventId: 'evt_future',
          eventType: 'future.type',
          occurredAt: '2024-01-16T12:00:00Z',
          payload: {},
          schemaVersion: 1,
          tenantId: 'tenant_1',
        };
        return sseResponse(`id: evt_future\nevent: future.type\ndata: ${JSON.stringify(data)}\n\n`);
      }
      return jsonResponse({});
    };
    setInnerFetchForTests(mockFetch as typeof fetch);
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');

    try {
      renderStream();
      await new Promise((resolve) => setTimeout(resolve, 700));
      expect(invalidate).not.toHaveBeenCalled();
      expect(warn.mock.calls.length).toBeGreaterThanOrEqual(0);
    } finally {
      warn.mockRestore();
    }
  });

  it('ignores payloads with transcript or media bodies', async () => {
    authenticate();
    const mockFetch = async (input: RequestInfo | URL): Promise<Response> => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
      if (url.includes('/progress/stream')) {
        const data = {
          correlationId: 'corr_1',
          eventId: 'evt_bad',
          eventType: 'stage.progress',
          occurredAt: '2024-01-16T12:00:00Z',
          payload: { transcript: 'hello world' },
          projectId: 'prj_1',
          schemaVersion: 1,
          tenantId: 'tenant_1',
        };
        return sseResponse(`id: evt_bad\nevent: stage.progress\ndata: ${JSON.stringify(data)}\n\n`);
      }
      return jsonResponse({});
    };
    setInnerFetchForTests(mockFetch as typeof fetch);
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');

    renderStream();
    await new Promise((resolve) => setTimeout(resolve, 700));
    expect(invalidate).not.toHaveBeenCalled();
  });
});

describe('no local pipeline mirror (R1)', () => {
  it('produces only invalidations with no store writes', async () => {
    authenticate();
    const mockFetch = async (input: RequestInfo | URL): Promise<Response> => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
      if (url.includes('/progress/stream')) {
        return sseResponse(envelopeFrame('evt_store', 'stage.progress'));
      }
      return jsonResponse({});
    };
    setInnerFetchForTests(mockFetch as typeof fetch);
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    const setData = vi.spyOn(queryClient, 'setQueryData');
    const workspaceBefore = useWorkspaceStore.getState();

    renderStream();

    await waitFor(
      () => {
        expect(invalidate.mock.calls.length).toBeGreaterThan(0);
      },
      { timeout: 3000 },
    );
    expect(setData).not.toHaveBeenCalled();
    expect(useWorkspaceStore.getState().selectedTab).toBe(workspaceBefore.selectedTab);
    expect(useWorkspaceStore.getState().mainWidth).toBe(workspaceBefore.mainWidth);
    // No direct cache writes: events only invalidate.
    expect(queryClient.getQueryData(queryKeys.progress.detail('prj_1'))).toBeUndefined();
  });
});

describe('stream auth and 404 handling', () => {
  it('sends the token in the header and never in the URL', async () => {
    authenticate();
    const observed: Array<{ url: string; auth: string | null; lastEvent: string | null }> = [];
    const mockFetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
      if (url.includes('/progress/stream')) {
        const headers = new Headers(init?.headers);
        observed.push({ url, auth: headers.get('Authorization'), lastEvent: headers.get('Last-Event-ID') });
        return sseResponse(envelopeFrame('evt_auth', 'stage.progress'));
      }
      return jsonResponse({});
    };
    setInnerFetchForTests(mockFetch as typeof fetch);

    renderStream();
    await waitFor(() => expect(observed.length).toBeGreaterThan(0), { timeout: 3000 });
    expect(observed[0]?.auth).toBe('Bearer test-token');
    expect(observed[0]?.url).not.toContain('test-token');
    expect(observed[0]?.url).not.toContain('access_token');
  });

  it('stops without retry on 404 and invalidates the workspace', async () => {
    authenticate();
    let streamCalls = 0;
    const mockFetch = async (input: RequestInfo | URL): Promise<Response> => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
      if (url.includes('/progress/stream')) {
        streamCalls += 1;
        return jsonResponse(
          { error: { code: 'NOT_FOUND', message: 'gone', correlationId: 'c', details: {} } },
          404,
        );
      }
      return jsonResponse({});
    };
    setInnerFetchForTests(mockFetch as typeof fetch);
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');

    function Probe(): null {
      const result = useProgressStream('prj_gone');
      useEffect(() => {
        void result;
      }, [result]);
      return null;
    }
    render(createElement(QueryClientProvider, { client: queryClient }, createElement(Probe)));

    await waitFor(
      () => {
        const keys = invalidate.mock.calls.map((call) => (call[0] as { queryKey?: unknown }).queryKey);
        expect(keys).toContainEqual(queryKeys.workspace.detail('prj_gone'));
      },
      { timeout: 3000 },
    );
    const after404 = streamCalls;
    await new Promise((resolve) => setTimeout(resolve, 1200));
    expect(streamCalls).toBe(after404);
  });
});
