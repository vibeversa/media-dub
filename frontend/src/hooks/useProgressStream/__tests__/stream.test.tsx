import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, waitFor } from '@testing-library/react';
import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../../features/auth/authStore.js';
import { resetRestoreStartedForTests } from '../../../features/auth/useSession.js';
import { useWorkspaceStore } from '../../../features/processing/workspaceStore.js';
import { useProgressStream } from '../../useProgressStream.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function sseResponse(text: string): Response {
  return new Response(text, {
    status: 200,
    headers: { 'Content-Type': 'text/event-stream' },
  });
}

function frame(id: string, eventType = 'stage.progress'): string {
  const data = {
    correlationId: 'corr_1',
    eventId: id,
    eventType,
    occurredAt: '2024-01-16T12:00:00Z',
    payload: { projectId: 'prj_1', status: 'Running' },
    projectId: 'prj_1',
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
  cleanup();
  vi.restoreAllMocks();
});

describe('cursor catch-up and dedupe over reconnects', () => {
  it('dedupes repeated event ids within one stream', async () => {
    authenticate();
    const mockFetch = async (input: RequestInfo | URL): Promise<Response> => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
      if (url.includes('/progress/stream')) {
        return sseResponse(frame('dup_1') + frame('dup_1') + frame('dup_2'));
      }
      return jsonResponse({});
    };
    setInnerFetchForTests(mockFetch as typeof fetch);
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');

    function Probe(): null {
      useProgressStream('prj_1');
      return null;
    }
    render(createElement(QueryClientProvider, { client: queryClient }, createElement(Probe)));

    await new Promise((resolve) => setTimeout(resolve, 700));
    const progressCalls = invalidate.mock.calls.filter((call) => {
      const key = (call[0] as { queryKey?: unknown }).queryKey;
      return JSON.stringify(key).includes('progress');
    });
    // Two unique ids collapse to a single debounced batch.
    expect(progressCalls.length).toBe(1);
  });

  it('resumes from the cursor with Last-Event-ID on reconnect', async () => {
    authenticate();
    const lastEventHeaders: Array<string | null> = [];
    let calls = 0;
    const mockFetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
      if (url.includes('/progress/stream')) {
        calls += 1;
        const headers = new Headers(init?.headers);
        lastEventHeaders.push(headers.get('Last-Event-ID'));
        if (calls === 1) {
          return sseResponse(frame('cursor_1'));
        }
        return sseResponse(frame('cursor_2'));
      }
      return jsonResponse({});
    };
    setInnerFetchForTests(mockFetch as typeof fetch);

    function Probe(): null {
      useProgressStream('prj_1');
      return null;
    }
    render(createElement(QueryClientProvider, { client: queryClient }, createElement(Probe)));

    await waitFor(() => expect(calls).toBeGreaterThanOrEqual(1), { timeout: 3000 });
    expect(lastEventHeaders[0]).toBeNull();

    // First stream closes immediately; the hook backs off ~1s then resumes
    // with the cursor from the first event.
    await waitFor(() => expect(calls).toBeGreaterThanOrEqual(2), { timeout: 5000 });
    expect(lastEventHeaders[1]).toBe('cursor_1');
  });
});
