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
import { getProgressPollInterval, useProgressPoll } from '../../useProgressStream.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function progressBody(status = 'Running'): Record<string, unknown> {
  return { percentApproximate: 42, projectId: 'prj_1', status, currentStage: 'Transcription' };
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view']);
}

function renderPoll(projectId = 'prj_1', enabled = true): void {
  function Probe(): null {
    useProgressPoll(projectId, { enabled });
    return null;
  }
  render(createElement(QueryClientProvider, { client: queryClient }, createElement(Probe)));
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
  vi.useRealTimers();
  cleanup();
  vi.restoreAllMocks();
});

describe('polling cadence with fake timers', () => {
  it('uses fast cadence when visible and slow when hidden', () => {
    expect(getProgressPollInterval({ isVisible: true, isTerminal: false })).toBe(3000);
    expect(getProgressPollInterval({ isVisible: false, isTerminal: false })).toBe(20000);
    expect(getProgressPollInterval({ isVisible: true, isTerminal: true })).toBe(false);
  });

  it('polls the progress endpoint on the fast cadence', async () => {
    vi.useFakeTimers();
    try {
      authenticate();
      let progressCalls = 0;
      const mockFetch = async (input: RequestInfo | URL): Promise<Response> => {
        const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
        if (url.includes('/progress') && !url.includes('/stream')) {
          progressCalls += 1;
          return jsonResponse(progressBody('Running'));
        }
        return jsonResponse({});
      };
      setInnerFetchForTests(mockFetch as typeof fetch);
      renderPoll();

      await vi.advanceTimersByTimeAsync(0);
      expect(progressCalls).toBe(1);

      await vi.advanceTimersByTimeAsync(3000);
      expect(progressCalls).toBe(2);

      await vi.advanceTimersByTimeAsync(3000);
      expect(progressCalls).toBe(3);
    } finally {
      vi.useRealTimers();
    }
  });

  it('stops polling at terminal states', async () => {
    vi.useFakeTimers();
    try {
      authenticate();
      let progressCalls = 0;
      const mockFetch = async (input: RequestInfo | URL): Promise<Response> => {
        const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
        if (url.includes('/progress') && !url.includes('/stream')) {
          progressCalls += 1;
          return jsonResponse(progressBody('Completed'));
        }
        return jsonResponse({});
      };
      setInnerFetchForTests(mockFetch as typeof fetch);
      renderPoll();

      await vi.advanceTimersByTimeAsync(0);
      expect(progressCalls).toBe(1);
      await vi.advanceTimersByTimeAsync(30000);
      expect(progressCalls).toBe(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('does not fetch when disabled', async () => {
    vi.useFakeTimers();
    try {
      authenticate();
      let progressCalls = 0;
      const mockFetch = async (input: RequestInfo | URL): Promise<Response> => {
        const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
        if (url.includes('/progress') && !url.includes('/stream')) {
          progressCalls += 1;
          return jsonResponse(progressBody('Running'));
        }
        return jsonResponse({});
      };
      setInnerFetchForTests(mockFetch as typeof fetch);
      renderPoll('prj_1', false);

      await vi.advanceTimersByTimeAsync(10000);
      expect(progressCalls).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('polling reads the progress query', () => {
  it('caches progress under the factory key with real timers', async () => {
    vi.useRealTimers();
    authenticate();
    const mockFetch = async (input: RequestInfo | URL): Promise<Response> => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
      if (url.includes('/progress') && !url.includes('/stream')) {
        return jsonResponse(progressBody('Running'));
      }
      return jsonResponse({});
    };
    setInnerFetchForTests(mockFetch as typeof fetch);
    renderPoll();
    await waitFor(() => {
      expect(queryClient.getQueryData(['projects', 'detail', 'prj_1', 'progress'])).toBeDefined();
    });
  });
});
