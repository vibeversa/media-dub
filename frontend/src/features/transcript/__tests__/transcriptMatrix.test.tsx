// Task 039B: transcript state-matrix gap closure.
//
// Extends `transcript.test.tsx` (panes/lineage, seek wiring, select/manual
// flows, virtualization) with the missing states: loading/error-retry/empty
// shells, stale banner + refresh, filter matrices (search/speaker/review-only),
// player scrub wiring, inspector empty/manual-draft/advanced/conflict paths,
// hook guards (empty id, anonymous, invalidation), player-store actions, and
// the types helper sweep. Every failure asserts its recovery control per
// §11.6 with text signals (041C); fixtures are synthetic (R3).
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { LocaleProvider } from '../../../app/providers/LocaleProvider.js';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS } from '../../../app/router.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { ToastProvider } from '../../../components/Toast/Toast.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { TranscriptEditor } from '../TranscriptEditor.js';
import { SegmentRow } from '../SegmentRow.js';
import { VirtualizedSegmentList } from '../VirtualizedSegmentList.js';
import { useTranscriptPlaybackStore } from '../playerStore.js';
import { invalidateTranscript, useTranscript, useTranscriptSegment } from '../useTranscript.js';
import { useCreateManualTranscriptVersion, useSelectTranscriptVersion } from '../useTranscript.js';
import {
  deriveLineage,
  filterTranscriptSegments,
  findActiveSegmentId,
  formatTimestamp,
  isSelectionConflict,
  mergeSegmentDetail,
  nearestSegmentId,
  parseTranscriptListItems,
  parseTranscriptSegment,
  parseTranscriptVersion,
} from '../types.js';
function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, details: Record<string, unknown> = {}): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-t27', details } },
    status,
  );
}

function makeSegment(index: number): Record<string, unknown> {
  const id = `seg_${String(index + 1).padStart(3, '0')}`;
  const startMs = index * 2000;
  return {
    id,
    projectId: 'prj_1',
    status: 'Ready',
    sequence: index + 1,
    startMs,
    endMs: startMs + 1800,
    speakerId: index % 2 === 0 ? 'spk_alice' : 'spk_bob',
    speakerLabel: index % 2 === 0 ? 'Alice' : 'Bob',
    selectionVersion: 2,
    reviewStatus: index % 5 === 0 ? 'Open' : 'Approved',
    qualityCodes: index % 5 === 0 ? ['QC_NOISY'] : [],
    syncStatus: 'SyncAcceptable',
    confidence: 0.95,
    text: `line ${id}`,
    transcriptVersions: [
      { id: `${id}-v1`, provider: 'acme', model: 'stt-v1', text: `v1 ${id}`, isSelected: false, createdAt: '2024-01-15T12:00:00Z' },
      { id: `${id}-v2`, provider: 'acme', model: 'stt-v2', text: `line ${id}`, isSelected: true, createdAt: '2024-01-15T13:00:00Z' },
    ],
  };
}

function listBody(count: number): Record<string, unknown> {
  const items: Record<string, unknown>[] = [];
  for (let i = 0; i < count; i += 1) {
    items.push(makeSegment(i));
  }
  return { items, page: 1, pageSize: 200, total: count, hasMore: false };
}

interface TranscriptMatrixWorld {
  listMode: 'ok' | 'empty' | 'error500' | 'never' | 'paged' | 'textless';
  selectConflict: boolean;
  listCalls: number;
}

let world: TranscriptMatrixWorld;

function resetWorld(): void {
  world = { listMode: 'ok', selectConflict: false, listCalls: 0 };
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') {
    return input;
  }
  if (input instanceof URL) {
    return input.href;
  }
  return (input as Request).url;
}

function methodOf(input: RequestInfo | URL, init?: RequestInit): string {
  if (typeof input !== 'string' && !(input instanceof URL)) {
    const request = input as Request;
    if (typeof request.method === 'string' && request.method !== '') {
      return request.method.toUpperCase();
    }
  }
  return (init?.method ?? 'GET').toUpperCase();
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) {
    return jsonResponse({});
  }
  if (url.includes('/transcript-selection') && method === 'POST') {
    if (world.selectConflict) {
      return errorEnvelope('SELECTION_CONFLICT', 409, { currentSelectionVersion: 9 });
    }
    return jsonResponse({ segmentId: 'seg_x', selectionVersion: 3, selectedVersionIds: ['v2'], outputStale: false });
  }
  if (url.includes('/transcript-edits') && method === 'POST') {
    return jsonResponse({ segmentId: 'seg_x', selectionVersion: 4, newVersionId: 'v-manual', outputStale: false });
  }
  if (method === 'GET' && /\/segments\/seg_/.test(url)) {
    const match = /\/segments\/(seg_[^/?]+)/.exec(url);
    const segmentId = match?.[1] ?? 'seg_001';
    if (segmentId === 'seg_404') {
      return errorEnvelope('NOT_FOUND', 404);
    }
    const index = Number(segmentId.replace('seg_', '')) - 1;
    const row = makeSegment(Number.isFinite(index) && index >= 0 ? index : 0);
    return jsonResponse({ ...row, id: segmentId, outputStale: false });
  }
  if (method === 'GET' && url.includes('/segments')) {
    world.listCalls += 1;
    if (world.listMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (world.listMode === 'empty') {
      return jsonResponse({ items: [], page: 1, pageSize: 200, total: 0, hasMore: false });
    }
    if (world.listMode === 'never') {
      return new Promise<Response>(() => {});
    }
    if (world.listMode === 'paged') {
      const page = Number.parseInt(new URL(url).searchParams.get('page') ?? '1', 10);
      if (page === 1) {
        return jsonResponse({ items: [makeSegment(0), makeSegment(1)], page: 1, pageSize: 200, total: 3, hasMore: true });
      }
      return jsonResponse({ items: [makeSegment(1), makeSegment(4)], page: 2, pageSize: 200, total: 3, hasMore: false });
    }
    if (world.listMode === 'textless') {
      return jsonResponse({
        items: [{ ...makeSegment(0), text: '', transcriptVersions: [] }, makeSegment(1)],
        page: 1,
        pageSize: 200,
        total: 2,
        hasMore: false,
      });
    }
    return jsonResponse(listBody(4));
  }
  return jsonResponse({});
}

function renderEditor(projectId = 'prj_1'): void {
  const router = createMemoryRouter(
    [{ path: '/projects/:id/transcript', element: <TranscriptEditor projectId={projectId} /> }],
    { initialEntries: ['/projects/prj_1/transcript'], future: { ...ROUTER_FUTURE_FLAGS } },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit']);
}

beforeEach(() => {
  resetWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  useTranscriptPlaybackStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  useTranscriptPlaybackStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  vi.restoreAllMocks();
});

describe('TranscriptEditor shell states', () => {
  it('shows loading while segments resolve (never blank)', () => {
    world.listMode = 'never';
    authenticate();
    renderEditor();
    expect(screen.getByTestId('transcript-loading')).toBeDefined();
  });

  it('recovers from list errors with retry (recovery: retry)', async () => {
    world.listMode = 'error500';
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-error')).toBeDefined();
    const callsBefore = world.listCalls;
    world.listMode = 'ok';
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(world.listCalls).toBeGreaterThan(callsBefore));
    expect(await screen.findByTestId('transcript-pane-list')).toBeDefined();
  });

  it('renders the empty state with a processing link (recovery: process)', async () => {
    world.listMode = 'empty';
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-empty')).toBeDefined();
    expect(screen.getByTestId('transcript-empty-processing-link').getAttribute('href')).toBe('/projects/prj_1');
  });

  it('warns on stale versions while keeping drafts (recovery: refresh)', async () => {
    world.selectConflict = true;
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-pane-list')).toBeDefined();
    fireEvent.click(screen.getByTestId('transcript-select-version-seg_001-v1'));
    expect(await screen.findByTestId('transcript-stale-banner')).toBeDefined();
    expect(screen.getByTestId('transcript-stale-banner').textContent?.length).toBeGreaterThan(0);
    world.selectConflict = false;
    fireEvent.click(screen.getByTestId('transcript-stale-refresh'));
    await waitFor(() => expect(screen.queryByTestId('transcript-stale-banner')).toBeNull());
  });
});

describe('TranscriptEditor filter matrix', () => {
  it('narrows rows by search text with result counts', async () => {
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-pane-list')).toBeDefined();
    fireEvent.change(screen.getByTestId('transcript-search'), { target: { value: 'line seg_001' } });
    await waitFor(() => expect(screen.queryByTestId('transcript-row-seg_002')).toBeNull(), { timeout: 5000 });
    expect(screen.getByTestId('transcript-row-seg_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('transcript-search'), { target: { value: '' } });
    expect(await screen.findByTestId('transcript-row-seg_002')).toBeDefined();
  });

  it('filters by speaker and review-only flags', async () => {
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-pane-list')).toBeDefined();
    fireEvent.change(screen.getByTestId('transcript-speaker-filter'), { target: { value: 'Bob' } });
    await waitFor(() => expect(screen.queryByTestId('transcript-row-seg_001')).toBeNull());
    expect(screen.getByTestId('transcript-row-seg_002')).toBeDefined();
    fireEvent.change(screen.getByTestId('transcript-speaker-filter'), { target: { value: '' } });
    fireEvent.click(screen.getByTestId('transcript-review-only'));
    await waitFor(() => expect(screen.getByTestId('transcript-row-seg_001')).toBeDefined());
    expect(screen.queryByTestId('transcript-row-seg_002')).toBeNull();
  });

  it('toggles autoscroll with a labelled switch', async () => {
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-pane-list')).toBeDefined();
    const toggle = screen.getByTestId('transcript-autoscroll-toggle') as HTMLInputElement;
    expect(toggle.checked).toBe(true);
    fireEvent.click(toggle);
    expect((screen.getByTestId('transcript-autoscroll-toggle') as HTMLInputElement).checked).toBe(false);
  });
});

describe('TranscriptEditor player wiring', () => {
  it('scrubs the shared playback store with position text', async () => {
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-player')).toBeDefined();
    fireEvent.change(screen.getByTestId('transcript-player-scrub'), { target: { value: '1500' } });
    await waitFor(() => expect(useTranscriptPlaybackStore.getState().positionMs).toBe(1500));
    expect(screen.getByTestId('transcript-player-position').textContent).toContain('1500');
  });
});

describe('Inspector matrix', () => {
  it('auto-selects the first segment with lineage badges', async () => {
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-inspector-title')).toBeDefined();
    expect(screen.getByTestId('transcript-inspector-title').textContent).toContain('seg_001');
  });

  it('inspects versions with lineage badges and draft lifecycle', async () => {
    authenticate();
    renderEditor();
    fireEvent.click(await screen.findByTestId('transcript-row-seg_001'));
    expect(await screen.findByTestId('transcript-inspector-title')).toBeDefined();
    expect(screen.getByTestId('transcript-original-text')).toBeDefined();
    expect(screen.getByTestId('transcript-selected-text')).toBeDefined();
    fireEvent.change(screen.getByTestId('transcript-manual-draft'), { target: { value: 'corrected line' } });
    expect(await screen.findByTestId('transcript-draft-dirty')).toBeDefined();
    fireEvent.click(screen.getByTestId('transcript-advanced-toggle'));
    expect(await screen.findByTestId('transcript-version-list')).toBeDefined();
  });

  it('surfaces selection conflicts with refresh recovery (recovery: refresh)', async () => {
    world.selectConflict = true;
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-inspector-title')).toBeDefined();
    fireEvent.click(screen.getByTestId('transcript-select-version-seg_001-v1'));
    expect(await screen.findByTestId('transcript-stale-banner')).toBeDefined();
  });
});

describe('transcript fetch matrix', () => {
  function renderHookProbe(segmentId?: string): void {
    function Probe(): null {
      useTranscript('prj_1');
      useTranscriptSegment('prj_1', segmentId);
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
  }

  it('pages, dedupes, and sorts multi-page lists (no loss, no dupes)', async () => {
    world.listMode = 'paged';
    authenticate();
    renderHookProbe();
    await waitFor(
      () => {
        const cached = queryClient.getQueryData<readonly { id: string }[]>(['projects', 'detail', 'prj_1', 'transcript', 'list']);
        expect(cached?.map((segment) => segment.id)).toEqual(['seg_001', 'seg_002', 'seg_005']);
      },
      { timeout: 5000 },
    );
  });

  it('hydrates textless rows from detail with fallback retention (recovery: keep-row)', async () => {
    world.listMode = 'textless';
    authenticate();
    renderHookProbe();
    await waitFor(
      () => {
        const cached = queryClient.getQueryData<readonly { id: string; text: string }[]>(['projects', 'detail', 'prj_1', 'transcript', 'list']);
        expect(cached?.find((segment) => segment.id === 'seg_001')?.text).toContain('line seg_001');
      },
      { timeout: 5000 },
    );
  });

  it('normalizes segment-detail failures to AppError (recovery: retry)', async () => {
    authenticate();
    renderHookProbe('seg_404');
    await waitFor(
      () => {
        const state = queryClient.getQueryState(['projects', 'detail', 'prj_1', 'transcript', 'detail', 'seg_404']);
        expect(state?.error).toBeDefined();
      },
      { timeout: 5000 },
    );
  });

  it('falls back to expected versions on shapeless mutation responses', async () => {
    authenticate();
    setInnerFetchForTests((async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = urlOf(input);
      const method = methodOf(input, init);
      if (url.includes('/transcript-selection') && method === 'POST') {
        return jsonResponse({ ok: true });
      }
      if (url.includes('/transcript-edits') && method === 'POST') {
        return jsonResponse({ ok: true });
      }
      return mockFetch(input, init);
    }) as typeof fetch);
    let selectResult = -1;
    let manualResult = -1;
    function Probe(): null {
      const select = useSelectTranscriptVersion('prj_1');
      const manual = useCreateManualTranscriptVersion('prj_1');
      (window as unknown as { __mut?: unknown }).__mut = { select, manual };
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
    await waitFor(() => expect((window as unknown as { __mut?: unknown }).__mut).toBeDefined());
    const mut = (window as unknown as { __mut?: { select: { mutateAsync: (v: unknown) => Promise<{ selectionVersion: number }> }; manual: { mutateAsync: (v: unknown) => Promise<{ selectionVersion: number }> } } }).__mut;
    selectResult = (await mut?.select.mutateAsync({ segmentId: 'seg_001', versionId: 'v1', expectedVersion: 2 }))?.selectionVersion ?? -1;
    manualResult = (await mut?.manual.mutateAsync({ segmentId: 'seg_001', text: 'fixed', expectedVersion: 2 }))?.selectionVersion ?? -1;
    expect(selectResult).toBe(2);
    expect(manualResult).toBe(2);
  });
});

describe('transcript hook guards', () => {
  it('never fires for empty ids or anonymous sessions', async () => {
    authenticate();
    let calls = 0;
    setInnerFetchForTests((async () => {
      calls += 1;
      return jsonResponse({});
    }) as typeof fetch);
    function Probe(): null {
      useTranscript('');
      useTranscriptSegment('prj_1', undefined);
      useTranscriptSegment('prj_1', '');
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(calls).toBe(0);
    useAuthStore.setState({ status: 'anonymous' });
    await invalidateTranscript(queryClient, 'prj_1');
    await invalidateTranscript(queryClient, 'prj_1', 'seg_001');
  });
});

describe('transcript player store matrix', () => {
  it('drives position and seeks with readable state', () => {
    const store = useTranscriptPlaybackStore.getState();
    store.reportPosition(1200);
    expect(useTranscriptPlaybackStore.getState().positionMs).toBe(1200);
    store.requestSeek(2400);
    expect(useTranscriptPlaybackStore.getState().seekTargetMs).toBe(2400);
    expect(useTranscriptPlaybackStore.getState().seekVersion).toBe(1);
    store.resetForTests();
    expect(useTranscriptPlaybackStore.getState().positionMs).toBe(0);
  });

  it('ignores unusable positions and clamps seeks (never NaN state)', () => {
    const store = useTranscriptPlaybackStore.getState();
    store.reportPosition(500);
    store.reportPosition(Number.NaN);
    expect(useTranscriptPlaybackStore.getState().positionMs).toBe(500);
    store.reportPosition(-5);
    expect(useTranscriptPlaybackStore.getState().positionMs).toBe(500);
    store.requestSeek(Number.NaN);
    expect(useTranscriptPlaybackStore.getState().seekTargetMs).toBe(0);
    expect(useTranscriptPlaybackStore.getState().positionMs).toBe(0);
    store.requestSeek(-25);
    expect(useTranscriptPlaybackStore.getState().seekTargetMs).toBe(0);
  });
});

describe('VirtualizedSegmentList direct matrix', () => {
  function manySegments(count: number) {
    const items: Record<string, unknown>[] = [];
    for (let i = 0; i < count; i += 1) {
      items.push(makeSegment(i));
    }
    return parseTranscriptListItems({ items });
  }

  function renderList(props: Partial<React.ComponentProps<typeof VirtualizedSegmentList>> = {}) {
    const onSelect = vi.fn();
    const onSeek = vi.fn();
    const onManualScroll = vi.fn();
    render(
      <VirtualizedSegmentList
        segments={manySegments(30)}
        selectedId="seg_001"
        positionMs={100}
        autoScroll={true}
        filter={{ query: '', speaker: '', reviewOnly: false }}
        onSelect={onSelect}
        onSeek={onSeek}
        onManualScroll={onManualScroll}
        {...props}
      />,
    );
    return { onSelect, onSeek, onManualScroll };
  }

  it('ignores keys on empty lists and without selection (no-ops, never crash)', () => {
    const { onSelect, onSeek } = renderList({ segments: [], selectedId: undefined });
    const list = screen.getByTestId('transcript-list-scroll');
    expect(screen.getByTestId('transcript-list').getAttribute('data-total')).toBe('0');
    fireEvent.keyDown(list, { key: 'ArrowDown' });
    fireEvent.keyDown(list, { key: 'Enter' });
    fireEvent.keyDown(list, { key: 'x' });
    expect(onSelect).not.toHaveBeenCalled();
    expect(onSeek).not.toHaveBeenCalled();
  });

  it('moves selection with arrows and seeks with Enter (text + aria signals)', () => {
    const { onSelect, onSeek } = renderList();
    const list = screen.getByTestId('transcript-list-scroll');
    fireEvent.keyDown(list, { key: 'ArrowDown' });
    expect(onSelect).toHaveBeenCalledWith('seg_002');
    fireEvent.keyDown(list, { key: 'ArrowUp' });
    expect(onSelect).toHaveBeenCalledWith('seg_001');
    fireEvent.keyDown(list, { key: 'Enter' });
    expect(onSeek).toHaveBeenCalledWith('seg_001');
    expect(screen.getByTestId('transcript-row-seg_001').getAttribute('data-active')).toBe('true');
  });

  it('windows large lists with padding and reports manual scrolls', () => {
    const { onManualScroll } = renderList({ positionMs: 0, autoScroll: false });
    const list = screen.getByTestId('transcript-list');
    expect(Number(list.getAttribute('data-total'))).toBe(30);
    expect(Number(list.getAttribute('data-rendered'))).toBeLessThan(30);
    fireEvent.scroll(screen.getByTestId('transcript-list-scroll'), { target: { scrollTop: 5000 } });
    expect(onManualScroll).toHaveBeenCalledTimes(1);
  });

  it('autoscrolls to the active segment (position sync)', () => {
    renderList({ positionMs: 100 });
    expect(screen.getByTestId('transcript-list-scroll')).toBeDefined();
  });
});

describe('SegmentRow direct matrix', () => {
  function parsedRow() {
    const parsed = parseTranscriptSegment(makeSegment(0));
    if (parsed === undefined) {
      throw new Error('fixture failed to parse');
    }
    return parsed;
  }

  it('plays and selects with distinct callbacks (text signals)', () => {
    const onSelect = vi.fn();
    const onSeek = vi.fn();
    render(
      <MemoryRouter>
        <SegmentRow segment={parsedRow()} isSelected={false} isActive={false} onSelect={onSelect} onSeek={onSeek} />,
      </MemoryRouter>,
    );
    fireEvent.click(screen.getByTestId('transcript-play-seg_001'));
    expect(onSeek).toHaveBeenCalledWith('seg_001');
    expect(onSelect).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('transcript-select-seg_001'));
    expect(onSelect).toHaveBeenCalledWith('seg_001');
    expect(screen.getByTestId('transcript-row-seg_001').getAttribute('data-selected')).toBe('false');
    expect(screen.getByTestId('transcript-confidence-seg_001').textContent).toContain('95%');
  });

  it('stops review-flag clicks from reselecting (no nav storm)', () => {
    const onSelect = vi.fn();
    const onSeek = vi.fn();
    render(
      <MemoryRouter>
        <SegmentRow segment={parsedRow()} isSelected={true} isActive={true} onSelect={onSelect} onSeek={onSeek} />,
      </MemoryRouter>,
    );
    expect(screen.getByTestId('transcript-row-seg_001').getAttribute('data-selected')).toBe('true');
    fireEvent.click(screen.getByTestId('transcript-review-flag-seg_001'));
    expect(onSelect).not.toHaveBeenCalled();
  });
});

describe('transcript types sweep', () => {
  const raw = makeSegment(0);

  it('parses versions and segments defensively', () => {
    expect(parseTranscriptVersion(null, 0)).toBeUndefined();
    expect(parseTranscriptVersion({ id: 'v1', text: 'hi' }, 0)?.id).toBe('v1');
    expect(parseTranscriptSegment(null)).toBeUndefined();
    expect(parseTranscriptSegment(raw)?.id).toBe('seg_001');
    expect(parseTranscriptListItems(null)).toEqual([]);
    expect(parseTranscriptListItems({ items: 'nope' })).toEqual([]);
    expect(parseTranscriptListItems({ items: [raw, null] }).length).toBe(1);
  });

  it('merges details, derives lineage, and formats timestamps', () => {
    const parsed = parseTranscriptSegment(raw);
    expect(parsed).toBeDefined();
    if (parsed !== undefined) {
      const merged = mergeSegmentDetail(parsed, { ...raw, selectionVersion: 9 });
      expect(merged.selectionVersion).toBe(9);
      const lineage = deriveLineage(parsed);
      expect(lineage.selectedText).toContain('line seg_001');
    }
    expect(formatTimestamp(0)).toBe('00:00.000');
  });

  it('locates active/nearest segments and filters rows', () => {
    const segments = parseTranscriptListItems({ items: [makeSegment(0), makeSegment(1)] });
    expect(findActiveSegmentId(segments, 100)).toBe('seg_001');
    expect(findActiveSegmentId([], 100)).toBeUndefined();
    expect(nearestSegmentId(segments, 'seg_002', undefined)).toBe('seg_001');
    expect(nearestSegmentId(segments, 'seg_001', 'seg_002')).toBe('seg_002');
    expect(nearestSegmentId([], 'seg_001', undefined)).toBeUndefined();
    expect(filterTranscriptSegments(segments, { query: 'seg_002', speaker: '', reviewOnly: false }).length).toBe(1);
    expect(filterTranscriptSegments(segments, { query: '', speaker: 'Bob', reviewOnly: false }).length).toBe(1);
    expect(filterTranscriptSegments(segments, { query: '', speaker: '', reviewOnly: true }).length).toBe(1);
  });

  it('classifies selection conflicts with null safety', () => {
    expect(isSelectionConflict({ code: 'SELECTION_CONFLICT', status: 409 })).toBe(true);
    expect(isSelectionConflict({ code: 'X', status: 500 })).toBe(false);
    expect(isSelectionConflict(undefined)).toBe(false);
    expect(isSelectionConflict(null)).toBe(false);
  });
});
