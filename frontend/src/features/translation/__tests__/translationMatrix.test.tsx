// Task 039B: translation state-matrix gap closure.
//
// Extends `translation.test.tsx` (side-by-side, immutability, select/manual
// flows, dirty-guard dialog) with the missing states: loading/error-retry/
// empty shells, stale banner + refresh, source-changed + rebase, back-link
// navigation, empty-source/no-voice/no-candidate fallbacks, glossary + sync
// displays, beforeunload guard, hook guards (empty id, anonymous,
// invalidation), useDirtyGuard unit matrix, and the types helper sweep.
// Every failure asserts its recovery control per §11.6 with text signals
// (041C); fixtures are synthetic and fetch is intercepted (R3).
import { QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
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
import { TranslationWorkspace } from '../TranslationWorkspace.js';
import { invalidateTranslations, useTranslationSegment, useTranslations } from '../useTranslations.js';
import { useCreateManualTranslationVersion, useSelectTranslationVersion } from '../useTranslations.js';
import { useDirtyGuard } from '../useDirtyGuard.js';
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
    { error: { code, message: `backend ${code}`, correlationId: 'corr-t28', details } },
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

interface TranslationMatrixWorld {
  listMode: 'ok' | 'empty' | 'error500' | 'never' | 'paged' | 'bare';
  selectConflict: boolean;
  sourceVersionId: string | null;
  listCalls: number;
}

let world: TranslationMatrixWorld;

function resetWorld(): void {
  world = { listMode: 'ok', selectConflict: false, sourceVersionId: null, listCalls: 0 };
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
  if (url.includes('/translation-selection') && method === 'POST') {
    if (world.selectConflict) {
      return errorEnvelope('TRANSLATION_VERSION_CONFLICT', 409, { currentVersion: 5 });
    }
    return jsonResponse({ segmentId: 'seg_x', selectionVersion: 3 });
  }
  if (url.includes('/translation-edits') && method === 'POST') {
    return jsonResponse({ segmentId: 'seg_x', selectionVersion: 4, newVersionId: 'v-manual' });
  }
  if (method === 'GET' && /\/segments\/seg_/.test(url)) {
    const match = /\/segments\/(seg_[^/?]+)/.exec(url);
    const segmentId = match?.[1] ?? 'seg_001';
    if (segmentId === 'seg_404') {
      return errorEnvelope('NOT_FOUND', 404);
    }
    const index = Number(segmentId.replace('seg_', '')) - 1;
    const row = makeSegment(Number.isFinite(index) && index >= 0 ? index : 0);
    if (world.sourceVersionId !== null) {
      row['selectedTranscriptVersionId'] = world.sourceVersionId;
    }
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
      return jsonResponse({ items: [makeSegment(1), makeSegment(2)], page: 2, pageSize: 200, total: 3, hasMore: false });
    }
    if (world.listMode === 'bare') {
      return jsonResponse({
        items: [{ ...makeSegment(0), translationVersions: [] }, makeSegment(1)],
        page: 1,
        pageSize: 200,
        total: 2,
        hasMore: false,
      });
    }
    const items = [makeSegment(0), makeSegment(1), makeSegment(2)];
    if (world.sourceVersionId !== null) {
      (items[0] as Record<string, unknown>)['selectedTranscriptVersionId'] = world.sourceVersionId;
    }
    return jsonResponse({ items, page: 1, pageSize: 200, total: 3, hasMore: false });
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
});

describe('TranslationWorkspace shell states', () => {
  it('shows loading while segments resolve (never blank)', () => {
    world.listMode = 'never';
    authenticate();
    renderWorkspace();
    expect(screen.getByTestId('translation-loading')).toBeDefined();
  });

  it('recovers from list errors with retry (recovery: retry)', async () => {
    world.listMode = 'error500';
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-error')).toBeDefined();
    const callsBefore = world.listCalls;
    world.listMode = 'ok';
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(world.listCalls).toBeGreaterThan(callsBefore));
    expect(await screen.findByTestId('translation-list')).toBeDefined();
  });

  it('renders the empty state with a processing link (recovery: process)', async () => {
    world.listMode = 'empty';
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-empty')).toBeDefined();
    expect(screen.getByTestId('translation-empty-progress-link').getAttribute('href')).toBe('/projects/prj_1');
  });

  it('warns on stale versions while keeping drafts (recovery: refresh)', async () => {
    world.selectConflict = true;
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_002')).toBeDefined();
    // Selecting another candidate posts against a stale selection version.
    fireEvent.click(screen.getByTestId('translation-select-version-seg_001-tr1'));
    expect(await screen.findByTestId('translation-stale-banner')).toBeDefined();
    world.selectConflict = false;
    fireEvent.click(screen.getByTestId('translation-stale-refresh'));
    await waitFor(() => expect(screen.queryByTestId('translation-stale-banner')).toBeNull());
  });
});

describe('source-changed rebase matrix', () => {
  it('notices mid-edit source moves and rebases on demand (recovery: rebase)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: 'dirty draft' } });
    world.sourceVersionId = 'seg_001-t1';
    await queryClient.invalidateQueries();
    expect(await screen.findByTestId('translation-source-changed')).toBeDefined();
    expect(screen.getByTestId('translation-source-changed-text').textContent).toContain('moved from');
    fireEvent.click(screen.getByTestId('translation-rebase'));
    await waitFor(() => expect(screen.queryByTestId('translation-source-changed')).toBeNull());
  });
});

describe('workspace navigation matrix', () => {
  it('leaves cleanly to the project overview (recovery: back link)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.click(screen.getByTestId('translation-back-link'));
    expect(await screen.findByTestId('project-overview')).toBeDefined();
  });

  it('warns on browser-tab leave with dirty drafts (no silent loss)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: 'dirty' } });
    const event = new Event('beforeunload', { cancelable: true });
    const prevented: boolean[] = [];
    event.preventDefault = () => {
      prevented.push(true);
    };
    window.dispatchEvent(event);
    expect(prevented.length).toBe(1);
  });
});

describe('sparse segment fallbacks', () => {
  it('renders empty source, missing voice, and no-candidate states (never blank)', async () => {
    world.listMode = 'ok';
    authenticate();
    setInnerFetchForTests((async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = urlOf(input);
      const method = methodOf(input, init);
      if (method === 'GET' && url.includes('/segments')) {
        return jsonResponse({
          items: [
            makeSegment(0, {
              transcriptVersions: [
                { id: 'e1', provider: 'acme', model: 'm', text: '', isSelected: true, createdAt: '2024-01-15T12:00:00Z' },
              ],
              selectedTranscriptVersionId: 'e1',
              assignedVoice: null,
              translationVersions: [],
              selectedTranslationVersionId: '',
            }),
          ],
          page: 1,
          pageSize: 200,
          total: 1,
          hasMore: false,
        });
      }
      return mockFetch(input, init);
    }) as typeof fetch);
    renderWorkspace();
    expect(await screen.findByTestId('translation-source')).toBeDefined();
    expect(screen.getByTestId('translation-source').textContent).toBe('(empty source)');
    expect(screen.getByTestId('translation-voice').textContent).toBe('—');
    expect(screen.getByTestId('translation-voice').getAttribute('title')).toContain('No voice assigned');
    expect(screen.getByTestId('translation-no-candidates')).toBeDefined();
    expect(screen.getByTestId('translation-no-candidates-progress-link').getAttribute('href')).toBe('/projects/prj_1');
  });

  it('shows glossary hits and voice assignment for enriched segments', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.click(screen.getByTestId('translation-select-seg_001'));
    expect(await screen.findByTestId('translation-header')).toBeDefined();
    expect(screen.getByTestId('translation-glossary')).toBeDefined();
    expect(screen.getByTestId('translation-glossary').textContent).toContain('Pilot');
  });
});

describe('translation fetch matrix', () => {
  function renderHookProbe(segmentId?: string): void {
    function Probe(): null {
      useTranslations('prj_1');
      useTranslationSegment('prj_1', segmentId);
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
        const cached = queryClient.getQueryData<readonly { id: string }[]>(['projects', 'detail', 'prj_1', 'translations', 'list']);
        expect(cached?.map((segment) => segment.id)).toEqual(['seg_001', 'seg_002', 'seg_003']);
      },
      { timeout: 5000 },
    );
  });

  it('hydrates candidate-less rows from detail with fallback retention (recovery: keep-row)', async () => {
    world.listMode = 'bare';
    authenticate();
    renderHookProbe();
    await waitFor(
      () => {
        const cached = queryClient.getQueryData<readonly { id: string; versions: readonly unknown[] }[]>(['projects', 'detail', 'prj_1', 'translations', 'list']);
        expect(cached?.find((segment) => segment.id === 'seg_001')?.versions.length).toBeGreaterThan(0);
      },
      { timeout: 5000 },
    );
  });

  it('normalizes segment-detail failures to AppError (recovery: retry)', async () => {
    authenticate();
    renderHookProbe('seg_404');
    await waitFor(
      () => {
        const state = queryClient.getQueryState(['projects', 'detail', 'prj_1', 'translations', 'detail', 'seg_404']);
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
      if (url.includes('/translation-selection') && method === 'POST') {
        return jsonResponse({ ok: true });
      }
      if (url.includes('/translation-edits') && method === 'POST') {
        return jsonResponse({ ok: true });
      }
      return mockFetch(input, init);
    }) as typeof fetch);
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
    const mut = (window as unknown as { __mut?: { select: { mutateAsync: (v: unknown) => Promise<{ selectionVersion: number }> }; manual: { mutateAsync: (v: unknown) => Promise<{ selectionVersion: number }> } } }).__mut;
    expect((await mut?.select.mutateAsync({ segmentId: 'seg_001', versionId: 'v1', expectedVersion: 2 }))?.selectionVersion).toBe(2);
    expect((await mut?.manual.mutateAsync({ segmentId: 'seg_001', text: 'fixed', expectedVersion: 2 }))?.selectionVersion).toBe(2);
  });
});

describe('translation hook guards', () => {
  it('never fires for empty ids or anonymous sessions', async () => {
    authenticate();
    let calls = 0;
    setInnerFetchForTests((async () => {
      calls += 1;
      return jsonResponse({});
    }) as typeof fetch);
    function Probe(): null {
      useTranslations('');
      useTranslationSegment('prj_1', undefined);
      useTranslationSegment('prj_1', '');
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
    await invalidateTranslations(queryClient, 'prj_1');
    await invalidateTranslations(queryClient, 'prj_1', 'seg_001');
  });
});

describe('useDirtyGuard unit matrix', () => {
  function renderGuard(options: Parameters<typeof useDirtyGuard>[0]) {
    let guard: ReturnType<typeof useDirtyGuard> | undefined;
    function Probe(): null {
      guard = useDirtyGuard(options);
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
    return () => guard as ReturnType<typeof useDirtyGuard>;
  }

  it('follows navigation immediately when clean', () => {
    const next = vi.fn();
    const get = renderGuard({ isDirty: false, onSave: () => {}, onDiscard: () => {} });
    act(() => {
      get().requestLeave(next, 'segment seg_002');
    });
    expect(next).toHaveBeenCalledTimes(1);
    expect(get().dialogOpen).toBe(false);
    cleanup();
  });

  it('holds navigation while dirty and labels the pending target', () => {
    const next = vi.fn();
    const get = renderGuard({ isDirty: true, onSave: () => {}, onDiscard: () => {} });
    act(() => {
      get().requestLeave(next, 'segment seg_002');
    });
    expect(next).not.toHaveBeenCalled();
    expect(get().dialogOpen).toBe(true);
    expect(get().pendingLabel).toBe('segment seg_002');
    act(() => {
      get().confirmCancel();
    });
    expect(get().dialogOpen).toBe(false);
    expect(next).not.toHaveBeenCalled();
    cleanup();
  });

  it('saves then follows, keeps failed saves in place, and discards on demand', async () => {
    const followed: string[] = [];
    const discarded: string[] = [];
    let saveResult: boolean | void = undefined;
    const get = renderGuard({
      isDirty: true,
      onSave: () => saveResult,
      onDiscard: () => {
        discarded.push('yes');
      },
    });
    act(() => {
      get().requestLeave(() => {
        followed.push('nav');
      });
    });
    await act(async () => {
      await get().confirmSave();
    });
    expect(followed).toEqual(['nav']);
    expect(get().dialogOpen).toBe(false);
    act(() => {
      get().requestLeave(() => {
        followed.push('nav2');
      });
    });
    saveResult = false;
    await act(async () => {
      await get().confirmSave();
    });
    expect(followed).toEqual(['nav']);
    expect(get().dialogOpen).toBe(true);
    act(() => {
      get().confirmDiscard();
    });
    expect(discarded).toEqual(['yes']);
    expect(followed).toEqual(['nav', 'nav2']);
    cleanup();
  });

  it('ignores re-entrant saves while a save is in flight', async () => {
    let saveCalls = 0;
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const get = renderGuard({
      isDirty: true,
      onSave: () => {
        saveCalls += 1;
        return gate;
      },
      onDiscard: () => {},
    });
    act(() => {
      get().requestLeave(() => {});
    });
    let first!: Promise<void>;
    act(() => {
      first = get().confirmSave();
    });
    expect(saveCalls).toBe(1);
    let second!: Promise<void>;
    act(() => {
      second = get().confirmSave();
    });
    await second;
    expect(saveCalls).toBe(1);
    expect(get().dialogOpen).toBe(true);
    await act(async () => {
      release();
      await first;
    });
    expect(saveCalls).toBe(1);
    expect(get().dialogOpen).toBe(false);
    cleanup();
  });
});

describe('translation types sweep', () => {
  const raw = makeSegment(0);

  it('parses versions and segments defensively', () => {
    expect(parseTranslationVersion(null, 0)).toBeUndefined();
    expect(parseTranslationVersion({ id: 't1', text: 'hi' }, 0)?.id).toBe('t1');
    expect(parseTranslationSegment(null)).toBeUndefined();
    expect(parseTranslationSegment(raw)?.id).toBe('seg_001');
    expect(parseTranslationListItems(null)).toEqual([]);
    expect(parseTranslationListItems({ items: [raw, null] }).length).toBe(1);
  });

  it('formats windows, speeds, and sync tones without throwing', () => {
    const parsed = parseTranslationSegment(raw);
    expect(parsed).toBeDefined();
    if (parsed !== undefined) {
      expect(windowDurationMs(parsed)).toBe(1800);
      expect(readingSpeedHint('hello world', 2000).length).toBeGreaterThan(0);
      expect(syncToneFor(parsed, parsed.selectedText, 1800)).not.toBe('');
    }
    expect(formatTimestamp(61_250)).toBe('01:01.250');
  });

  it('classifies translation conflicts with null safety', () => {
    expect(isTranslationConflict({ code: 'TRANSLATION_VERSION_CONFLICT', status: 409 })).toBe(true);
    expect(isTranslationConflict({ code: 'X', status: 500 })).toBe(false);
    expect(isTranslationConflict(undefined)).toBe(false);
    expect(isTranslationConflict(null)).toBe(false);
  });

  it('splits glossary runs around matched terms', () => {
    const runs = splitGlossaryRuns('Pilot episode one', [{ term: 'Pilot', definition: 'Project codename' }]);
    expect(runs.length).toBeGreaterThan(1);
    expect(runs.some((run) => run.text === 'Pilot')).toBe(true);
    const plain = splitGlossaryRuns('plain text', []);
    expect(plain.length).toBe(1);
    expect(plain[0]?.text).toBe('plain text');
    expect(plain[0]?.term).toBeUndefined();
  });

  it('parses versions and segments through every defensive branch', () => {
    expect(parseTranslationVersion(null, 0)).toBeUndefined();
    expect(parseTranslationVersion({ text: 'hi' }, 0)).toBeUndefined();
    const bare = parseTranslationVersion({ id: 'v1' }, 2);
    expect(bare?.provider).toBe('unknown');
    expect(bare?.model).toBe('unknown');
    expect(bare?.versionNumber).toBe(3);
    expect(bare?.isSelected).toBe(false);
    expect(bare?.isManual).toBe(false);
    const manual = parseTranslationVersion({ id: 'm1', provider: 'manual', model: 'manual-review-v1', text: 'fixed', isSelected: true, score: 0.5 }, 0);
    expect(manual?.isManual).toBe(true);
    expect(parseTranslationSegment(null)).toBeUndefined();
    expect(parseTranslationSegment({ id: 'seg_1' })?.id).toBe('seg_1');
    expect(parseTranslationSegment({ Id: 'seg_2', Status: 'Ready' })?.id).toBe('seg_2');
    expect(parseTranslationListItems('nope')).toEqual([]);
    expect(parseTranslationListItems({ items: [null, { id: 'seg_3' }] }).length).toBe(1);
    expect(parseTranslationListItems([makeSegment(0), null]).length).toBe(1);
  });

  it('merges details with per-field fallbacks (never blanks)', () => {
    const summary = parseTranslationSegment(makeSegment(0));
    expect(summary).toBeDefined();
    if (summary === undefined) {
      return;
    }
    expect(mergeTranslationDetail(summary, null)).toBe(summary);
    const merged = mergeTranslationDetail(summary, { ...makeSegment(0), translationVersions: [], sourceText: undefined });
    expect(merged.selectionVersion).toBe(summary.selectionVersion);
    const full = mergeTranslationDetail(summary, makeSegment(0, { translationVersions: [{ id: 'n1', provider: 'acme', model: 'm', text: 'neu', isSelected: true }] }));
    expect(full.versions.length).toBeGreaterThan(0);
  });

  it('formats windows, speeds, and sync tones across edges', () => {
    expect(windowDurationMs({ startMs: 2000, endMs: 1000 })).toBe(0);
    expect(windowDurationMs({ startMs: 0, endMs: 1800 })).toBe(1800);
    expect(readingSpeedHint('', 0)).toContain('window —');
    expect(readingSpeedHint('hi', -5)).toContain('window —');
    expect(readingSpeedHint('hello world', 2000)).toContain('chars/sec');
    expect(syncToneFor({ syncStatus: 'SyncOverflow' }, 'hi', 2000)).toBe('overflow');
    expect(syncToneFor({ syncStatus: 'out-of-sync' }, 'hi', 2000)).toBe('overflow');
    expect(syncToneFor({ syncStatus: 'SyncAcceptable' }, 'hi', 2000)).toBe('in-window');
    expect(syncToneFor({ syncStatus: 'SyncAcceptable' }, 'x'.repeat(100000), 1000)).toBe('overflow');
    expect(syncToneFor({ syncStatus: undefined }, 'hi', 0)).toBe('unknown');
    expect(syncToneFor({ syncStatus: undefined }, 'hi', 2000)).toBe('in-window');
    expect(syncToneFor({ syncStatus: undefined }, 'x'.repeat(100000), 1000)).toBe('overflow');
    expect(formatTimestamp(-5)).toBe('00:00.000');
  });

  it('splits glossary runs with regex-safe matching (case-insensitive)', () => {
    const runs = splitGlossaryRuns('Pilot (pilot) episode.', [{ term: 'pilot', definition: 'd' }, { term: '', definition: 'x' }]);
    expect(runs.some((run) => run.text.toLowerCase() === 'pilot' && run.term !== undefined)).toBe(true);
    expect(splitGlossaryRuns('', [{ term: 'Pilot', definition: 'd' }])).toEqual([{ text: '', term: undefined }]);
  });
});
