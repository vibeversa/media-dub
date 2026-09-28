// Delta 2: review remaining-branch closure.
//
// Covers pure `types.ts` defensive branches unreachable in reviewMatrix
// (non-object rows, PascalCase keys, missing ids, empty-text versions,
// history id fallback, sort ties with empty stamps, reason/conflict guards,
// payload fallbacks, version/actor extraction) plus the ReviewCard
// disposition-error branches (edit gate, already-resolved with/without
// actor, conflict with/without version, server reason/edit codes, generic
// errors, refresh + jump recovery), ReviewQueue empty/show-more/filter
// inputs, and ReviewStudio filter-sync + settle paths. Synthetic fixtures,
// fetch intercepted, text signals only.
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
  buildDispositionPayload,
  currentVersionFromDetails,
  isAlreadyResolved,
  isDispositionAllowed,
  isReasonRequired,
  isReviewConflict,
  parseReviewContext,
  parseReviewQueueItem,
  parseReviewQueueItems,
  sortHistoryNewestFirst,
  validateEditText,
  validateReason,
} from '../types.js';
import type { ReviewFilters } from '../types.js';
import { resetDispositionKeysForTests } from '../useDisposition.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, details: Record<string, unknown> = {}): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-r31b', details } },
    status,
  );
}

function queueRow(id: string, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id,
    projectId: 'prj_1',
    status: 'Open',
    reason: 'needs eyes',
    createdAt: '2024-01-16T10:00:00Z',
    ...overrides,
  };
}

function fullContext(reviewId: string, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    item: { id: reviewId, type: 'TRANSLATION_QUALITY', severity: 'high', status: 'Open', version: 2 },
    project: { id: 'prj_1', name: 'Pilot' },
    run: { id: 'run_1', status: 'Running' },
    segment: { id: 'seg_001', startMs: 1000, endMs: 3000, speakerId: 'spk_alice' },
    versions: {
      transcript: [
        { id: 'tr-v1', provider: 'acme', model: 'stt-v1', text: 'hello', isSelected: true, createdAt: '2024-01-15T12:00:00Z' },
      ],
      translation: [
        { id: 'tl-v1', primaryText: 'hola', provider: 'acme', model: 'mt-v1', isSelected: true, createdAt: '2024-01-15T13:00:00Z' },
      ],
      selectedTranscriptVersionId: 'tr-v1',
      selectedTranslationVersionId: 'tl-v1',
      selectionVersion: 2,
    },
    voice: { speakerId: 'spk_alice', voiceProfileId: 'voice_1', voiceId: 'stock-es-1', consentState: 'valid' },
    audio: { previewArtifactId: 'art_1' },
    sync: { offsetMs: 120, driftFlag: false },
    qc: { issues: [{ id: 'qc_1', code: 'QC_GAP', severity: 'low', message: '' }], evidenceArtifactIds: [] },
    actions: { allowed: ['resolve', 'dismiss', 'reopen', 'resolve-with-edit'] },
    permissions: { canResolve: true, canEdit: true },
    history: [{ id: 'h1', type: 'Opened', reviewer: 'sys', reason: '', createdAt: '' }],
    truncated: true,
    ...overrides,
  };
}

type MutationMode =
  | 'ok'
  | 'resolved-with-actor'
  | 'resolved-anon'
  | 'conflict-full'
  | 'conflict-bare'
  | 'reason-required'
  | 'edit-empty'
  | 'generic';

interface World {
  queueCount: number;
  mutationMode: MutationMode;
  mutationCalls: number;
  contextCalls: number;
}

let world: World;

function resetWorld(): void {
  world = { queueCount: 1, mutationMode: 'ok', mutationCalls: 0, contextCalls: 0 };
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.href;
  return (input as Request).url;
}

function methodOf(input: RequestInfo | URL, init?: RequestInit): string {
  if (typeof input !== 'string' && !(input instanceof URL)) {
    const request = input as Request;
    if (typeof request.method === 'string' && request.method !== '') return request.method.toUpperCase();
  }
  return (init?.method ?? 'GET').toUpperCase();
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) return jsonResponse({});
  if (url.includes('/reviews') && method === 'GET' && !url.includes('/reviews/')) {
    const items: Record<string, unknown>[] = [];
    for (let i = 0; i < world.queueCount; i += 1) {
      items.push(queueRow(`rev_${String(i + 1).padStart(3, '0')}`));
    }
    return jsonResponse({ items, page: 1, pageSize: 50, total: items.length, hasMore: false });
  }
  if (/\/reviews\/(rev_[^/?]+)(\/context)?$/.test(url) && method === 'GET') {
    world.contextCalls += 1;
    const match = /\/reviews\/(rev_[^/?]+)/.exec(url);
    return jsonResponse(fullContext(match?.[1] ?? 'rev_001'));
  }
  if (url.includes('/reviews/') && method === 'POST') {
    world.mutationCalls += 1;
    switch (world.mutationMode) {
      case 'resolved-with-actor':
        return errorEnvelope('REVIEW_ALREADY_RESOLVED', 409, { resolvedBy: 'usr_7' });
      case 'resolved-anon':
        return errorEnvelope('REVIEW_ALREADY_RESOLVED', 409, {});
      case 'conflict-full':
        return errorEnvelope('REVIEW_VERSION_CONFLICT', 409, { currentVersion: 5, actor: 'usr_9' });
      case 'conflict-bare':
        return errorEnvelope('CONFLICT', 409, {});
      case 'reason-required':
        return errorEnvelope('REVIEW_REASON_REQUIRED', 400, {});
      case 'edit-empty':
        return errorEnvelope('REVIEW_EDIT_EMPTY', 400, {});
      default:
        return jsonResponse({ reviewId: 'rev_001', status: 'Resolved', version: 3 });
    }
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

describe('parseReviewQueueItem defensive branches', () => {
  it('rejects non-objects and id-less rows', () => {
    expect(parseReviewQueueItem(undefined)).toBeUndefined();
    expect(parseReviewQueueItem(null)).toBeUndefined();
    expect(parseReviewQueueItem(42)).toBeUndefined();
    expect(parseReviewQueueItem([])).toBeUndefined();
    expect(parseReviewQueueItem('row')).toBeUndefined();
    expect(parseReviewQueueItem({ status: 'Open' })).toBeUndefined();
    expect(parseReviewQueueItem({ id: '' })).toBeUndefined();
  });

  it('reads PascalCase keys with fallbacks', () => {
    const parsed = parseReviewQueueItem({
      Id: 'rev_9',
      ProjectId: 'prj_9',
      Status: 'Resolved',
      Reason: 'done',
      CreatedAt: '2024-01-16T10:00:00Z',
      ResolvedAt: '2024-01-17T10:00:00Z',
      Type: 'SYNC',
      Severity: 'high',
      SpeakerId: 'spk_a',
      Language: 'es',
    });
    expect(parsed?.id).toBe('rev_9');
    expect(parsed?.status).toBe('Resolved');
    expect(parsed?.resolvedAt).toBe('2024-01-17T10:00:00Z');
    expect(parsed?.type).toBe('SYNC');
    expect(parsed?.severity).toBe('high');
  });

  it('defaults status and drops non-string reason/resolvedAt', () => {
    const parsed = parseReviewQueueItem({ id: 'rev_1', reason: 7, resolvedAt: 7 });
    expect(parsed?.status).toBe('Open');
    expect(parsed?.reason).toBe('');
    expect(parsed?.resolvedAt).toBeUndefined();
    expect(parseReviewQueueItems({ items: [queueRow('a'), null, { nope: true }] }).length).toBe(1);
    expect(parseReviewQueueItems([queueRow('b')]).length).toBe(1);
  });
});

describe('parseReviewContext defensive branches', () => {
  it('rejects non-objects and id-less aggregates', () => {
    expect(parseReviewContext(undefined)).toBeUndefined();
    expect(parseReviewContext(null)).toBeUndefined();
    expect(parseReviewContext([])).toBeUndefined();
    expect(parseReviewContext({})).toBeUndefined();
    expect(parseReviewContext({ item: { id: '' } })).toBeUndefined();
  });

  it('reads top-level reviewId fallback and PascalCase sections', () => {
    const parsed = parseReviewContext({
      ReviewId: 'rev_top',
      Item: { Type: 'X', Severity: 'low', Status: 'Open', Version: 1 },
      Project: { Id: 'prj_1', Name: 'Pilot' },
      Run: { Id: 'run_1', Status: 'Running' },
      Segment: { Id: 'seg_1', StartMs: 5, EndMs: 9, SpeakerId: 'spk_1' },
      Versions: { Transcript: [], Translation: [] },
      Voice: { SpeakerId: 'spk_1' },
      Audio: {},
      Sync: {},
      Qc: { Issues: [] },
      Actions: { Allowed: ['resolve'] },
      Permissions: { CanResolve: true, CanEdit: false },
      History: [],
    });
    expect(parsed?.reviewId).toBe('rev_top');
    expect(parsed?.segmentStartMs).toBe(5);
    expect(parsed?.canResolve).toBe(true);
  });

  it('skips bad version/qc/history rows and falls back to neutral text', () => {
    const parsed = parseReviewContext(
      fullContext('rev_001', {
        versions: {
          transcript: [null, { nope: true }, { id: 't1', text: '', provider: '', model: '' }],
          translation: 'nope',
        },
        qc: { issues: [null, { nope: true }, { id: 'q1', code: '', severity: '', message: 7 }] },
        history: [null, { nope: true }, { decisionId: '', type: '', reviewer: '' }],
      }),
    );
    expect(parsed?.transcript.length).toBe(1);
    expect(parsed?.transcript[0]?.provider).toBe('unknown');
    expect(parsed?.translation).toEqual([]);
    expect(parsed?.qcIssues.length).toBe(1);
    expect(parsed?.qcIssues[0]?.code).toBe('unknown');
    expect(parsed?.history.length).toBe(2);
  });

  it('reads selected version ids from the versions section and normalizes rows + timing', () => {
    const parsed = parseReviewContext(
      fullContext('rev_001', {
        segment: { id: 'seg_1', startMs: -50, endMs: -80, speakerId: '' },
        versions: {
          transcript: [{ id: 't1', provider: 'a', model: 'm', text: 'picked', isSelected: true }],
          translation: [{ id: 'l1', provider: 'a', model: 'm', text: 'elegido', isSelected: true }],
          selectedTranscriptVersionId: 't1',
          selectedTranslationVersionId: 'l1',
          selectionVersion: 3,
        },
      }),
    );
    expect(parsed).toBeDefined();
    expect(parsed?.selectedTranscriptVersionId).toBe('t1');
    expect(parsed?.selectedTranslationVersionId).toBe('l1');
    expect(parsed?.selectionVersion).toBe(3);
    expect(parsed?.transcript[0]?.text).toBe('picked');
    expect(parsed?.translation[0]?.text).toBe('elegido');
    expect(parsed?.segmentStartMs).toBe(-50);
    expect(parsed?.segmentEndMs).toBe(-80);
    // Empty speakerId is not a valid identifier: dropped, never rendered as ''.
    expect(parsed?.segmentSpeakerId).toBeUndefined();
  });
});

describe('review guard + payload branches', () => {
  it('sorts empty stamps last with stable ties', () => {
    const sorted = sortHistoryNewestFirst([
      { id: 'a', createdAt: '' },
      { id: 'b', createdAt: '' },
      { id: 'c', createdAt: '2024-01-16T10:00:00Z' },
    ] as never);
    expect(sorted[0]?.id).toBe('c');
    expect(sortHistoryNewestFirst([])).toEqual([]);
  });

  it('classifies conflicts, resolved, and reason guards exhaustively', () => {
    expect(isReviewConflict({ code: 'SELECTION_CONFLICT' })).toBe(true);
    expect(isReviewConflict({ code: 'CONFLICT' })).toBe(true);
    expect(isReviewConflict({ status: 409 })).toBe(true);
    expect(isReviewConflict({ status: 412 })).toBe(true);
    expect(isReviewConflict({ code: 'NOPE', status: 500 })).toBe(false);
    expect(isAlreadyResolved(null)).toBe(false);
    expect(isAlreadyResolved({ code: 'NOPE' })).toBe(false);
    expect(isReasonRequired(undefined)).toBe(false);
    expect(isReasonRequired(null)).toBe(false);
    expect(isReasonRequired({ code: 'REVIEW_REASON_REQUIRED' })).toBe(true);
    expect(isReasonRequired({ status: 400 })).toBe(true);
    expect(isDispositionAllowed({ allowedActions: ['RESOLVE'] } as never, 'approve')).toBe(true);
  });

  it('validates overlong edits and builds fallback payloads', () => {
    expect(validateReason('   ', 'approve').valid).toBe(true);
    expect(validateEditText('   ').valid).toBe(false);
    expect(validateEditText('x'.repeat(6000)).valid).toBe(false);
    expect(buildDispositionPayload('approve', 1, '   ').reason).toBe('Approved via review studio.');
    expect(buildDispositionPayload('reject', 1, '').reason).toBe('Reviewed via review studio.');
    expect(buildDispositionPayload('resolve-with-edit', 1, 'ok', undefined).editText).toBe('');
    expect(currentVersionFromDetails({ currentSelectionVersion: 7 })).toBe(7);
    expect(currentVersionFromDetails({ version: 2.9 })).toBe(2);
    expect(currentVersionFromDetails({ currentVersion: Number.NaN })).toBeUndefined();
    expect(actorFromDetails({ resolvedBy: '' })).toBeUndefined();
    expect(actorFromDetails({ actor: 7 })).toBeUndefined();
    expect(actorFromDetails({ reviewer: 'usr_1' })).toBe('usr_1');
  });
});

describe('ReviewCard disposition-error branches', () => {
  it('blocks resolve-with-edit on empty edit text (recovery: fix input)', async () => {
    authenticate();
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    expect(await screen.findByTestId('review-resolve-edit-rev_001')).toBeDefined();
    fireEvent.click(screen.getByTestId('review-resolve-edit-rev_001'));
    expect(await screen.findByTestId('review-field-error-rev_001')).toBeDefined();
    expect(screen.getByTestId('review-field-error-rev_001').textContent).toContain('corrected text');
    expect(world.mutationCalls).toBe(0);
  });

  it('blocks reject without a reason (recovery: add reason)', async () => {
    authenticate();
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    expect(await screen.findByTestId('review-reject-rev_001')).toBeDefined();
    fireEvent.click(screen.getByTestId('review-reject-rev_001'));
    expect(await screen.findByTestId('review-field-error-rev_001')).toBeDefined();
    expect(world.mutationCalls).toBe(0);
  });

  it('names the resolver on already-resolved with actor (recovery: refresh)', async () => {
    world.mutationMode = 'resolved-with-actor';
    authenticate();
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    expect(await screen.findByTestId('review-approve-rev_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'looks good' } });
    fireEvent.click(screen.getByTestId('review-approve-rev_001'));
    expect(await screen.findByTestId('review-stale-rev_001')).toBeDefined();
    expect(screen.getByTestId('review-stale-rev_001').textContent).toContain('usr_7');
    expect(screen.getByTestId('review-resolved-by-rev_001').textContent).toContain('usr_7');
  });

  it('falls back to another reviewer when the actor is absent', async () => {
    world.mutationMode = 'resolved-anon';
    authenticate();
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    expect(await screen.findByTestId('review-approve-rev_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'looks good' } });
    fireEvent.click(screen.getByTestId('review-approve-rev_001'));
    expect(await screen.findByTestId('review-stale-rev_001')).toBeDefined();
    expect(screen.getByTestId('review-stale-rev_001').textContent).toContain('another reviewer');
  });

  it('preserves reason text on version conflicts with current version (recovery: retry same key)', async () => {
    world.mutationMode = 'conflict-full';
    authenticate();
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    expect(await screen.findByTestId('review-approve-rev_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'my reasoning' } });
    fireEvent.click(screen.getByTestId('review-approve-rev_001'));
    const banner = await screen.findByTestId('review-stale-rev_001');
    expect(banner.textContent).toContain('v5');
    expect(banner.textContent).toContain('same key');
    expect((screen.getByTestId('review-reason-rev_001') as HTMLTextAreaElement).value).toBe('my reasoning');
    expect(screen.getByTestId('review-resolved-by-rev_001').textContent).toContain('usr_9');
    const callsBefore = world.contextCalls;
    fireEvent.click(screen.getByTestId('review-refresh-rev_001'));
    await waitFor(() => expect(world.contextCalls).toBeGreaterThan(callsBefore));
  });

  it('renders the bare conflict banner without version or actor', async () => {
    world.mutationMode = 'conflict-bare';
    authenticate();
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    expect(await screen.findByTestId('review-approve-rev_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'ok' } });
    fireEvent.click(screen.getByTestId('review-approve-rev_001'));
    const banner = await screen.findByTestId('review-stale-rev_001');
    expect(banner.textContent).toContain('changed while you worked');
    expect(banner.textContent).not.toContain('now v');
  });

  it('surfaces server reason/edit codes and generic failures inline', async () => {
    authenticate();
    world.mutationMode = 'reason-required';
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    expect(await screen.findByTestId('review-approve-rev_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'ok' } });
    fireEvent.click(screen.getByTestId('review-approve-rev_001'));
    expect(await screen.findByTestId('review-field-error-rev_001')).toBeDefined();
    cleanup();
    queryClient.clear();
    world.mutationMode = 'edit-empty';
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    expect(await screen.findByTestId('review-approve-rev_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'ok' } });
    fireEvent.click(screen.getByTestId('review-approve-rev_001'));
    expect(await screen.findByTestId('review-field-error-rev_001')).toBeDefined();
    cleanup();
    queryClient.clear();
    world.mutationMode = 'generic';
    setInnerFetchForTests((async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = urlOf(input);
      const method = methodOf(input, init);
      if (url.includes('/reviews/') && method === 'POST') {
        world.mutationCalls += 1;
        return errorEnvelope('INTERNAL_ERROR', 500);
      }
      return mockFetch(input, init);
    }) as typeof fetch);
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    expect(await screen.findByTestId('review-approve-rev_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'ok' } });
    fireEvent.click(screen.getByTestId('review-approve-rev_001'));
    expect(await screen.findByTestId('review-field-error-rev_001')).toBeDefined();
  });

  it('jumps to the issue timestamp and clears field errors on edit', async () => {
    authenticate();
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    expect(await screen.findByTestId('review-jump-rev_001')).toBeDefined();
    fireEvent.click(screen.getByTestId('review-jump-rev_001'));
    fireEvent.click(screen.getByTestId('review-reject-rev_001'));
    expect(await screen.findByTestId('review-field-error-rev_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'now with reason' } });
    await waitFor(() => expect(screen.queryByTestId('review-field-error-rev_001')).toBeNull());
    fireEvent.click(screen.getByTestId('review-resolve-edit-rev_001'));
    expect(await screen.findByTestId('review-field-error-rev_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('review-edit-rev_001'), { target: { value: 'fixed text' } });
    await waitFor(() => expect(screen.queryByTestId('review-field-error-rev_001')).toBeNull());
  });

  it('renders empty-message QC rows and truncated version notes', async () => {
    authenticate();
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    expect(await screen.findByTestId('review-qc-qc_1')).toBeDefined();
    expect(screen.getByTestId('review-qc-qc_1').textContent).toContain('QC_GAP');
    expect(screen.getByTestId('review-versions-rev_001').textContent).toContain('truncated');
    expect(screen.getByTestId('review-selection-version-rev_001').textContent).toContain('Selection v2');
  });
});

describe('ReviewQueue empty/show-more/filter-input branches', () => {
  it('renders the true-empty state without filter chrome (recovery: none needed)', async () => {
    world.queueCount = 0;
    authenticate();
    const { props } = queueProps();
    renderWithProviders(<ReviewQueue {...props} />);
    expect(await screen.findByTestId('review-queue-empty')).toBeDefined();
    expect(screen.queryByTestId('review-queue-empty-filtered')).toBeNull();
  });

  it('windows large backlogs with a show-more control (recovery: show more)', async () => {
    world.queueCount = 60;
    authenticate();
    const { props } = queueProps();
    renderWithProviders(<ReviewQueue {...props} />);
    const list = await screen.findByTestId('review-queue-list');
    expect(list.getAttribute('data-total')).toBe('60');
    expect(list.getAttribute('data-rendered')).toBe('50');
    fireEvent.click(screen.getByTestId('review-queue-more'));
    await waitFor(() => expect(screen.getByTestId('review-queue-list').getAttribute('data-rendered')).toBe('60'));
    expect(screen.queryByTestId('review-queue-more')).toBeNull();
  });

  it('syncs draft when filters change externally and edits every input', async () => {
    authenticate();
    const { props, calls } = queueProps();
    const { rerender } = render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <ToastProvider>
            <MemoryRouter>
              <ReviewQueue {...props} />
            </MemoryRouter>
          </ToastProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    expect(await screen.findByTestId('review-queue-list')).toBeDefined();
    for (const [testid, key, value] of [
      ['review-filter-type', 'type', 'SYNC'],
      ['review-filter-speaker', 'speaker', 'spk_a'],
      ['review-filter-language', 'language', 'es'],
      ['review-filter-age', 'age', 'older-than-7d'],
    ] as const) {
      fireEvent.change(screen.getByTestId(testid), { target: { value } });
      fireEvent.blur(screen.getByTestId(testid));
      expect(calls.filters[calls.filters.length - 1]?.[key]).toBe(value);
    }
    rerender(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <ToastProvider>
            <MemoryRouter>
              <ReviewQueue {...props} filters={{ ...EMPTY_REVIEW_FILTERS, severity: 'high' }} />
            </MemoryRouter>
          </ToastProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    expect((screen.getByTestId('review-filter-severity') as HTMLInputElement).value).toBe('high');
  });
});

describe('ReviewStudio filter-sync and settle branches', () => {
  it('syncs queue filter edits into the URL and selects rows', async () => {
    authenticate();
    renderWithProviders(<ReviewStudio projectId="prj_1" />, '/review?severity=high');
    expect(await screen.findByTestId('review-queue-list')).toBeDefined();
    fireEvent.change(screen.getByTestId('review-filter-status'), { target: { value: 'open' } });
    fireEvent.blur(screen.getByTestId('review-filter-status'));
    fireEvent.click(await screen.findByTestId('review-row-rev_001'));
    expect(await screen.findByTestId('review-studio-detail')).toBeDefined();
  });

  it('clears the selection when the card settles', async () => {
    authenticate();
    renderWithProviders(<ReviewStudio projectId="prj_1" />);
    fireEvent.click(await screen.findByTestId('review-row-rev_001'));
    expect(await screen.findByTestId('review-card-rev_001')).toBeDefined();
    expect(await screen.findByTestId('review-approve-rev_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'looks good' } });
    fireEvent.click(screen.getByTestId('review-approve-rev_001'));
    expect(await screen.findByTestId('review-studio-empty')).toBeDefined();
  });

  it('writes the project scope from the URL input', async () => {
    authenticate();
    renderWithProviders(<ReviewStudio />, '/review');
    const input = await screen.findByTestId('review-filter-project');
    fireEvent.change(input, { target: { value: 'prj_9' } });
    expect(screen.getByTestId('review-studio').getAttribute('data-project')).toBe('prj_9');
  });
});
