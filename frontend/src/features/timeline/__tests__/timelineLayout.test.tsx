// GAP-023: the timeline workspace degrades to list mode below the tablet
// breakpoint and renders the canvas timeline from 768px up. The viewport is
// stubbed through matchMedia — the same mechanism the hook subscribes to — so
// this pins the real branch the component takes.
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it } from 'vitest';
import '../../../i18n/i18n.js';
import { setInnerFetchForTests, setTokenProvider } from '../../../api/client/index.js';
import { TimelineWorkspace } from '../TimelineWorkspace.js';
import { useAuthStore } from '../../auth/authStore.js';
import { useAppStore } from '../../../stores/index.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function segment(index: number): Record<string, unknown> {
  const id = `seg_${String(index + 1).padStart(3, '0')}`;
  const startMs = index * 2000;
  return {
    id,
    projectId: 'prj_test',
    status: 'Ready',
    sequence: index + 1,
    startMs,
    endMs: startMs + 1800,
    speakerId: index % 2 === 0 ? 'spk_alice' : 'spk_bob',
    speakerLabel: index % 2 === 0 ? 'Alice' : 'Bob',
    selectionVersion: 2,
    reviewStatus: index === 0 ? 'Open' : 'Approved',
    qualityCodes: [],
    syncStatus: 'SyncAcceptable',
    confidence: 0.95,
    text: `line ${id}`,
  };
}

async function stubFetch(input: RequestInfo | URL): Promise<Response> {
  const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
  if (url.includes('/api/v1/') && url.includes('/segments')) {
    return jsonResponse({
      items: [segment(0), segment(1)],
      page: 1,
      pageSize: 200,
      total: 2,
      hasMore: false,
    });
  }

  return jsonResponse({});
}

function stubMatchMedia(matches: boolean): () => void {
  const original = window.matchMedia;
  window.matchMedia = ((query: string) => ({
    matches,
    media: query,
    onchange: null,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
  return () => {
    window.matchMedia = original;
  };
}

function renderWorkspace(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <TimelineWorkspace projectId="prj_test" />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('timeline responsive layout', () => {
  afterEach(() => {
    cleanup();
  });

  it('renders list mode below the tablet breakpoint', async () => {
    setTokenProvider(() => 'test-token');
    setInnerFetchForTests(stubFetch as typeof fetch);
    useAuthStore.setState({ status: 'authenticated' });
    useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit']);
    const restore = stubMatchMedia(true);
    try {
      renderWorkspace();

      // Either the transcript has not resolved yet (loading skeleton) or the
      // segments are already available; the list branch is what must render.
      const workspace = await screen.findByTestId('timeline-workspace');
      await waitFor(() => {
        expect(within(workspace).getByTestId('timeline-list-mode')).toBeDefined();
      });

      // The canvas surfaces are absent, not merely hidden: a phone never pays
      // for a pixel-grid waveform render.
      expect(within(workspace).queryByTestId('timeline-workspace-waveform')).toBeNull();
      expect(within(workspace).queryByTestId('timeline-workspace-timeline')).toBeNull();
      expect(within(workspace).getByTestId('timeline-workspace-player')).toBeDefined();
    } finally {
      restore();
    }
  });

  it('renders the canvas timeline from the tablet breakpoint up', async () => {
    setTokenProvider(() => 'test-token');
    setInnerFetchForTests(stubFetch as typeof fetch);
    useAuthStore.setState({ status: 'authenticated' });
    useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit']);
    const restore = stubMatchMedia(false);
    try {
      renderWorkspace();
      const workspace = await screen.findByTestId('timeline-workspace');
      await waitFor(() => {
        expect(within(workspace).getByTestId('timeline-workspace-waveform')).toBeDefined();
      });
      expect(within(workspace).getByTestId('timeline-workspace-timeline')).toBeDefined();
      expect(within(workspace).queryByTestId('timeline-list-mode')).toBeNull();
    } finally {
      restore();
    }
  });
});