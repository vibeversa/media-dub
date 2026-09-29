// Task 039B: review state-matrix gap closure.
//
// Extends `review.test.tsx` (filters, empties, dispositions incl. 409, full
// context, studio composition) with the missing states: queue
// needs-project/loading/error-retry/stale-with-data/row
// select-deselect/filter-draft-apply, studio project scoping + filter URL
// sync, card loading/error/minimal-context section matrices, disposition
// endpoint/response-shape branches, queue pagination, and the helper sweep
// (endpoints, conflict/resolved/reason guards, version/actor extraction,
// validation). Every failure asserts its recovery control per §11.6 with
// text signals (041C); fixtures are synthetic and fetch is intercepted (R3).
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { LocaleProvider } from '../../../app/providers/LocaleProvider.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { ToastProvider } from '../../../components/Toast/Toast.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { ReviewCard } from '../ReviewCard.js';
import { ReviewQueue } from '../ReviewQueue.js';
import { ReviewStudio } from '../ReviewStudio.js';
import {
  EMPTY_REVIEW_FILTERS,
  actorFromDetails,
  allowedTokenFor,
  buildDispositionPayload,
  currentVersionFromDetails,
  dispositionEndpointFor,
  dispositionRequiresReason,
  hasActiveReviewFilters,
  isAlreadyResolved,
  isDispositionAllowed,
  isReasonRequired,
  isReviewConflict,
  parseReviewContext,
  parseReviewQueueItems,
  reviewFiltersFromSearchParams,
  reviewFiltersToSearchParams,
  sortHistoryNewestFirst,
  toReviewListQuery,
  validateEditText,
  validateReason,
} from '../types.js';
import type { ReviewFilters } from '../types.js';
import { executeDisposition, invalidateDisposition, resetDispositionKeysForTests } from '../useDisposition.js';
import { reviewListParamsFor } from '../useReviewQueue.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, details: Record<string, unknown> = {}): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-r31', details } },
    status,
  );
}

function makeQueueRow(id: string, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id,
    projectId: 'prj_1',
    status: 'Open',
    reason: 'TRANSLATION_QUALITY',
    createdAt: '2024-01-16T10:00:00Z',
    resolvedAt: null,
    ...overrides,
  };
}

function contextBody(reviewId: string, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    item: { id: reviewId, type: 'TRANSLATION_QUALITY', severity: 'high', status: 'Open', version: 1 },
    project: { id: 'prj_1', name: 'Pilot' },
    run: { id: 'run_1', status: 'Running', configHash: 'cfg_abc' },
    segment: { id: 'seg_001', startMs: 0, endMs: 2000, speakerId: 'spk_alice' },
    versions: {
      transcript: [
        { id: 'tr-v1', provider: 'acme', model: 'stt-v1', text: 'hello world', isSelected: true, createdAt: '2024-01-15T12:00:00Z' },
      ],
      translation: [
        { id: 'tl-v1', primaryText: 'hola mundo', provider: 'acme', model: 'mt-v1', isSelected: true, createdAt: '2024-01-15T13:00:00Z' },
      ],
      selectedTranscriptVersionId: 'tr-v1',
      selectedTranslationVersionId: 'tl-v1',
      selectionVersion: 2,
      truncated: false,
    },
    voice: { speakerId: 'spk_alice', voiceProfileId: 'voice_1', voiceId: 'stock-es-1', consentState: 'not_required' },
    audio: { previewArtifactId: 'art_1', signedUrl: null },
    sync: { offsetMs: 300, driftFlag: true },
    qc: {
      issues: [{ id: 'qc_1', code: 'QC_NOISY', severity: 'high', message: 'noisy segment', artifactId: null }],
      evidenceArtifactIds: [],
    },
    actions: { allowed: ['resolve', 'dismiss', 'reopen', 'resolve-with-edit'] },
    permissions: { canResolve: true, canEdit: true },
    history: [
      { id: 'h1', type: 'Requeue', reviewer: 'usr_1', reason: 'needs fix', createdAt: '2024-01-16T09:00:00Z' },
    ],
    truncated: false,
    ...overrides,
  };
}

interface ReviewMatrixWorld {
  queueMode: 'ok' | 'empty' | 'error500' | 'paged';
  queueCalls: number;
  contextMode: 'ok' | 'minimal' | 'error500';
  mutationMode: 'ok' | 'conflict';
  mutationCalls: number;
  mutationUrls: string[];
}

let world: ReviewMatrixWorld;

function resetWorld(): void {
  world = { queueMode: 'ok', queueCalls: 0, contextMode: 'ok', mutationMode: 'ok', mutationCalls: 0, mutationUrls: [] };
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
  if (url.includes('/reviews') && method === 'GET' && !url.includes('/reviews/')) {
    world.queueCalls += 1;
    if (world.queueMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (world.queueMode === 'empty') {
      return jsonResponse({ items: [], page: 1, pageSize: 50, total: 0, hasMore: false });
    }
    if (world.queueMode === 'paged') {
      const page = Number.parseInt(new URL(url).searchParams.get('page') ?? '1', 10);
      const items = page === 1 ? [makeQueueRow('rev_001'), makeQueueRow('rev_002')] : [makeQueueRow('rev_003')];
      return jsonResponse({ items, page, pageSize: 2, total: 3, hasMore: page === 1 });
    }
    return jsonResponse({ items: [makeQueueRow('rev_001', { reason: '' })], page: 1, pageSize: 50, total: 1, hasMore: false });
  }
  if (/\/reviews\/(rev_[^/?]+)(\/context)?$/.test(url) && method === 'GET') {
    if (world.contextMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (world.contextMode === 'minimal') {
      return jsonResponse({
        item: { id: 'rev_001', type: 'X', severity: 'low', status: 'Open', version: 1 },
        project: { id: 'prj_1', name: '' },
        run: { id: 'run_1', status: 'Running' },
        segment: { id: 'seg_001' },
        versions: { transcript: [], translation: [] },
        voice: {},
        audio: {},
        sync: {},
        qc: { issues: [] },
        actions: { allowed: [] },
        permissions: { canResolve: false, canEdit: false },
        history: [],
      });
    }
    return jsonResponse(contextBody('rev_001'));
  }
  if (url.includes('/reviews/') && method === 'POST') {
    world.mutationCalls += 1;
    world.mutationUrls.push(url);
    if (world.mutationMode === 'conflict') {
      return errorEnvelope('REVIEW_VERSION_CONFLICT', 409, { currentVersion: 4, actor: 'usr_9' });
    }
    return jsonResponse({ reviewId: 'rev_001', status: 'Resolved', version: 2 });
  }
  return jsonResponse({});
}

function renderWithProviders(node: React.JSX.Element, initialEntry = '/review'): void {
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <MemoryRouter initialEntries={[initialEntry]}>{node}</MemoryRouter>
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view']);
}

function queueProps(overrides: Partial<React.ComponentProps<typeof ReviewQueue>> = {}) {
  const calls: { filters: ReviewFilters[]; selected: Array<string | undefined> } = { filters: [], selected: [] };
  return {
    calls,
    props: {
      projectId: 'prj_1',
      filters: { ...EMPTY_REVIEW_FILTERS },
      onFiltersChange: (next: ReviewFilters) => {
        calls.filters.push(next);
      },
      selectedReviewId: undefined,
      onSelect: (id: string | undefined) => {
        calls.selected.push(id);
      },
      ...overrides,
    },
  };
}

beforeEach(() => {
  resetWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  resetDispositionKeysForTests();
  queryClient.clear();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  resetDispositionKeysForTests();
  queryClient.clear();
  vi.restoreAllMocks();
});

describe('ReviewQueue shell states', () => {
  it('asks for a project when unscoped (no fetch, no crash)', () => {
    authenticate();
    const { props } = queueProps({ projectId: '' });
    renderWithProviders(<ReviewQueue {...props} />);
    expect(screen.getByTestId('review-queue-needs-project')).toBeDefined();
    expect(world.queueCalls).toBe(0);
  });

  it('recovers from load errors with retry (recovery: retry)', async () => {
    world.queueMode = 'error500';
    authenticate();
    const { props } = queueProps();
    renderWithProviders(<ReviewQueue {...props} />);
    expect(await screen.findByTestId('review-queue-error')).toBeDefined();
    const callsBefore = world.queueCalls;
    world.queueMode = 'ok';
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(world.queueCalls).toBeGreaterThan(callsBefore));
    expect(await screen.findByTestId('review-queue-list')).toBeDefined();
  });

  it('warns on stale data while keeping the last queue (recovery: wait)', async () => {
    authenticate();
    const { props } = queueProps();
    renderWithProviders(<ReviewQueue {...props} />);
    expect(await screen.findByTestId('review-queue-list')).toBeDefined();
    world.queueMode = 'error500';
    await queryClient.invalidateQueries();
    expect(await screen.findByTestId('review-queue-stale')).toBeDefined();
    expect(screen.getByTestId('review-queue-stale').textContent).toContain('last loaded queue');
    expect(screen.getByTestId('review-queue-list')).toBeDefined();
  });

  it('selects and deselects rows with pressed-state signals', async () => {
    authenticate();
    const { props, calls } = queueProps();
    renderWithProviders(<ReviewQueue {...props} />);
    const row = await screen.findByTestId('review-row-rev_001');
    expect(row.getAttribute('aria-pressed')).toBe('false');
    expect(screen.queryByTestId('review-row-reason-rev_001')).toBeNull();
    fireEvent.click(row);
    expect(calls.selected).toEqual(['rev_001']);
  });

  it('applies filter drafts on blur and submit (recovery: clear)', async () => {
    authenticate();
    const { props, calls } = queueProps();
    renderWithProviders(<ReviewQueue {...props} />);
    expect(await screen.findByTestId('review-queue-list')).toBeDefined();
    fireEvent.change(screen.getByTestId('review-filter-severity'), { target: { value: 'blocking' } });
    fireEvent.blur(screen.getByTestId('review-filter-severity'));
    expect(calls.filters[calls.filters.length - 1]?.severity).toBe('blocking');
    fireEvent.change(screen.getByTestId('review-filter-status'), { target: { value: 'open' } });
    fireEvent.click(screen.getByTestId('review-apply-filters'));
    expect(calls.filters[calls.filters.length - 1]?.status).toBe('open');
    fireEvent.click(screen.getByTestId('review-clear-filters-top'));
    expect(calls.filters[calls.filters.length - 1]).toEqual(expect.objectContaining({ severity: '', status: '' }));
  });
});

describe('ReviewStudio scoping matrix', () => {
  it('scopes by URL project input when no fixed project is set', async () => {
    authenticate();
    renderWithProviders(<ReviewStudio />);
    const input = await screen.findByTestId('review-filter-project');
    expect(screen.getByTestId('review-studio').getAttribute('data-project')).toBe('');
    expect(screen.getByTestId('review-queue-needs-project')).toBeDefined();
    // Task 041C: the field used to write through to the URL on every
    // keystroke, which dropped keyboard focus after one character and made the
    // scope unusable without a mouse. It commits on blur or Enter now, like
    // every other filter on this screen, so the test commits it the same way.
    fireEvent.change(input, { target: { value: 'prj_1' } });
    expect(screen.getByTestId('review-studio').getAttribute('data-project')).toBe('');
    fireEvent.blur(input);
    expect(await screen.findByTestId('review-queue-list')).toBeDefined();
  });

  it('commits the project scope on Enter, for a keyboard user', async () => {
    authenticate();
    renderWithProviders(<ReviewStudio />);
    const input = await screen.findByTestId('review-filter-project');
    fireEvent.change(input, { target: { value: 'prj_1' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(await screen.findByTestId('review-queue-list')).toBeDefined();
  });

  it('hides the project input for fixed project scopes', async () => {
    authenticate();
    renderWithProviders(<ReviewStudio projectId="prj_1" />);
    expect(await screen.findByTestId('review-queue-list')).toBeDefined();
    expect(screen.queryByTestId('review-filter-project')).toBeNull();
  });

  it('opens the card on selection and clears it on settle', async () => {
    authenticate();
    renderWithProviders(<ReviewStudio projectId="prj_1" />);
    fireEvent.click(await screen.findByTestId('review-row-rev_001'));
    expect(await screen.findByTestId('review-studio-detail')).toBeDefined();
    expect(screen.queryByTestId('review-studio-empty')).toBeNull();
  });
});

describe('ReviewCard loading/error/minimal matrix', () => {
  it('shows the loading skeleton while context resolves', () => {
    authenticate();
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    expect(screen.getByTestId('review-card-loading')).toBeDefined();
  });

  it('recovers from context errors with retry (recovery: retry)', async () => {
    world.contextMode = 'error500';
    authenticate();
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    expect(await screen.findByTestId('review-card-error')).toBeDefined();
    const callsBefore = world.queueCalls;
    void callsBefore;
    world.contextMode = 'ok';
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(screen.queryByTestId('review-card-error')).toBeNull());
  });

  it('renders sparse contexts with fallback text (no crash, no blanks)', async () => {
    world.contextMode = 'minimal';
    authenticate();
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    await waitFor(() => expect(screen.queryByTestId('review-card-loading')).toBeNull(), { timeout: 5000 });
    expect(screen.queryByTestId('review-card-error')).toBeNull();
    expect(screen.getByTestId('review-card-title-rev_001').textContent).toContain('Open');
    expect(screen.getByTestId('review-card-project-rev_001').textContent).toContain('prj_1');
    expect(screen.queryByTestId('review-jump-rev_001')).toBeNull();
    expect(screen.queryByTestId('review-resolved-by-rev_001')).toBeNull();
    expect(screen.getByTestId('review-media-rev_001')).toBeDefined();
    expect(screen.getByTestId('review-transcript-rev_001').textContent).toContain('No transcript versions.');
    expect(screen.getByTestId('review-translation-rev_001').textContent).toContain('No translation candidates.');
    expect(screen.getByTestId('review-voice-rev_001').textContent).toContain('—');
    expect(screen.getByTestId('review-audio-rev_001').textContent).toContain('No preview artifact');
    expect(screen.getByTestId('review-sync-rev_001').textContent).toContain('in sync');
    expect(screen.getByTestId('review-sync-rev_001').textContent).toContain('No QC flags.');
    expect(screen.getByTestId('review-versions-rev_001').textContent).toContain('0 version(s)');
    expect(screen.getByTestId('review-versions-rev_001').textContent).not.toContain('truncated');
    expect(screen.getByTestId('review-history-rev_001').textContent).toContain('No audit entries yet.');
    expect(screen.getByTestId('review-permissions-rev_001').textContent).toContain('canResolve:false');
    expect(screen.queryByTestId('review-approve-rev_001')).toBeNull();
  });
});

describe('disposition endpoint/response matrix', () => {
  it('routes every action to its endpoint with versioned payloads', async () => {
    authenticate();
    const bodies: Record<string, unknown> = {};
    setInnerFetchForTests((async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = urlOf(input);
      const method = methodOf(input, init);
      if (method === 'POST' && url.includes('/reviews/rev_001/')) {
        bodies[url] = JSON.parse(String(init?.body ?? '{}')) as unknown;
        return jsonResponse({ reviewId: 'rev_001', status: 'Resolved', version: 3 });
      }
      return mockFetch(input, init);
    }) as typeof fetch);
    const actions = ['approve', 'reject', 'requeue', 'resolve-with-edit'] as const;
    for (const action of actions) {
      resetDispositionKeysForTests();
      const result = await executeDisposition(
        'rev_001',
        { action, expectedVersion: 2, reason: 'looks good', editText: action === 'resolve-with-edit' ? 'fixed' : undefined },
        { idempotencyKey: `key-${action}` },
      );
      expect(result.version).toBe(3);
      expect(result.reviewId).toBe('rev_001');
    }
    expect(Object.keys(bodies).length).toBe(4);
  });

  it('falls back to optimistic values for shapeless responses', async () => {
    authenticate();
    setInnerFetchForTests((async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = urlOf(input);
      const method = methodOf(input, init);
      if (method === 'POST' && url.includes('/reviews/rev_001/')) {
        return jsonResponse({ manualVersionId: 'mv_1', versionKind: 'manual' });
      }
      return mockFetch(input, init);
    }) as typeof fetch);
    resetDispositionKeysForTests();
    const result = await executeDisposition('rev_001', { action: 'approve', expectedVersion: 2, reason: 'ok' });
    expect(result.status).toBe('Approved');
    expect(result.version).toBe(3);
    expect(result.manualVersionId).toBe('mv_1');
    expect(result.versionKind).toBe('manual');
  });

  it('normalizes transport failures to AppError (recovery: retry same key)', async () => {
    authenticate();
    setInnerFetchForTests((async () => {
      throw new TypeError('fetch failed');
    }) as typeof fetch);
    await expect(
      executeDisposition('rev_001', { action: 'approve', expectedVersion: 1, reason: 'ok' }),
    ).rejects.toMatchObject({ retryable: true });
  });

  it('invalidates queue, context, and detail scopes after attempts', async () => {
    await invalidateDisposition(queryClient, 'prj_1', 'rev_001');
  });
});

describe('review queue hook guards', () => {
  it('partitions cache params per filter set', () => {
    expect(reviewListParamsFor({ ...EMPTY_REVIEW_FILTERS, severity: 'blocking' })).toMatchObject({ severity: 'blocking' });
    expect(reviewListParamsFor({ ...EMPTY_REVIEW_FILTERS })).toEqual({});
  });

  it('pages until hasMore is false', async () => {
    world.queueMode = 'paged';
    authenticate();
    const { props } = queueProps();
    renderWithProviders(<ReviewQueue {...props} />);
    const list = await screen.findByTestId('review-queue-list');
    expect(list.getAttribute('data-total')).toBe('3');
  });
});

describe('review helper sweep', () => {
  it('maps every disposition endpoint and reason requirement', () => {
    expect(dispositionEndpointFor('approve')).toContain('resolve');
    expect(dispositionEndpointFor('reject')).toContain('dismiss');
    expect(dispositionEndpointFor('requeue')).toContain('reopen');
    expect(dispositionEndpointFor('resolve-with-edit')).toContain('resolve-with-edit');
    expect(dispositionRequiresReason('approve')).toBe(false);
    expect(dispositionRequiresReason('reject')).toBe(true);
    expect(dispositionRequiresReason('requeue')).toBe(true);
    expect(dispositionRequiresReason('resolve-with-edit')).toBe(false);
    expect(allowedTokenFor('approve')).toBe('resolve');
    expect(allowedTokenFor('reject')).toBe('dismiss');
  });

  it('classifies conflict/resolved/reason guards with null safety', () => {
    expect(isReviewConflict({ code: 'REVIEW_VERSION_CONFLICT', status: 409 })).toBe(true);
    expect(isReviewConflict({ code: 'X', status: 500 })).toBe(false);
    expect(isReviewConflict(undefined)).toBe(false);
    expect(isReviewConflict(null)).toBe(false);
    expect(isAlreadyResolved({ code: 'REVIEW_ALREADY_RESOLVED' })).toBe(true);
    expect(isAlreadyResolved(undefined)).toBe(false);
    expect(isReasonRequired({ code: 'REVIEW_REASON_REQUIRED' })).toBe(true);
    expect(isReasonRequired({ status: 400 })).toBe(true);
    expect(isReasonRequired({ code: 'X', status: 500 })).toBe(false);
  });

  it('extracts versions and actors defensively', () => {
    expect(currentVersionFromDetails({ currentVersion: 4 })).toBe(4);
    expect(currentVersionFromDetails({ currentVersion: 'x' })).toBeUndefined();
    expect(currentVersionFromDetails(undefined)).toBeUndefined();
    expect(actorFromDetails({ actor: 'usr_9' })).toBe('usr_9');
    expect(actorFromDetails({})).toBeUndefined();
  });

  it('validates reasons and edits per action', () => {
    expect(validateReason('looks good', 'approve').valid).toBe(true);
    expect(validateReason('', 'reject').valid).toBe(false);
    expect(validateReason('', 'approve').valid).toBe(true);
    expect(validateReason('x'.repeat(600), 'approve').valid).toBe(false);
    expect(validateEditText('fixed text').valid).toBe(true);
    expect(validateEditText('').valid).toBe(false);
    expect(validateEditText('x'.repeat(6000)).valid).toBe(false);
  });

  it('detects active filters including the project scope', () => {
    expect(hasActiveReviewFilters({ ...EMPTY_REVIEW_FILTERS })).toBe(false);
    expect(hasActiveReviewFilters({ ...EMPTY_REVIEW_FILTERS, project: 'prj_1' })).toBe(true);
    expect(hasActiveReviewFilters({ ...EMPTY_REVIEW_FILTERS, severity: 'blocking' })).toBe(true);
  });

  it('round-trips filters through query strings and search params', () => {
    expect(toReviewListQuery({ ...EMPTY_REVIEW_FILTERS }).severity).toBeUndefined();
    expect(toReviewListQuery({ ...EMPTY_REVIEW_FILTERS, severity: 'blocking', age: 'older-than-7d' })).toMatchObject({
      severity: 'blocking',
    });
    const params = reviewFiltersToSearchParams({ ...EMPTY_REVIEW_FILTERS, severity: 'blocking' });
    expect(params.get('severity')).toBe('blocking');
    expect(reviewFiltersFromSearchParams(params).severity).toBe('blocking');
    expect(reviewFiltersFromSearchParams(new URLSearchParams('bogus=%ZZ')).severity).toBe('');
  });

  it('parses queue items and contexts defensively', () => {
    expect(parseReviewQueueItems({ items: 'nope' })).toEqual([]);
    expect(parseReviewQueueItems(null)).toEqual([]);
    expect(parseReviewContext(null)).toBeUndefined();
    expect(parseReviewContext({ item: null })).toBeUndefined();
    const parsed = parseReviewContext(contextBody('rev_001'));
    expect(parsed?.reviewId).toBe('rev_001');
  });

  it('sorts history newest-first with stable ties', () => {
    const sorted = sortHistoryNewestFirst([
      { id: 'a', createdAt: '2024-01-16T09:00:00Z' },
      { id: 'b', createdAt: '2024-01-16T10:00:00Z' },
      { id: 'c', createdAt: '2024-01-16T10:00:00Z' },
    ] as never);
    expect(sorted[0]?.id).toBe('b');
    expect(sorted.map((e) => e.id).sort()).toEqual(['a', 'b', 'c']);
  });

  it('builds versioned reasoned payloads and gates allowed actions', () => {
    const payload = buildDispositionPayload('approve', 2, 'ok', undefined);
    expect(payload).toMatchObject({ expectedVersion: 2, reason: 'ok' });
    const edited = buildDispositionPayload('resolve-with-edit', 2, 'ok', 'fixed');
    expect(edited).toMatchObject({ editText: 'fixed' });
    expect(isDispositionAllowed({ allowedActions: ['resolve'] } as never, 'approve')).toBe(true);
    expect(isDispositionAllowed({ allowedActions: [] } as never, 'approve')).toBe(false);
  });
});
