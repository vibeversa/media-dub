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
import { useTimelinePlayerStore } from '../../timeline/playerStore.js';
import { ReviewCard } from '../ReviewCard.js';
import { ReviewQueue } from '../ReviewQueue.js';
import { ReviewStudio } from '../ReviewStudio.js';
import { dispositionKeyFor, resetDispositionKeysForTests } from '../useDisposition.js';
import { executeDisposition } from '../useDisposition.js';
import {
  DISPOSITION_ACTIONS,
  EMPTY_REVIEW_FILTERS,
  REVIEW_MAX_REASON_LENGTH,
  actorFromDetails,
  buildDispositionPayload,
  currentVersionFromDetails,
  dispositionEndpointFor,
  isDispositionAllowed,
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

function queueBody(count: number): Record<string, unknown> {
  const items: Record<string, unknown>[] = [];
  for (let i = 0; i < count; i += 1) {
    items.push(makeQueueRow(`rev_${String(i + 1).padStart(3, '0')}`));
  }
  return { items, page: 1, pageSize: 50, total: count, hasMore: false };
}

function contextBody(reviewId: string, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  const base: Record<string, unknown> = {
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
      { id: 'h2', type: 'Approve', reviewer: 'usr_2', reason: 'looks good', createdAt: '2024-01-16T09:30:00Z' },
    ],
    truncated: false,
  };
  return { ...base, ...overrides };
}

type MutationBehavior = 'ok' | 'conflict' | 'already-resolved' | 'reason-required';

interface ReviewWorld {
  queueCalls: number;
  queueUrls: string[];
  contextCalls: number;
  mutationCalls: number;
  mutationUrls: string[];
  mutationBodies: unknown[];
  mutationKeys: (string | null)[];
  auditCount: number;
  seenKeys: Map<string, unknown>;
  mutationBehavior: MutationBehavior;
  contextOverrides: Record<string, unknown>;
  queueEmpty: boolean;
}

function newWorld(overrides: Partial<ReviewWorld> = {}): ReviewWorld {
  return {
    queueCalls: 0,
    queueUrls: [],
    contextCalls: 0,
    mutationCalls: 0,
    mutationUrls: [],
    mutationBodies: [],
    mutationKeys: [],
    auditCount: 0,
    seenKeys: new Map(),
    mutationBehavior: 'ok',
    contextOverrides: {},
    queueEmpty: false,
    ...overrides,
  };
}

let world: ReviewWorld = newWorld();

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

function headerOf(init: RequestInit | undefined, name: string): string | null {
  const headers = init?.headers;
  if (headers instanceof Headers) {
    return headers.get(name);
  }
  if (typeof headers === 'object' && headers !== null && !Array.isArray(headers)) {
    const record = headers as Record<string, string>;
    for (const key of Object.keys(record)) {
      if (key.toLowerCase() === name.toLowerCase()) {
        return record[key] ?? null;
      }
    }
  }
  return null;
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
  if (url.includes('/output/download') && method === 'GET') {
    return jsonResponse({ downloadUrl: 'https://example.com/media.mp4', expiresAt: '2026-09-26T00:00:00Z' });
  }
  if (method === 'POST' && /\/reviews\/rev_[^/]+\/(resolve-with-edit|resolve|dismiss|reopen)$/.test(url)) {
    world.mutationCalls += 1;
    world.mutationUrls.push(url);
    const body = bodyOf(init);
    world.mutationBodies.push(body);
    const key = headerOf(init, 'Idempotency-Key');
    world.mutationKeys.push(key);
    if (world.mutationBehavior === 'conflict') {
      return errorEnvelope('REVIEW_VERSION_CONFLICT', 409, { currentVersion: 2 });
    }
    if (world.mutationBehavior === 'already-resolved') {
      return errorEnvelope('REVIEW_ALREADY_RESOLVED', 409, { currentStatus: 'Approved', resolvedBy: 'usr_other' });
    }
    if (world.mutationBehavior === 'reason-required') {
      return errorEnvelope('REVIEW_REASON_REQUIRED', 400);
    }
    const bodyRecord = (body ?? {}) as Record<string, unknown>;
    const keyText = key ?? '';
    const hash = JSON.stringify(bodyRecord);
    if (world.seenKeys.has(`${keyText}:${hash}`)) {
      return jsonResponse({ reviewId: 'rev_001', status: 'Approved', version: 2, manualVersionId: null, versionKind: null });
    }
    world.seenKeys.set(`${keyText}:${hash}`, body);
    world.auditCount += 1;
    const isEdit = url.endsWith('/resolve-with-edit');
    return jsonResponse({
      reviewId: 'rev_001',
      status: url.endsWith('/reopen') ? 'Open' : url.endsWith('/dismiss') ? 'Rejected' : 'Approved',
      version: 2,
      manualVersionId: isEdit ? 'ver_manual_1' : null,
      versionKind: isEdit ? 'manual' : null,
    });
  }
  if (method === 'GET' && /\/reviews\/rev_[^/?]+(\/context)?(\?|$)/.test(url)) {
    world.contextCalls += 1;
    const match = /\/reviews\/(rev_[^/?]+)/.exec(url);
    const reviewId = match?.[1] ?? 'rev_001';
    return jsonResponse(contextBody(reviewId, world.contextOverrides));
  }
  if (method === 'GET' && url.includes('/projects/prj_1/reviews')) {
    world.queueCalls += 1;
    world.queueUrls.push(url);
    if (world.queueEmpty) {
      return jsonResponse({ items: [], page: 1, pageSize: 50, total: 0, hasMore: false });
    }
    return jsonResponse(queueBody(3));
  }
  if (method === 'GET' && url.includes('/reviews')) {
    world.queueCalls += 1;
    world.queueUrls.push(url);
    return jsonResponse(queueBody(1));
  }
  return jsonResponse({});
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit']);
}

function renderWithProviders(node: React.ReactNode): void {
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <MemoryRouter>{node}</MemoryRouter>
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

function stubMediaElement(): void {
  const playStub = vi.fn().mockResolvedValue(undefined);
  const pauseStub = vi.fn();
  Object.defineProperty(window.HTMLMediaElement.prototype, 'play', {
    configurable: true,
    writable: true,
    value: playStub,
  });
  Object.defineProperty(window.HTMLMediaElement.prototype, 'pause', {
    configurable: true,
    writable: true,
    value: pauseStub,
  });
}

beforeEach(() => {
  world = newWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  resetDispositionKeysForTests();
  useTimelinePlayerStore.getState().resetForTests();
  queryClient.clear();
  stubMediaElement();
  authenticate();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  resetDispositionKeysForTests();
  useTimelinePlayerStore.getState().resetForTests();
  queryClient.clear();
  vi.restoreAllMocks();
});

describe('pure review helpers', () => {
  it('maps every filter to a server-side query param', () => {
    const query = toReviewListQuery({
      project: 'prj_1',
      severity: 'high',
      status: 'Open',
      type: 'TRANSLATION_QUALITY',
      speaker: 'spk_alice',
      language: 'es',
      age: 'older-than-7d',
    });
    expect(query['severity']).toBe('high');
    expect(query['status']).toBe('Open');
    expect(query['type']).toBe('TRANSLATION_QUALITY');
    expect(query['speakerId']).toBe('spk_alice');
    expect(query['language']).toBe('es');
    expect(query['age']).toBe('older-than-7d');
    expect('project' in query).toBe(false);
    const empty = toReviewListQuery(EMPTY_REVIEW_FILTERS);
    expect(Object.keys(empty)).toHaveLength(0);
  });

  it('round-trips filters through search params', () => {
    const filters = {
      project: 'prj_1',
      severity: 'high',
      status: '',
      type: 'QC',
      speaker: '',
      language: 'es',
      age: '',
    };
    const params = reviewFiltersToSearchParams(filters);
    expect(params.get('project')).toBe('prj_1');
    expect(params.get('status')).toBeNull();
    const parsed = reviewFiltersFromSearchParams(params);
    expect(parsed).toEqual(filters);
  });

  it('builds versioned, reasoned payloads per action', () => {
    expect(buildDispositionPayload('approve', 1, 'looks good')).toEqual({ expectedVersion: 1, reason: 'looks good' });
    expect(buildDispositionPayload('reject', 2, 'bad take')).toEqual({ expectedVersion: 2, reason: 'bad take' });
    const edit = buildDispositionPayload('resolve-with-edit', 3, 'fixed', 'hola mundo corregido');
    expect(edit.editText).toBe('hola mundo corregido');
    expect(edit.expectedVersion).toBe(3);
    expect(dispositionEndpointFor('approve')).toBe('resolve');
    expect(dispositionEndpointFor('reject')).toBe('dismiss');
    expect(dispositionEndpointFor('requeue')).toBe('reopen');
    expect(dispositionEndpointFor('resolve-with-edit')).toBe('resolve-with-edit');
    expect(DISPOSITION_ACTIONS).toHaveLength(4);
  });

  it('validates reasons and edits without sending', () => {
    expect(validateReason('', 'reject').valid).toBe(false);
    expect(validateReason('', 'approve').valid).toBe(true);
    expect(validateReason('x'.repeat(REVIEW_MAX_REASON_LENGTH + 1), 'approve').valid).toBe(false);
    expect(validateEditText('').valid).toBe(false);
    expect(validateEditText('fixed text').valid).toBe(true);
    expect(isReviewConflict({ code: 'REVIEW_VERSION_CONFLICT', status: 409 })).toBe(true);
    expect(isReviewConflict({ code: 'CONFLICT', status: 409 })).toBe(true);
    expect(isReviewConflict({ status: 412 })).toBe(true);
    expect(isReviewConflict(null)).toBe(false);
    expect(currentVersionFromDetails({ currentVersion: 4 })).toBe(4);
    expect(actorFromDetails({ resolvedBy: 'usr_9' })).toBe('usr_9');
  });

  it('parses queue and context defensively with newest-first history', () => {
    expect(parseReviewQueueItems(queueBody(2))).toHaveLength(2);
    expect(parseReviewQueueItems({ items: [{ noId: true }] })).toHaveLength(0);
    const parsed = parseReviewContext(contextBody('rev_001'));
    expect(parsed?.reviewId).toBe('rev_001');
    expect(parsed?.transcript[0]?.text).toBe('hello world');
    expect(parsed?.translation[0]?.text).toBe('hola mundo');
    expect(parsed?.allowedActions).toContain('resolve');
    const sorted = sortHistoryNewestFirst(parsed?.history ?? []);
    expect(sorted[0]?.id).toBe('h2');
    expect(sorted[1]?.id).toBe('h1');
    expect(isDispositionAllowed(parsed!, 'approve')).toBe(true);
  });
});

describe('review queue', () => {
  it('sends every active filter server-side', async () => {
    renderWithProviders(
      <ReviewQueue
        projectId="prj_1"
        filters={{ project: '', severity: 'high', status: 'Open', type: 'TRANSLATION_QUALITY', speaker: 'spk_alice', language: 'es', age: 'older-than-7d' }}
        onFiltersChange={() => undefined}
        selectedReviewId={undefined}
        onSelect={() => undefined}
      />,
    );
    await waitFor(() => {
      expect(screen.getByTestId('review-queue-list')).toBeDefined();
    });
    expect(world.queueCalls).toBeGreaterThan(0);
    const url = world.queueUrls[0] ?? '';
    expect(url).toContain('severity=high');
    expect(url).toContain('status=Open');
    expect(url).toContain('type=TRANSLATION_QUALITY');
    expect(url).toContain('speakerId=spk_alice');
    expect(url).toContain('language=es');
    expect(url).toContain('age=');
  });

  it('distinguishes filtered-empty from true-empty with clear action', async () => {
    world.queueEmpty = true;
    const seen: string[] = [];
    renderWithProviders(
      <ReviewQueue
        projectId="prj_1"
        filters={{ ...EMPTY_REVIEW_FILTERS, status: 'Open' }}
        onFiltersChange={(next) => {
          seen.push(next.status);
        }}
        selectedReviewId={undefined}
        onSelect={() => undefined}
      />,
    );
    await waitFor(() => {
      expect(screen.getByTestId('review-queue-empty-filtered')).toBeDefined();
    });
    fireEvent.click(screen.getByTestId('review-clear-filters'));
    expect(seen).toEqual(['']);
  });

  it('virtualizes large queues with total and rendered counts', async () => {
    renderWithProviders(
      <ReviewQueue
        projectId="prj_1"
        filters={EMPTY_REVIEW_FILTERS}
        onFiltersChange={() => undefined}
        selectedReviewId={undefined}
        onSelect={() => undefined}
      />,
    );
    await waitFor(() => {
      expect(screen.getByTestId('review-queue-list')).toBeDefined();
    });
    const list = screen.getByTestId('review-queue-list');
    expect(list.getAttribute('data-total')).toBe('3');
    expect(Number(list.getAttribute('data-rendered') ?? '0')).toBeGreaterThan(0);
  });
});

describe('review dispositions', () => {
  it('sends idempotency key plus version plus reason per action', async () => {
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    await waitFor(() => {
      expect(screen.getByTestId('review-reason-rev_001')).toBeDefined();
    });
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'looks good' } });
    fireEvent.click(screen.getByTestId('review-approve-rev_001'));
    await waitFor(() => {
      expect(world.mutationCalls).toBe(1);
    });
    expect(world.mutationUrls[0]).toContain('/resolve');
    const body = world.mutationBodies[0] as Record<string, unknown>;
    expect(body['expectedVersion']).toBe(1);
    expect(body['reason']).toBe('looks good');
    expect(world.mutationKeys[0]).toBeTruthy();
  });

  it('reuses the same key across retries with a single audit row', async () => {
    const first = dispositionKeyFor('rev_001', 'approve');
    const second = dispositionKeyFor('rev_001', 'approve');
    expect(first).toBe(second);
    const payload = { action: 'approve' as const, expectedVersion: 1, reason: 'looks good' };
    await executeDisposition('rev_001', payload, { idempotencyKey: first });
    await executeDisposition('rev_001', payload, { idempotencyKey: first });
    expect(world.mutationCalls).toBe(2);
    expect(world.mutationKeys[0]).toBe(world.mutationKeys[1]);
    expect(world.auditCount).toBe(1);
  });

  it('shows a stale banner on 409 and preserves reason text', async () => {
    world.mutationBehavior = 'conflict';
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    await waitFor(() => {
      expect(screen.getByTestId('review-reason-rev_001')).toBeDefined();
    });
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'my preserved note' } });
    fireEvent.click(screen.getByTestId('review-approve-rev_001'));
    await waitFor(() => {
      expect(screen.getByTestId('review-stale-rev_001')).toBeDefined();
    });
    expect((screen.getByTestId('review-reason-rev_001') as HTMLTextAreaElement).value).toBe('my preserved note');
    expect(world.contextCalls).toBeGreaterThan(1);
  });

  it('marks resolved-by another reviewer without duplicating dispositions', async () => {
    world.mutationBehavior = 'already-resolved';
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    await waitFor(() => {
      expect(screen.getByTestId('review-reason-rev_001')).toBeDefined();
    });
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'late approve' } });
    fireEvent.click(screen.getByTestId('review-approve-rev_001'));
    await waitFor(() => {
      expect(screen.getByTestId('review-resolved-by-rev_001')).toBeDefined();
    });
    expect(screen.getByTestId('review-resolved-by-rev_001').textContent).toContain('usr_other');
    expect(world.auditCount).toBe(0);
  });

  it('hides disallowed actions while keeping the rationale in history', async () => {
    world.contextOverrides = {
      actions: { allowed: ['resolve'] },
      permissions: { canResolve: true, canEdit: false },
    };
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    await waitFor(() => {
      expect(screen.getByTestId('review-reason-rev_001')).toBeDefined();
    });
    expect(screen.queryByTestId('review-approve-rev_001')).not.toBeNull();
    expect(screen.queryByTestId('review-reject-rev_001')).toBeNull();
    expect(screen.queryByTestId('review-requeue-rev_001')).toBeNull();
    expect(screen.getByTestId('review-history-rev_001')).toBeDefined();
  });

  it('embeds the manual edit inline for resolve-with-edit', async () => {
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    await waitFor(() => {
      expect(screen.getByTestId('review-reason-rev_001')).toBeDefined();
    });
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'fixed translation' } });
    fireEvent.change(screen.getByTestId('review-edit-rev_001'), { target: { value: 'hola mundo corregido' } });
    fireEvent.click(screen.getByTestId('review-resolve-edit-rev_001'));
    await waitFor(() => {
      expect(world.mutationCalls).toBe(1);
    });
    expect(world.mutationUrls[0]).toContain('/resolve-with-edit');
    const body = world.mutationBodies[0] as Record<string, unknown>;
    expect(body['editText']).toBe('hola mundo corregido');
    expect(body['reason']).toBe('fixed translation');
  });

  it('blocks overlong reasons client-side without sending', async () => {
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    await waitFor(() => {
      expect(screen.getByTestId('review-reason-rev_001')).toBeDefined();
    });
    fireEvent.change(screen.getByTestId('review-reason-rev_001'), { target: { value: 'x'.repeat(REVIEW_MAX_REASON_LENGTH + 10) } });
    fireEvent.click(screen.getByTestId('review-approve-rev_001'));
    await waitFor(() => {
      expect(screen.getByTestId('review-field-error-rev_001')).toBeDefined();
    });
    expect(world.mutationCalls).toBe(0);
  });

  it('renders full single-screen context without navigating away', async () => {
    renderWithProviders(<ReviewCard projectId="prj_1" reviewId="rev_001" />);
    await waitFor(() => {
      expect(screen.getByTestId('review-reason-rev_001')).toBeDefined();
    });
    expect(screen.getByTestId('review-media-rev_001')).toBeDefined();
    expect(screen.getByTestId('review-transcript-rev_001').textContent).toContain('hello world');
    expect(screen.getByTestId('review-translation-rev_001').textContent).toContain('hola mundo');
    expect(screen.getByTestId('review-voice-rev_001').textContent).toContain('stock-es-1');
    expect(screen.getByTestId('review-audio-rev_001')).toBeDefined();
    expect(screen.getByTestId('review-sync-rev_001').textContent).toContain('QC_NOISY');
    expect(screen.getByTestId('review-versions-rev_001')).toBeDefined();
    expect(screen.getByTestId('review-history-rev_001')).toBeDefined();
  });
});

describe('review studio', () => {
  it('composes queue plus card selection', async () => {
    renderWithProviders(<ReviewStudio projectId="prj_1" />);
    await waitFor(() => {
      expect(screen.getByTestId('review-studio')).toBeDefined();
    });
    await waitFor(() => {
      expect(screen.getByTestId('review-queue-list')).toBeDefined();
    });
    fireEvent.click(screen.getByTestId('review-row-rev_001'));
    await waitFor(() => {
      expect(screen.getByTestId('review-reason-rev_001')).toBeDefined();
    });
  });
});
