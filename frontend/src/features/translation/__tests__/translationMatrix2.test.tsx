// Delta 2: translation gap closure (round 2).
//
// Extends `translationMatrix.test.tsx` with the remaining branches:
// - `useTranslations.ts`: list-error normalization (code + ref),
//   ten-page cap with dedupe, detail-hydration failure retention
//   (recovery: keep-row), select/manual mutation error normalization.
// - `TranslationWorkspace.tsx`: manual save success (recovery: none),
//   manual 409 with kept draft (recovery: refresh), select 5xx toast
//   (recovery: report id), segment-removed fallback + toast
//   (recovery: nearest), ArrowUp/ArrowDown navigation, dirty
//   save/discard flows, empty-text candidate/selected states, unknown
//   sync tone, voice deep link, duration/reading hints, glossary
//   draft marks + empty preview.
// - `TranslationEditor.tsx`: empty-segment and saving states.
// - `types.ts` sweep 2: transcript-source fallbacks, glossary/voice
//   shapes, selected-text derivation, segment clamps + speaker
//   fallbacks, detail-merge branches, timestamp/speed/sync edges,
//   conflict classification, glossary-run splitting.
//
// Every failure asserts its recovery control per §11.6 with text
// signals (041C); fixtures are synthetic (R3); parsed shapes use
// `undefined`, never `null`.
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
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
import { TranslationEditor } from '../TranslationEditor.js';
import { TranslationWorkspace } from '../TranslationWorkspace.js';
import { useSelectTranslationVersion, useCreateManualTranslationVersion, useTranslations } from '../useTranslations.js';
import {
  formatTimestamp,
  isTranslationConflict,
  mergeTranslationDetail,
  parseTranslationListItems,
  parseTranslationSegment,
  parseTranslationVersion,
  readingSpeedHint,
  splitGlossaryRuns,
  syncToneFor,
  windowDurationMs,
} from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, details: Record<string, unknown> = {}): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-t99', details } },
    status,
  );
}

function makeSegment(index: number, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  const id = `seg_${String(index + 1).padStart(3, '0')}`;
  const startMs = index * 2000;
  const base: Record<string, unknown> = {
    id,
    projectId: 'prj_1',
    status: 'Ready',
    sequence: index + 1,
    startMs,
    endMs: startMs + 1800,
    speakerId: index % 2 === 0 ? 'spk_alice' : 'spk_bob',
    speakerLabel: index % 2 === 0 ? 'Alice' : 'Bob',
    selectionVersion: 2,
    reviewStatus: 'Approved',
    qualityCodes: [],
    syncStatus: 'SyncAcceptable',
    transcriptVersions: [
      { id: `${id}-t1`, provider: 'acme', model: 'stt-v1', text: `source line ${id}`, isSelected: false, createdAt: '2024-01-15T12:00:00Z' },
      { id: `${id}-t2`, provider: 'acme', model: 'stt-v2', text: `selected source ${id}`, isSelected: true, createdAt: '2024-01-15T13:00:00Z' },
    ],
    translationVersions: [
      { id: `${id}-tr1`, provider: 'acme', model: 'mt-v1', text: `candidate A ${id}`, isSelected: false, score: 0.81, createdAt: '2024-01-15T12:00:00Z' },
      { id: `${id}-tr2`, provider: 'acme', model: 'mt-v2', text: `candidate B ${id}`, isSelected: true, score: 0.92, createdAt: '2024-01-15T13:00:00Z' },
    ],
    selectedTranslationVersionId: `${id}-tr2`,
    selectedTranscriptVersionId: `${id}-t2`,
    ...overrides,
  };
  if (index === 0 && overrides['glossaryHits'] === undefined && overrides['assignedVoice'] === undefined) {
    base['glossaryHits'] = [{ term: 'Pilot', definition: 'Project codename' }];
    base['assignedVoice'] = { voiceId: 'voice_1', label: 'Voice One' };
  }
  return base;
}

interface TranslationDeltaWorld {
  mode: 'ok' | 'empty' | 'error500' | 'never' | 'custom';
  items: Array<Record<string, unknown>>;
  selectBehavior: 'ok' | 'conflict' | 'error500';
  manualBehavior: 'ok' | 'conflict' | 'error500';
  listCalls: number;
}

let world: TranslationDeltaWorld;

function resetWorld(): void {
  world = { mode: 'ok', items: [makeSegment(0), makeSegment(1), makeSegment(2)], selectBehavior: 'ok', manualBehavior: 'ok', listCalls: 0 };
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

function currentItems(): Array<Record<string, unknown>> {
  return world.mode === 'custom' ? world.items : [makeSegment(0), makeSegment(1), makeSegment(2)];
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) {
    return jsonResponse({});
  }
  if (url.includes('/translation-selection') && method === 'POST') {
    if (world.selectBehavior === 'conflict') {
      return errorEnvelope('TRANSLATION_VERSION_CONFLICT', 409, { currentVersion: 5 });
    }
    if (world.selectBehavior === 'error500') {
      return errorEnvelope('SELECTION_FAILED', 500);
    }
    return jsonResponse({ segmentId: 'seg_x', selectionVersion: 3 });
  }
  if (url.includes('/translation-edits') && method === 'POST') {
    if (world.manualBehavior === 'conflict') {
      return errorEnvelope('TRANSLATION_VERSION_CONFLICT', 409, { currentVersion: 5 });
    }
    if (world.manualBehavior === 'error500') {
      return errorEnvelope('EDIT_FAILED', 500);
    }
    return jsonResponse({ segmentId: 'seg_x', selectionVersion: 4, newVersionId: 'v-manual' });
  }
  if (method === 'GET' && /\/segments\/seg_/.test(url)) {
    const match = /\/segments\/(seg_[^/?]+)/.exec(url);
    const segmentId = match?.[1] ?? 'seg_001';
    if (segmentId === 'seg_404') {
      return errorEnvelope('NOT_FOUND', 404);
    }
    const found = currentItems().find((item) => item['id'] === segmentId);
    if (found !== undefined) {
      return jsonResponse({ ...found, outputStale: false });
    }
    const index = Number(segmentId.replace('seg_', '')) - 1;
    return jsonResponse({ ...makeSegment(Number.isFinite(index) && index >= 0 ? index : 0), id: segmentId, outputStale: false });
  }
  if (method === 'GET' && url.includes('/segments')) {
    world.listCalls += 1;
    if (world.mode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (world.mode === 'empty') {
      return jsonResponse({ items: [], page: 1, pageSize: 200, total: 0, hasMore: false });
    }
    if (world.mode === 'never') {
      return new Promise<Response>(() => {});
    }
    const items = currentItems();
    return jsonResponse({ items, page: 1, pageSize: 200, total: items.length, hasMore: false });
  }
  return jsonResponse({});
}

function renderWorkspace(projectId = 'prj_1'): void {
  const router = createMemoryRouter(
    [
      { path: '/projects/:id/translation', element: <TranslationWorkspace projectId={projectId} /> },
      { path: '/projects/:id', element: <div data-testid="project-overview">overview</div> },
    ],
    { initialEntries: ['/projects/prj_1/translation'], future: { ...ROUTER_FUTURE_FLAGS } },
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
  resetRestoreStartedForTests();
  queryClient.clear();
  delete (window as unknown as { __mut?: unknown }).__mut;
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  vi.restoreAllMocks();
  delete (window as unknown as { __mut?: unknown }).__mut;
});

describe('translation fetch edges', () => {
  it('normalizes list failures to AppError with refs (recovery: retry)', async () => {
    world.mode = 'error500';
    authenticate();
    function Probe(): null {
      useTranslations('prj_1');
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
    await waitFor(
      () => {
        const state = queryClient.getQueryState(['projects', 'detail', 'prj_1', 'translations', 'list']);
        expect(state?.error).toMatchObject({ code: 'INTERNAL_ERROR', status: 500 });
      },
      { timeout: 5000 },
    );
    const state = queryClient.getQueryState(['projects', 'detail', 'prj_1', 'translations', 'list']);
    expect(typeof (state?.error as { correlationId?: unknown } | undefined)?.correlationId).toBe('string');
  });

  it('caps runaway pagination at ten pages with dedupe (recovery: none needed)', async () => {
    let calls = 0;
    authenticate();
    setInnerFetchForTests((async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = urlOf(input);
      if (url.includes('/segments') && (init?.method ?? 'GET') === 'GET' && !/\/segments\/seg_/.test(url)) {
        calls += 1;
        return jsonResponse({ items: [makeSegment(0)], page: calls, pageSize: 200, total: 11, hasMore: true });
      }
      return mockFetch(input, init);
    }) as typeof fetch);
    function Probe(): null {
      useTranslations('prj_1');
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
    await waitFor(
      () => {
        const cached = queryClient.getQueryData<readonly { id: string }[]>(['projects', 'detail', 'prj_1', 'translations', 'list']);
        expect(cached?.map((segment) => segment.id)).toEqual(['seg_001']);
      },
      { timeout: 5000 },
    );
    expect(calls).toBe(10);
  });

  it('keeps rows whose detail hydration fails (recovery: keep-row)', async () => {
    authenticate();
    setInnerFetchForTests((async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = urlOf(input);
      const method = methodOf(input, init);
      if (method === 'GET' && url.includes('/segments') && !/\/segments\/seg_/.test(url)) {
        return jsonResponse({
          items: [{ ...makeSegment(0), id: 'seg_404', translationVersions: [] }],
          page: 1, pageSize: 200, total: 1, hasMore: false,
        });
      }
      if (method === 'GET' && /\/segments\/seg_404/.test(url)) {
        return errorEnvelope('NOT_FOUND', 404);
      }
      return mockFetch(input, init);
    }) as typeof fetch);
    function Probe(): null {
      useTranslations('prj_1');
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
    await waitFor(
      () => {
        const cached = queryClient.getQueryData<readonly { id: string; versions: readonly unknown[] }[]>(['projects', 'detail', 'prj_1', 'translations', 'list']);
        expect(cached?.find((segment) => segment.id === 'seg_404')?.versions).toEqual([]);
      },
      { timeout: 5000 },
    );
  });

  it('normalizes mutation failures to AppError (recovery: report id)', async () => {
    world.selectBehavior = 'error500';
    world.manualBehavior = 'error500';
    authenticate();
    function Probe(): null {
      const select = useSelectTranslationVersion('prj_1');
      const manual = useCreateManualTranslationVersion('prj_1');
      (window as unknown as { __mut?: unknown }).__mut = { select, manual };
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
    await waitFor(() => expect((window as unknown as { __mut?: unknown }).__mut).toBeDefined());
    const mut = (window as unknown as { __mut?: { select: { mutateAsync: (v: unknown) => Promise<unknown> }; manual: { mutateAsync: (v: unknown) => Promise<unknown> } } }).__mut;
    await expect(mut?.select.mutateAsync({ segmentId: 'seg_001', versionId: 'v1', expectedVersion: 2 })).rejects.toMatchObject({
      code: 'SELECTION_FAILED',
      status: 500,
    });
    await expect(mut?.manual.mutateAsync({ segmentId: 'seg_001', text: 'fixed', expectedVersion: 2 })).rejects.toMatchObject({
      code: 'EDIT_FAILED',
      status: 500,
    });
  });
});

describe('manual save matrix', () => {
  it('creates manual versions with a toast and clears the draft (recovery: none needed)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: 'fixed text' } });
    expect(screen.getByTestId('translation-draft-dirty').textContent).toContain('Unsaved draft');
    fireEvent.click(screen.getByTestId('translation-draft-save'));
    await waitFor(() =>
      expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('Manual translation created.'),
    );
    await waitFor(() => expect((screen.getByTestId('translation-draft') as HTMLTextAreaElement).value).toBe(''));
  });

  it('keeps drafts on version conflicts with a stale banner (recovery: refresh)', async () => {
    world.manualBehavior = 'conflict';
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: 'fixed text' } });
    fireEvent.click(screen.getByTestId('translation-draft-save'));
    expect(await screen.findByTestId('translation-stale-banner')).toBeDefined();
    expect((screen.getByTestId('translation-draft') as HTMLTextAreaElement).value).toBe('fixed text');
    fireEvent.click(screen.getByTestId('translation-stale-refresh'));
    await waitFor(() => expect(screen.queryByTestId('translation-stale-banner')).toBeNull());
  });

  it('rejects blank drafts through the dirty guard without losing them (recovery: retype)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: '   ' } });
    expect((screen.getByTestId('translation-draft-save') as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByTestId('translation-select-seg_002'));
    expect(await screen.findByTestId('translation-dirty-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('translation-dirty-save'));
    await waitFor(() =>
      expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('cannot be empty'),
    );
    expect(screen.getByTestId('translation-dirty-dialog')).toBeDefined();
    expect((screen.getByTestId('translation-draft') as HTMLTextAreaElement).value).toBe('   ');
  });
});

describe('selection + removal matrix', () => {
  it('toasts generic selection failures with the backend code (recovery: report id)', async () => {
    world.selectBehavior = 'error500';
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.click(screen.getByTestId('translation-select-version-seg_001-tr1'));
    await waitFor(() =>
      expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('SELECTION_FAILED'),
    );
  });

  it('falls back to the nearest segment when rows disappear (recovery: nearest)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    world.mode = 'custom';
    world.items = [makeSegment(1), makeSegment(2)];
    await queryClient.invalidateQueries();
    await waitFor(() =>
      expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('was removed'),
    );
    expect(screen.getByTestId('translation-header').getAttribute('data-segment-id')).toBe('seg_002');
  });

  it('moves selection with arrow keys (recovery: none needed)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.keyDown(screen.getByTestId('translation-list-scroll'), { key: 'ArrowDown' });
    await waitFor(() => expect(screen.getByTestId('translation-header').getAttribute('data-segment-id')).toBe('seg_002'));
    fireEvent.keyDown(screen.getByTestId('translation-list-scroll'), { key: 'ArrowUp' });
    await waitFor(() => expect(screen.getByTestId('translation-header').getAttribute('data-segment-id')).toBe('seg_001'));
  });

  it('discards dirty drafts on segment change (recovery: none needed)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: 'dirty' } });
    fireEvent.click(screen.getByTestId('translation-select-seg_002'));
    expect(await screen.findByTestId('translation-dirty-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('translation-dirty-discard'));
    await waitFor(() => expect(screen.getByTestId('translation-header').getAttribute('data-segment-id')).toBe('seg_002'));
    expect((screen.getByTestId('translation-draft') as HTMLTextAreaElement).value).toBe('');
  });
});

describe('header + candidate details', () => {
  it('links voices, times windows, and hints reading speed (never blank)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.click(screen.getByTestId('translation-select-seg_001'));
    expect(await screen.findByTestId('translation-header')).toBeDefined();
    expect(screen.getByTestId('translation-voice').getAttribute('href')).toBe('/projects/prj_1/voices');
    expect(screen.getByTestId('translation-voice').textContent).toBe('Voice One');
    expect(screen.getByTestId('translation-duration-display').textContent).toContain('1800 ms window');
    expect(screen.getByTestId('translation-reading-hint').textContent).toContain('chars/sec');
    expect(screen.getByTestId('translation-candidate-score-seg_001-tr2').textContent).toContain('0.92');
  });

  it('renders unknown sync tones as em-dashes with tooltips (never blank)', async () => {
    world.mode = 'custom';
    world.items = [makeSegment(0, { startMs: 5000, endMs: 5000, syncStatus: undefined, speakerId: undefined, speakerLabel: undefined, assignedVoice: undefined, glossaryHits: [] })];
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-header')).toBeDefined();
    expect(screen.getByTestId('translation-sync').textContent).toBe('—');
    expect(screen.getByTestId('translation-sync-indicator').textContent).toBe('—');
    expect(screen.getByTestId('translation-speaker').textContent).toBe('Unknown speaker');
  });

  it('renders empty candidate texts as text, never blanks', async () => {
    world.mode = 'custom';
    world.items = [makeSegment(0, {
      translationVersions: [{ id: 'e-tr1', provider: 'acme', model: 'm', text: '', isSelected: true, score: 0.5, createdAt: '2024-01-15T12:00:00Z' }],
      selectedTranslationVersionId: 'e-tr1',
    })];
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-candidate-text-e-tr1')).toBeDefined();
    expect(screen.getByTestId('translation-candidate-text-e-tr1').textContent).toBe('(empty)');
    expect(screen.getByTestId('translation-selected').textContent).toBe('(no translation yet)');
  });

  it('highlights glossary terms in drafts and empties previews cleanly', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.click(screen.getByTestId('translation-select-seg_001'));
    expect(await screen.findByTestId('translation-header')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: 'Pilot rocks' } });
    const mark = await screen.findByTestId('translation-draft-mark-0');
    expect(mark.textContent).toBe('Pilot');
    expect(mark.getAttribute('title')).toContain('Project codename');
    fireEvent.click(screen.getByTestId('translation-select-seg_002'));
    fireEvent.click(screen.getByTestId('translation-dirty-discard'));
    await waitFor(() => expect(screen.getByTestId('translation-header').getAttribute('data-segment-id')).toBe('seg_002'));
    expect(screen.getByTestId('translation-glossary-preview-empty').textContent).toBe('—');
  });
});

describe('TranslationEditor states', () => {
  it('asks for a segment when empty and flags saving (never blank)', () => {
    render(<TranslationEditor projectId="prj_1" segment={undefined} draft="" onDraftChange={() => {}} onSave={() => {}} isSaving={false} />);
    expect(screen.getByTestId('translation-editor-empty')).toBeDefined();
    cleanup();
    const segment = parseTranslationSegment(makeSegment(0));
    expect(segment).toBeDefined();
    if (segment === undefined) {
      return;
    }
    render(<TranslationEditor projectId="prj_1" segment={segment} draft="Pilot rocks" onDraftChange={() => {}} onSave={() => {}} isSaving={true} />);
    expect(screen.getByTestId('translation-editor-saving').textContent).toContain('Saving');
    expect((screen.getByTestId('translation-draft-save') as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByTestId('translation-draft-mark-0').textContent).toBe('Pilot');
  });
});

describe('translation types sweep 2', () => {
  it('resolves transcript sources through every fallback', () => {
    const explicitMissing = parseTranslationSegment({
      id: 'seg_010',
      transcriptVersions: [
        { id: 'a', text: 'first', isSelected: false },
        { id: 'b', text: 'second', isSelected: true },
      ],
      selectedTranscriptVersionId: 'nope',
    });
    expect(explicitMissing?.sourceText).toBe('second');
    expect(explicitMissing?.sourceVersionLabel).toBe('transcript v2');
    const lastFallback = parseTranslationSegment({
      id: 'seg_011',
      transcriptVersions: [
        { id: 'a', text: 'first' },
        { id: 'b', text: 'second' },
      ],
    });
    expect(lastFallback?.sourceText).toBe('second');
    expect(lastFallback?.sourceVersionLabel).toBe('transcript v2');
    const direct = parseTranslationSegment({ id: 'seg_012', sourceText: 'hello' });
    expect(direct?.sourceText).toBe('hello');
    expect(direct?.sourceVersionLabel).toBe('transcript selected');
    const none = parseTranslationSegment({ id: 'seg_013' });
    expect(none?.sourceText).toBe('');
    expect(none?.sourceVersionLabel).toBe('transcript —');
    const pascal = parseTranslationSegment({
      Id: 'seg_014',
      TranscriptVersions: [{ Id: 'p1', Text: 'Pascal source', IsSelected: true }],
      SelectedTranscriptVersionId: 'p1',
    });
    expect(pascal?.id).toBe('seg_014');
    expect(pascal?.sourceText).toBe('Pascal source');
  });

  it('parses glossary and voice shapes defensively', () => {
    const fromMap = parseTranslationSegment({ id: 'seg_020', glossaryHits: { Pilot: 'Project codename', '': 'skip', Bare: '' } });
    expect(fromMap?.glossaryHits).toContainEqual({ term: 'Pilot', definition: 'Project codename' });
    expect(fromMap?.glossaryHits).toContainEqual({ term: 'Bare', definition: undefined });
    const fromStrings = parseTranslationSegment({ id: 'seg_021', glossaryHits: ['Term', '', 42, {}, { term: '' }, { sourceTerm: 'Origen', notes: 'n' }] });
    expect(fromStrings?.glossaryHits).toContainEqual({ term: 'Term', definition: undefined });
    expect(fromStrings?.glossaryHits).toContainEqual({ term: 'Origen', definition: 'n' });
    expect(parseTranslationSegment({ id: 'seg_022' })?.assignedVoice).toBeUndefined();
    expect(parseTranslationSegment({ id: 'seg_023', assignedVoice: {} })?.assignedVoice).toBeUndefined();
    expect(parseTranslationSegment({ id: 'seg_024', assignedVoice: { voiceId: '' } })?.assignedVoice).toBeUndefined();
    expect(parseTranslationSegment({ id: 'seg_025', assignedVoice: { voiceId: 'v1' } })?.assignedVoice).toEqual({ voiceId: 'v1', label: 'v1' });
    const pascalVoice = parseTranslationSegment({ id: 'seg_026', Voice: { VoiceId: 'v2', DisplayName: 'Two' } });
    expect(pascalVoice?.assignedVoice).toEqual({ voiceId: 'v2', label: 'Two' });
    const profiled = parseTranslationSegment({ id: 'seg_027', voiceAssignment: { voiceProfileId: 'vp1', voiceName: 'Prof' } });
    expect(profiled?.assignedVoice).toEqual({ voiceId: 'vp1', label: 'Prof' });
  });

  it('derives selected texts without losing rows', () => {
    const explicit = parseTranslationSegment({
      id: 'seg_030',
      translationVersions: [{ id: 'v1', text: 'one' }, { id: 'v2', text: 'two', isSelected: true }],
      selectedTranslationVersionId: 'v1',
    });
    expect(explicit?.selectedText).toBe('one');
    const flagged = parseTranslationSegment({
      id: 'seg_031',
      translationVersions: [{ id: 'v1', text: 'one' }, { id: 'v2', text: 'two', isSelected: true }],
    });
    expect(flagged?.selectedText).toBe('two');
    const direct = parseTranslationSegment({ id: 'seg_032', translationText: 'direct hit' });
    expect(direct?.selectedText).toBe('direct hit');
    const last = parseTranslationSegment({ id: 'seg_033', translationVersions: [{ id: 'v1', text: 'only' }] });
    expect(last?.selectedText).toBe('only');
    const empty = parseTranslationSegment({ id: 'seg_034' });
    expect(empty?.selectedText).toBe('');
    expect(empty?.hasCandidates).toBe(false);
    expect(parseTranslationVersion({ id: 'm1', provider: 'Manual', text: 'hand' }, 0)?.isManual).toBe(true);
    const clamped = parseTranslationSegment({ id: 'seg_035', startMs: 2000, endMs: 1000 });
    expect(clamped?.endMs).toBe(2000);
    expect(parseTranslationSegment({ id: 'seg_036' })?.sequence).toBe(0);
    expect(parseTranslationSegment({ id: 'seg_037', speakerName: 'Narrator' })?.speakerLabel).toBe('Narrator');
    expect(parseTranslationSegment({ id: 'seg_038', speakerId: 'spk_x' })?.speakerLabel).toBe('spk_x');
    const manual = parseTranslationSegment({ id: 'seg_039', translationVersions: [{ id: 'v1', provider: 'manual', text: 'hand' }] });
    expect(manual?.manualVersionId).toBe('v1');
  });

  it('merges details with per-field fallbacks (never blanks)', () => {
    const summary = parseTranslationSegment(makeSegment(0));
    expect(summary).toBeDefined();
    if (summary === undefined) {
      return;
    }
    const versionOnly = mergeTranslationDetail(summary, { id: 'seg_001', selectionVersion: 7 });
    expect(versionOnly.selectionVersion).toBe(7);
    expect(versionOnly.versions).toEqual(summary.versions);
    expect(versionOnly.sourceText).toBe(summary.sourceText);
    const labelFallback = mergeTranslationDetail(summary, { id: 'seg_001', selectionVersion: 8, sourceText: '' });
    expect(labelFallback.sourceVersionLabel).toBe(summary.sourceVersionLabel);
    expect(mergeTranslationDetail(summary, {})).toBe(summary);
    const full = mergeTranslationDetail(summary, makeSegment(0, {
      translationVersions: [{ id: 'n1', provider: 'acme', model: 'm', text: 'neu', isSelected: true }],
    }));
    expect(full.versions.map((v) => v.id)).toEqual(['n1']);
  });

  it('parses list envelopes and versions defensively', () => {
    expect(parseTranslationListItems(undefined)).toEqual([]);
    expect(parseTranslationListItems({ items: 'nope' })).toEqual([]);
    expect(parseTranslationVersion({ text: 'hi' }, 0)).toBeUndefined();
    const bare = parseTranslationVersion({ id: 'v9' }, 0);
    expect(bare?.text).toBe('');
    expect(bare?.score).toBeUndefined();
    expect(bare?.versionNumber).toBe(1);
    const scored = parseTranslationVersion({ id: 'v8', text: 't', score: 'high' }, 1);
    expect(scored?.score).toBeUndefined();
    expect(parseTranslationSegment(undefined)).toBeUndefined();
    expect(parseTranslationSegment({})).toBeUndefined();
  });

  it('formats windows, speeds, and sync tones across edges', () => {
    expect(windowDurationMs({ startMs: 0, endMs: 0 })).toBe(0);
    expect(formatTimestamp(0)).toBe('00:00.000');
    expect(formatTimestamp(3_661_000)).toBe('61:01.000');
    expect(readingSpeedHint('', 1800)).toBe('0 chars · 0.0 chars/sec');
    expect(syncToneFor({ syncStatus: 'exceeds window' }, 'hi', 2000)).toBe('overflow');
    expect(syncToneFor({ syncStatus: 'SYNC_ISSUE' }, 'hi', 2000)).toBe('overflow');
    expect(syncToneFor({ syncStatus: 'Out-of-Sync' }, 'hi', 2000)).toBe('overflow');
    expect(syncToneFor({ syncStatus: 'ok' }, 'hi', 2000)).toBe('in-window');
    expect(syncToneFor({ syncStatus: 'in-window' }, 'hi', 2000)).toBe('in-window');
    expect(syncToneFor({ syncStatus: undefined }, 'hi', 0)).toBe('unknown');
    expect(syncToneFor({ syncStatus: undefined }, 'hi', 2000)).toBe('in-window');
  });

  it('classifies conflicts and splits glossary runs across edges', () => {
    expect(isTranslationConflict({ code: 'SELECTION_CONFLICT', status: 500 })).toBe(true);
    expect(isTranslationConflict({ code: 'X', status: 409 })).toBe(true);
    expect(isTranslationConflict({})).toBe(false);
    expect(isTranslationConflict(undefined)).toBe(false);
    const longest = splitGlossaryRuns('Pilot episode one', [
      { term: 'Pilot', definition: 'd1' },
      { term: 'Pilot episode', definition: 'd2' },
    ]);
    expect(longest.some((run) => run.text === 'Pilot episode')).toBe(true);
    const around = splitGlossaryRuns('a Pilot z', [{ term: 'Pilot', definition: undefined }]);
    expect(around.map((run) => run.text)).toEqual(['a ', 'Pilot', ' z']);
    expect(around[1]?.term?.definition).toBeUndefined();
    const miss = splitGlossaryRuns('plain text here', [{ term: 'Pilot', definition: 'd' }]);
    expect(miss).toEqual([{ text: 'plain text here', term: undefined }]);
  });
});
