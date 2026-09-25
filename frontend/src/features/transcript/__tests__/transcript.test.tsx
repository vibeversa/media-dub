import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
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
import { useTranscriptPlaybackStore } from '../playerStore.js';
import {
  deriveLineage,
  filterTranscriptSegments,
  findActiveSegmentId,
  formatTimestamp,
  isSelectionConflict,
  parseTranscriptSegment,
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
  const speaker = index % 2 === 0 ? 'Alice' : 'Bob';
  const needsReview = index % 5 === 0;
  const confidence = needsReview ? 0.4 : 0.95;
  const v1 = `v1-original-${id}`;
  const v2 = `v2-selected-${id}`;
  const versions =
    index % 3 === 0
      ? [
          { id: `${id}-v1`, provider: 'acme', model: 'stt-v1', text: v1, isSelected: false, createdAt: '2024-01-15T12:00:00Z' },
          { id: `${id}-v2`, provider: 'acme', model: 'stt-v2', text: v2, isSelected: true, createdAt: '2024-01-15T13:00:00Z' },
          { id: `${id}-v3`, provider: 'manual', model: 'manual-review-v1', text: `manual-${id}`, isSelected: false, createdAt: '2024-01-15T14:00:00Z' },
        ]
      : [
          { id: `${id}-v1`, provider: 'acme', model: 'stt-v1', text: v1, isSelected: false, createdAt: '2024-01-15T12:00:00Z' },
          { id: `${id}-v2`, provider: 'acme', model: 'stt-v2', text: v2, isSelected: true, createdAt: '2024-01-15T13:00:00Z' },
        ];
  return {
    id,
    projectId: 'prj_1',
    status: 'Ready',
    sequence: index + 1,
    startMs,
    endMs: startMs + 1800,
    speakerId: index % 2 === 0 ? 'spk_alice' : 'spk_bob',
    speakerLabel: speaker,
    selectionVersion: 2,
    reviewStatus: needsReview ? 'Open' : 'Approved',
    qualityCodes: needsReview ? ['QC_NOISY'] : [],
    syncStatus: 'SyncAcceptable',
    confidence,
    text: v2,
    transcriptVersions: versions,
  };
}

function listBody(count: number): Record<string, unknown> {
  const items: Record<string, unknown>[] = [];
  for (let i = 0; i < count; i += 1) {
    items.push(makeSegment(i));
  }
  return { items, page: 1, pageSize: 200, total: count, hasMore: false };
}

function detailBody(segmentId: string): Record<string, unknown> {
  const index = Number(segmentId.replace('seg_', '')) - 1;
  const safe = Number.isFinite(index) && index >= 0 ? index : 0;
  const summary = makeSegment(safe);
  return { ...summary, id: segmentId, outputStale: false };
}

type MutationBehavior = 'ok' | 'conflict' | 'validation-error';

interface TranscriptWorld {
  count: number;
  empty: boolean;
  selectBehavior: MutationBehavior;
  manualBehavior: MutationBehavior;
  selectBodies: unknown[];
  manualBodies: unknown[];
  methods: string[];
  listCalls: number;
}

function newWorld(overrides: Partial<TranscriptWorld> = {}): TranscriptWorld {
  return {
    count: 4,
    empty: false,
    selectBehavior: 'ok',
    manualBehavior: 'ok',
    selectBodies: [],
    manualBodies: [],
    methods: [],
    listCalls: 0,
    ...overrides,
  };
}

let world: TranscriptWorld = newWorld();

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

function bodyOf(init?: RequestInit): unknown {
  if (typeof init?.body !== 'string') {
    return undefined;
  }
  try {
    return JSON.parse(init.body as string) as unknown;
  } catch {
    return undefined;
  }
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) {
    return jsonResponse({});
  }
  if (url.includes('/segments/') && url.includes('/transcript-selection') && method === 'POST') {
    world.methods.push(`POST selection`);
    world.selectBodies.push(bodyOf(init));
    if (world.selectBehavior === 'conflict') {
      return errorEnvelope('SELECTION_CONFLICT', 409, {
        currentSelectionVersion: 9,
        currentVersionIds: { transcriptVersionId: 'v-current', translationVersionId: null },
      });
    }
    return jsonResponse({ segmentId: 'seg_x', selectionVersion: 3, selectedVersionIds: ['v2'], newVersionId: null, outputStale: false });
  }
  if (url.includes('/segments/') && url.includes('/transcript-edits') && method === 'POST') {
    world.methods.push(`POST edits`);
    world.manualBodies.push(bodyOf(init));
    if (world.manualBehavior === 'conflict') {
      return errorEnvelope('SELECTION_CONFLICT', 409, { currentSelectionVersion: 9 });
    }
    if (world.manualBehavior === 'validation-error') {
      return errorEnvelope('SEGMENT_TEXT_EMPTY', 400);
    }
    return jsonResponse({ segmentId: 'seg_x', selectionVersion: 4, newVersionId: 'v-manual', outputStale: false });
  }
  if (url.includes('/segments/') && (method === 'PUT' || method === 'PATCH')) {
    world.methods.push(`${method} forbidden`);
    return errorEnvelope('VALIDATION_FAILED', 400);
  }
  const segmentDetail = /\/segments\/([^/?]+)(\?|$)/.exec(url);
  const isDetail = segmentDetail !== null && url.includes('/segments/') && !url.includes('/transcript') && method === 'GET' && !url.endsWith('/segments') && !url.includes('/segments?') && !url.includes('/segments/?');
  void isDetail;
  if (method === 'GET' && /\/segments\/seg_/.test(url)) {
    const match = /\/segments\/(seg_[^/?]+)/.exec(url);
    const segmentId = match?.[1] ?? 'seg_001';
    return jsonResponse(detailBody(segmentId));
  }
  if (method === 'GET' && url.includes('/segments')) {
    world.listCalls += 1;
    world.methods.push('GET list');
    if (world.empty) {
      return jsonResponse({ items: [], page: 1, pageSize: 200, total: 0, hasMore: false });
    }
    return jsonResponse(listBody(world.count));
  }
  return jsonResponse({});
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit']);
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

beforeEach(() => {
  world = newWorld();
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

describe('pure transcript helpers', () => {
  it('formats timestamps ms-accurately', () => {
    expect(formatTimestamp(0)).toBe('00:00.000');
    expect(formatTimestamp(61_250)).toBe('01:01.250');
    expect(formatTimestamp(3_600_000)).toBe('60:00.000');
  });

  it('parses summaries defensively and derives lineage', () => {
    const parsed = parseTranscriptSegment(makeSegment(0));
    expect(parsed?.selectionVersion).toBe(2);
    expect(parsed?.text).toContain('v2-selected');
    const lineage = deriveLineage(parsed!);
    expect(lineage.originalText).toContain('v1-original');
    expect(lineage.selectedBadge).toMatch(/selected v\d+/);
    expect(lineage.hasManual).toBe(true);
  });

  it('detects selection conflicts by code or status', () => {
    expect(isSelectionConflict({ code: 'SELECTION_CONFLICT', status: 409 })).toBe(true);
    expect(isSelectionConflict({ code: 'SELECTION_CONFLICT' })).toBe(true);
    expect(isSelectionConflict({ status: 409 })).toBe(true);
    expect(isSelectionConflict({ code: 'VALIDATION_FAILED', status: 400 })).toBe(false);
    expect(isSelectionConflict(undefined)).toBe(false);
  });

  it('tracks the active segment and clears beyond range', () => {
    const segments = [makeSegment(0), makeSegment(1)].map((raw) => parseTranscriptSegment(raw)!);
    expect(findActiveSegmentId(segments, 500)).toBe('seg_001');
    expect(findActiveSegmentId(segments, 2500)).toBe('seg_002');
    expect(findActiveSegmentId(segments, 999_999)).toBeUndefined();
  });

  it('filters by speaker, text, and review flag', () => {
    const segments = [makeSegment(0), makeSegment(1), makeSegment(5)].map((raw) => parseTranscriptSegment(raw)!);
    expect(filterTranscriptSegments(segments, { query: '', speaker: '', reviewOnly: true }).length).toBeGreaterThan(0);
    expect(filterTranscriptSegments(segments, { query: '', speaker: 'Alice', reviewOnly: false }).every((s) => s.speakerLabel === 'Alice')).toBe(true);
    expect(filterTranscriptSegments(segments, { query: 'v2-selected-seg_001', speaker: '', reviewOnly: false })).toHaveLength(1);
  });
});

describe('TranscriptEditor panes and lineage (R5)', () => {
  it('renders three panes with lineage badges per segment', async () => {
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-editor')).toBeDefined();
    expect(await screen.findByTestId('transcript-pane-player')).toBeDefined();
    expect(await screen.findByTestId('transcript-pane-list')).toBeDefined();
    expect(await screen.findByTestId('transcript-pane-inspector')).toBeDefined();
    expect(await screen.findByTestId('transcript-row-seg_001')).toBeDefined();
    expect(screen.getByTestId('transcript-badge-original-seg_001').textContent).toBe('original');
    expect(screen.getByTestId('transcript-badge-selected-seg_001').textContent).toMatch(/selected/);
    expect(screen.getByTestId('transcript-badge-manual-seg_001')).toBeDefined();
    expect(screen.getByTestId('transcript-player')).toBeDefined();
  });

  it('shows EmptyState with a processing link when the transcript is empty', async () => {
    world.empty = true;
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-empty')).toBeDefined();
    expect(screen.getByTestId('transcript-empty-processing-link')).toBeDefined();
    expect(screen.queryByTestId('transcript-list')).toBeNull();
  });
});

describe('seek-on-select wiring (R3)', () => {
  it('selecting a row seeks the shared player ms-accurately', async () => {
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-row-seg_002')).toBeDefined();
    fireEvent.click(screen.getByTestId('transcript-select-seg_002'));
    const player = screen.getByTestId('transcript-player');
    expect(player.getAttribute('data-seek-target')).toBe('2000');
    expect(useTranscriptPlaybackStore.getState().seekTargetMs).toBe(2000);
    expect(useTranscriptPlaybackStore.getState().positionMs).toBe(2000);
  });

  it('highlights the active segment during playback and yields to manual scroll', async () => {
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-row-seg_001')).toBeDefined();
    useTranscriptPlaybackStore.getState().reportPosition(500);
    await waitFor(() => {
      expect(screen.getByTestId('transcript-row-seg_001').getAttribute('data-active')).toBe('true');
    });
    const scroll = screen.getByTestId('transcript-list-scroll');
    fireEvent.scroll(scroll, { target: { scrollTop: 400 } });
    expect((screen.getByTestId('transcript-autoscroll-toggle') as HTMLInputElement).checked).toBe(false);
    fireEvent.click(screen.getByTestId('transcript-autoscroll-toggle'));
    expect((screen.getByTestId('transcript-autoscroll-toggle') as HTMLInputElement).checked).toBe(true);
  });

  it('moves selection with Up/Down and seeks with Enter', async () => {
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-row-seg_001')).toBeDefined();
    const list = screen.getByTestId('transcript-list-scroll');
    fireEvent.keyDown(list, { key: 'ArrowDown' });
    await waitFor(() => {
      expect(screen.getByTestId('transcript-row-seg_002').getAttribute('data-selected')).toBe('true');
    });
    fireEvent.keyDown(list, { key: 'Enter' });
    expect(useTranscriptPlaybackStore.getState().seekTargetMs).toBe(2000);
  });
});

describe('select-version flow (R2)', () => {
  it('sends expectedVersion and invalidates the transcript on success', async () => {
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-row-seg_001')).toBeDefined();
    const callsBefore = world.listCalls;
    const selectButton = await screen.findByTestId('transcript-select-version-seg_001-v1');
    fireEvent.click(selectButton);
    await waitFor(() => {
      expect(world.selectBodies.length).toBeGreaterThan(0);
    });
    const body = world.selectBodies[0] as Record<string, unknown>;
    expect(body['expectedVersion']).toBe(2);
    expect(body['selectedVersionIds']).toEqual(['seg_001-v1']);
    await waitFor(() => {
      expect(world.listCalls).toBeGreaterThan(callsBefore);
    });
    expect(screen.queryByTestId('transcript-stale-banner')).toBeNull();
  });

  it('shows the stale banner, refetches, and keeps the draft on 409', async () => {
    world.selectBehavior = 'conflict';
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-row-seg_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('transcript-manual-draft'), { target: { value: 'keep me' } });
    const selectButton = await screen.findByTestId('transcript-select-version-seg_001-v1');
    fireEvent.click(selectButton);
    expect(await screen.findByTestId('transcript-stale-banner')).toBeDefined();
    expect((screen.getByTestId('transcript-manual-draft') as HTMLTextAreaElement).value).toBe('keep me');
    const callsBefore = world.listCalls;
    fireEvent.click(screen.getByTestId('transcript-stale-refresh'));
    await waitFor(() => {
      expect(world.listCalls).toBeGreaterThan(callsBefore);
    });
    expect(screen.queryByTestId('transcript-stale-banner')).toBeNull();
  });
});

describe('manual-version flow (R1 optimistic draft + rollback)', () => {
  it('creates a manual version with expectedVersion and clears the draft', async () => {
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-manual-draft')).toBeDefined();
    fireEvent.change(screen.getByTestId('transcript-manual-draft'), { target: { value: 'corrected line' } });
    const callsBefore = world.listCalls;
    fireEvent.click(screen.getByTestId('transcript-manual-save'));
    await waitFor(() => {
      expect(world.manualBodies.length).toBeGreaterThan(0);
    });
    const body = world.manualBodies[0] as Record<string, unknown>;
    expect(body['expectedVersion']).toBe(2);
    expect(body['text']).toBe('corrected line');
    await waitFor(() => {
      expect((screen.getByTestId('transcript-manual-draft') as HTMLTextAreaElement).value).toBe('');
    });
    await waitFor(() => {
      expect(world.listCalls).toBeGreaterThan(callsBefore);
    });
  });

  it('rolls back to the preserved draft on 409 without auto-resubmitting', async () => {
    world.manualBehavior = 'conflict';
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-manual-draft')).toBeDefined();
    fireEvent.change(screen.getByTestId('transcript-manual-draft'), { target: { value: 'do not lose me' } });
    fireEvent.click(screen.getByTestId('transcript-manual-save'));
    expect(await screen.findByTestId('transcript-stale-banner')).toBeDefined();
    expect((screen.getByTestId('transcript-manual-draft') as HTMLTextAreaElement).value).toBe('do not lose me');
    expect(world.manualBodies).toHaveLength(1);
  });

  it('never issues PUT or PATCH for version writes (R1)', async () => {
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-manual-draft')).toBeDefined();
    fireEvent.change(screen.getByTestId('transcript-manual-draft'), { target: { value: 'history stays immutable' } });
    fireEvent.click(screen.getByTestId('transcript-manual-save'));
    await waitFor(() => {
      expect(world.manualBodies.length).toBeGreaterThan(0);
    });
    const forbidden = world.methods.filter((method) => method.startsWith('PUT') || method.startsWith('PATCH'));
    expect(forbidden).toEqual([]);
    expect(world.methods).toContain('POST edits');
  });
});

describe('virtualization + filtering (R6)', () => {
  it('windows 1000 rows so only the visible slice renders', async () => {
    world.count = 1000;
    authenticate();
    renderEditor();
    const list = await screen.findByTestId('transcript-list');
    expect(list.getAttribute('data-total')).toBe('1000');
    const rendered = Number(list.getAttribute('data-rendered') ?? '0');
    expect(rendered).toBeGreaterThan(0);
    expect(rendered).toBeLessThan(100);
  });

  it('filters by speaker, text, and review flag', async () => {
    authenticate();
    renderEditor();
    expect(await screen.findByTestId('transcript-list')).toBeDefined();
    const list = (): HTMLElement => screen.getByTestId('transcript-list');
    expect(list().getAttribute('data-total')).toBe('4');
    fireEvent.click(screen.getByTestId('transcript-review-only'));
    await waitFor(() => {
      expect(Number(list().getAttribute('data-total'))).toBeLessThan(4);
    });
    fireEvent.click(screen.getByTestId('transcript-review-only'));
    fireEvent.change(screen.getByTestId('transcript-speaker-filter'), { target: { value: 'Alice' } });
    await waitFor(() => {
      const rows = within(list()).getAllByTestId(/^transcript-row-/);
      expect(rows.length).toBeGreaterThan(0);
      for (const row of rows) {
        expect(within(row).getByTestId(/^transcript-speaker-/).textContent).toBe('Alice');
      }
    });
  });
});
