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
import { TranslationWorkspace } from '../TranslationWorkspace.js';
import {
  formatTimestamp,
  isTranslationConflict,
  parseTranslationSegment,
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

function makeSegment(index: number): Record<string, unknown> {
  const id = `seg_${String(index + 1).padStart(3, '0')}`;
  const startMs = index * 2000;
  const speaker = index % 2 === 0 ? 'Alice' : 'Bob';
  const versions =
    index % 3 === 0
      ? [
          { id: `${id}-tr1`, provider: 'acme', model: 'mt-v1', text: `candidate A ${id}`, isSelected: false, score: 0.81, createdAt: '2024-01-15T12:00:00Z' },
          { id: `${id}-tr2`, provider: 'acme', model: 'mt-v2', text: `candidate B ${id}`, isSelected: true, score: 0.92, createdAt: '2024-01-15T13:00:00Z' },
          { id: `${id}-tr3`, provider: 'manual', model: 'manual-review-v1', text: `manual ${id}`, isSelected: false, createdAt: '2024-01-15T14:00:00Z' },
        ]
      : [
          { id: `${id}-tr1`, provider: 'acme', model: 'mt-v1', text: `candidate A ${id}`, isSelected: false, score: 0.81, createdAt: '2024-01-15T12:00:00Z' },
          { id: `${id}-tr2`, provider: 'acme', model: 'mt-v2', text: `candidate B ${id}`, isSelected: true, score: 0.92, createdAt: '2024-01-15T13:00:00Z' },
        ];
  const base: Record<string, unknown> = {
    id,
    projectId: 'prj_1',
    status: 'Ready',
    sequence: index + 1,
    startMs,
    endMs: startMs + 1800,
    speakerId: index % 2 === 0 ? 'spk_alice' : 'spk_bob',
    speakerLabel: speaker,
    selectionVersion: 2,
    reviewStatus: 'Approved',
    qualityCodes: [],
    syncStatus: 'SyncAcceptable',
    transcriptVersions: [
      { id: `${id}-t1`, provider: 'acme', model: 'stt-v1', text: `source line ${id}`, isSelected: false, createdAt: '2024-01-15T12:00:00Z' },
      { id: `${id}-t2`, provider: 'acme', model: 'stt-v2', text: `selected source ${id}`, isSelected: true, createdAt: '2024-01-15T13:00:00Z' },
    ],
    translationVersions: versions,
    selectedTranslationVersionId: `${id}-tr2`,
    selectedTranscriptVersionId: `${id}-t2`,
  };
  if (index === 0) {
    base['glossaryHits'] = [{ term: 'Pilot', definition: 'Project codename' }];
    base['assignedVoice'] = { voiceId: 'voice_1', label: 'Voice One' };
  } else if (index === 1) {
    base['glossaryHits'] = [{ term: 'candidate' }];
  }
  return base;
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

interface TranslationWorld {
  count: number;
  empty: boolean;
  selectBehavior: MutationBehavior;
  manualBehavior: MutationBehavior;
  selectBodies: unknown[];
  manualBodies: unknown[];
  methods: string[];
  listCalls: number;
}

function newWorld(overrides: Partial<TranslationWorld> = {}): TranslationWorld {
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

let world: TranslationWorld = newWorld();

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
  if (url.includes('/segments/') && url.includes('/translation-selection') && method === 'POST') {
    world.methods.push('POST selection');
    world.selectBodies.push(bodyOf(init));
    if (world.selectBehavior === 'conflict') {
      return errorEnvelope('SELECTION_CONFLICT', 409, {
        currentSelectionVersion: 9,
        currentVersionIds: { transcriptVersionId: 'v-current', translationVersionId: 'v-current-tr' },
      });
    }
    return jsonResponse({ segmentId: 'seg_x', selectionVersion: 3, selectedVersionIds: ['v2'], newVersionId: null, outputStale: false });
  }
  if (url.includes('/segments/') && url.includes('/translation-edits') && method === 'POST') {
    world.methods.push('POST edits');
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

beforeEach(() => {
  world = newWorld();
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

describe('pure translation helpers', () => {
  it('formats timestamps ms-accurately', () => {
    expect(formatTimestamp(0)).toBe('00:00.000');
    expect(formatTimestamp(61_250)).toBe('01:01.250');
  });

  it('parses source as the selected transcript version with a label', () => {
    const parsed = parseTranslationSegment(makeSegment(0));
    expect(parsed?.sourceText).toContain('selected source');
    expect(parsed?.sourceVersionLabel).toMatch(/transcript v\d+/);
    expect(parsed?.selectedText).toContain('candidate B');
    expect(parsed?.selectionVersion).toBe(2);
  });

  it('detects translation conflicts by code or status', () => {
    expect(isTranslationConflict({ code: 'SELECTION_CONFLICT', status: 409 })).toBe(true);
    expect(isTranslationConflict({ code: 'SELECTION_CONFLICT' })).toBe(true);
    expect(isTranslationConflict({ status: 409 })).toBe(true);
    expect(isTranslationConflict({ code: 'VALIDATION_FAILED', status: 400 })).toBe(false);
    expect(isTranslationConflict(undefined)).toBe(false);
  });

  it('computes window duration and reading-speed hints read-only', () => {
    const parsed = parseTranslationSegment(makeSegment(0))!;
    expect(windowDurationMs(parsed)).toBe(1800);
    expect(readingSpeedHint('hello', 1000)).toContain('5 chars');
    expect(syncToneFor(parsed, parsed.selectedText, 1800)).toBe('in-window');
    expect(syncToneFor(parsed, 'x'.repeat(500), 1000)).toBe('overflow');
  });

  it('splits glossary runs without crashing on missing definitions', () => {
    const parsed = parseTranslationSegment(makeSegment(1))!;
    const runs = splitGlossaryRuns('candidate A here', parsed.glossaryHits);
    expect(runs.some((r) => r.term !== undefined)).toBe(true);
    const missing = splitGlossaryRuns('plain text', []);
    expect(missing).toHaveLength(1);
  });
});

describe('TranslationWorkspace side-by-side (R4/R5)', () => {
  it('renders source, selected, and alternatives side-by-side with header', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-workspace')).toBeDefined();
    expect(await screen.findByTestId('translation-side-source')).toBeDefined();
    expect(await screen.findByTestId('translation-side-selected')).toBeDefined();
    expect(await screen.findByTestId('translation-side-alternatives')).toBeDefined();
    expect(screen.getByTestId('translation-source').textContent).toContain('selected source');
    expect(screen.getByTestId('translation-source-version').textContent).toMatch(/transcript v\d+/);
    expect(screen.getByTestId('translation-selected').textContent).toContain('candidate B');
    expect(screen.getByTestId('translation-speaker').textContent).toBe('Alice');
    expect(screen.getByTestId('translation-time').textContent).toContain('00:00');
    expect(screen.getByTestId('translation-duration').textContent).toContain('1800 ms');
    expect(screen.getByTestId('translation-sync')).toBeDefined();
    expect(screen.getByTestId('translation-glossary')).toBeDefined();
    expect(screen.getByTestId('translation-voice').textContent).toBe('Voice One');
  });

  it('shows EmptyState with a progress link when translations are pending', async () => {
    world.empty = true;
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-empty')).toBeDefined();
    expect(screen.getByTestId('translation-empty-progress-link')).toBeDefined();
    expect(screen.queryByTestId('translation-editor')).toBeNull();
  });

  it('renders missing voice/glossary as an em dash with a tooltip, never blank', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.click(screen.getByTestId('translation-select-seg_004'));
    await waitFor(() => {
      expect(screen.getByTestId('translation-voice').textContent).toBe('—');
    });
    expect(screen.getByTestId('translation-voice').getAttribute('title')).toContain('No voice');
    expect(screen.getByTestId('translation-glossary').textContent).toBe('—');
  });
});

describe('candidate immutability (R1)', () => {
  it('renders candidates without editable inputs', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-candidate-seg_001-tr1')).toBeDefined();
    const candidates = screen.getAllByTestId(/^translation-candidate-seg_/);
    expect(candidates.length).toBeGreaterThan(0);
    for (const candidate of candidates) {
      expect(within(candidate).queryByRole('textbox')).toBeNull();
      expect(candidate.querySelector('input')).toBeNull();
      expect(candidate.querySelector('textarea')).toBeNull();
    }
    expect(screen.getByTestId('translation-candidate-meta-seg_001-tr1').textContent).toMatch(/acme/);
    expect(screen.getByTestId('translation-candidate-score-seg_001-tr1').textContent).toContain('score');
  });

  it('never issues PUT or PATCH for version writes', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-draft')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: 'immutable history' } });
    fireEvent.click(screen.getByTestId('translation-draft-save'));
    await waitFor(() => {
      expect(world.manualBodies.length).toBeGreaterThan(0);
    });
    const forbidden = world.methods.filter((method) => method.startsWith('PUT') || method.startsWith('PATCH'));
    expect(forbidden).toEqual([]);
    expect(world.methods).toContain('POST edits');
  });
});

describe('select flow (R2)', () => {
  it('sends expectedVersion and invalidates translations on success', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    const callsBefore = world.listCalls;
    fireEvent.click(screen.getByTestId('translation-select-version-seg_001-tr1'));
    await waitFor(() => {
      expect(world.selectBodies.length).toBeGreaterThan(0);
    });
    const body = world.selectBodies[0] as Record<string, unknown>;
    expect(body['expectedVersion']).toBe(2);
    expect(body['selectedVersionIds']).toEqual(['seg_001-tr1']);
    await waitFor(() => {
      expect(world.listCalls).toBeGreaterThan(callsBefore);
    });
    expect(screen.queryByTestId('translation-stale-banner')).toBeNull();
  });

  it('shows the stale banner, refetches, and keeps the draft on 409', async () => {
    world.selectBehavior = 'conflict';
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_001')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: 'keep me' } });
    fireEvent.click(screen.getByTestId('translation-select-version-seg_001-tr1'));
    expect(await screen.findByTestId('translation-stale-banner')).toBeDefined();
    expect((screen.getByTestId('translation-draft') as HTMLTextAreaElement).value).toBe('keep me');
    const callsBefore = world.listCalls;
    fireEvent.click(screen.getByTestId('translation-stale-refresh'));
    await waitFor(() => {
      expect(world.listCalls).toBeGreaterThan(callsBefore);
    });
    expect(screen.queryByTestId('translation-stale-banner')).toBeNull();
  });
});

describe('manual-version flow (rollback)', () => {
  it('creates a manual version with expectedVersion and clears the draft', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-draft')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: 'corrected translation' } });
    const callsBefore = world.listCalls;
    fireEvent.click(screen.getByTestId('translation-draft-save'));
    await waitFor(() => {
      expect(world.manualBodies.length).toBeGreaterThan(0);
    });
    const body = world.manualBodies[0] as Record<string, unknown>;
    expect(body['expectedVersion']).toBe(2);
    expect(body['text']).toBe('corrected translation');
    await waitFor(() => {
      expect((screen.getByTestId('translation-draft') as HTMLTextAreaElement).value).toBe('');
    });
    await waitFor(() => {
      expect(world.listCalls).toBeGreaterThan(callsBefore);
    });
  });

  it('rolls back to the preserved draft on 409 without auto-resubmitting', async () => {
    world.manualBehavior = 'conflict';
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-draft')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: 'do not lose me' } });
    fireEvent.click(screen.getByTestId('translation-draft-save'));
    expect(await screen.findByTestId('translation-stale-banner')).toBeDefined();
    expect((screen.getByTestId('translation-draft') as HTMLTextAreaElement).value).toBe('do not lose me');
    expect(world.manualBodies).toHaveLength(1);
  });
});

describe('dirty-guard dialog paths (R3)', () => {
  it('blocks segment change with save/discard/cancel', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_002')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: 'dirty draft' } });
    fireEvent.click(screen.getByTestId('translation-select-seg_002'));
    expect(await screen.findByTestId('translation-dirty-dialog')).toBeDefined();
    // Cancel stays on the current segment with the draft kept.
    fireEvent.click(screen.getByTestId('translation-dirty-cancel'));
    await waitFor(() => {
      expect(screen.queryByTestId('translation-dirty-dialog')).toBeNull();
    });
    expect(screen.getByTestId('translation-row-seg_001').getAttribute('data-selected')).toBe('true');
    expect((screen.getByTestId('translation-draft') as HTMLTextAreaElement).value).toBe('dirty draft');
    // Discard resets the draft and follows the pending segment change.
    fireEvent.click(screen.getByTestId('translation-select-seg_002'));
    expect(await screen.findByTestId('translation-dirty-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('translation-dirty-discard'));
    await waitFor(() => {
      expect(screen.getByTestId('translation-row-seg_002').getAttribute('data-selected')).toBe('true');
    });
    expect((screen.getByTestId('translation-draft') as HTMLTextAreaElement).value).toBe('');
  });

  it('saves through the dialog then follows the pending navigation', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-row-seg_002')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: 'save me first' } });
    fireEvent.click(screen.getByTestId('translation-select-seg_002'));
    expect(await screen.findByTestId('translation-dirty-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('translation-dirty-save'));
    await waitFor(() => {
      expect(world.manualBodies.length).toBeGreaterThan(0);
    });
    await waitFor(() => {
      expect(screen.getByTestId('translation-row-seg_002').getAttribute('data-selected')).toBe('true');
    });
  });

  it('blocks the leave link until the draft resolves', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('translation-back-link')).toBeDefined();
    fireEvent.change(screen.getByTestId('translation-draft'), { target: { value: 'blocking draft' } });
    fireEvent.click(screen.getByTestId('translation-back-link'));
    expect(await screen.findByTestId('translation-dirty-dialog')).toBeDefined();
    expect(screen.queryByTestId('project-overview')).toBeNull();
    fireEvent.click(screen.getByTestId('translation-dirty-discard'));
    expect(await screen.findByTestId('project-overview')).toBeDefined();
  });
});
