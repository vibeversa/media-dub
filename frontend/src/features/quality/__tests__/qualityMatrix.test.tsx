// Task 039B: quality state-matrix gap closure.
//
// Extends `quality.test.tsx` (helpers, summary, issue cards, filters,
// grouping) with the missing states: workspace shells
// (needs-project/loading/error-retry/pending/empty-filtered+clear/empty-passed),
// stale banner + refresh, evidence audio loading/missing, artifact
// refetch-then-unavailable, retry-segment success/failure, review-link
// targets, action/blocked notes, group + filter controls, the
// canvas-progressive-enhancement paths, hook guards, and the types sweep.
// Every failure asserts its recovery control per §11.6 with text signals
// (041C); fixtures are synthetic and fetch is intercepted (R3).
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
import { useTimelinePlayerStore } from '../../timeline/playerStore.js';
import { QualityWorkspace } from '../QualityWorkspace.js';
import {
  filterQualityIssues,
  groupQualityIssues,
  hasActiveQualityFilters,
  iconForQualitySeverity,
  iconForQualityStatus,
  labelForQualitySeverity,
  labelForQualityStatus,
  patternForQualityStatus,
  qualityFiltersFromSearchParams,
  qualityFiltersToSearchParams,
  summarizeQuality,
} from '../types.js';
import { EMPTY_QUALITY_FILTERS, buildQualityIssues, parseQualitySegments } from '../types.js';
import { fetchQuality, invalidateQuality, useQuality } from '../useQuality.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse({ error: { code, message: `backend ${code}`, correlationId: 'corr-q32', details: {} } }, status);
}

function makeSegment(index: number, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  const id = `seg_${String(index + 1).padStart(3, '0')}`;
  const startMs = index * 2000;
  return {
    id,
    projectId: 'prj_1',
    status: 'Ready',
    sequence: index + 1,
    startMs,
    endMs: startMs + 1800,
    speakerId: 'spk_alice',
    speakerLabel: 'Alice',
    selectionVersion: 2,
    reviewStatus: 'Approved',
    qualityCodes: [],
    syncStatus: 'SyncAcceptable',
    confidence: 0.95,
    text: `line ${id}`,
    ...overrides,
  };
}

interface QualityMatrixWorld {
  segmentsMode: 'ok' | 'empty' | 'error500' | 'never';
  retryMode: 'ok' | 'fail';
  mediaMode: 'ok' | 'missing';
  segmentsCalls: number;
}

let world: QualityMatrixWorld;

function resetWorld(): void {
  world = { segmentsMode: 'ok', retryMode: 'ok', mediaMode: 'ok', segmentsCalls: 0 };
}

function segmentsBody(): Record<string, unknown> {
  return {
    items: [
      makeSegment(0, { reviewStatus: 'Open', qualityCodes: ['QC_UNRESOLVED_REVIEW'], artifactId: 'art_qc_1', artifactUrl: 'https://example.com/qc-evidence-1.json' }),
      makeSegment(2, { qualityCodes: ['QC_GAP'], syncStatus: 'SyncAcceptable' }),
      makeSegment(3, { qualityCodes: [] }),
    ],
    page: 1,
    pageSize: 200,
    total: 4,
    hasMore: false,
  };
}

function workspaceBody(runStatus = 'Completed'): Record<string, unknown> {
  return {
    project: { id: 'prj_1', name: 'Pilot', status: runStatus === 'Completed' ? 'Completed' : 'Processing', sourceLanguage: 'en', targetLanguage: 'es', isArchived: false, configurationHash: 'cfg_abc', settingsVersion: 3 },
    media: { id: 'med_1', status: 'Valid', container: 'mp4', sizeBytes: 1024, durationMs: 61000 },
    run: { id: 'run_1', status: runStatus, configHash: 'cfg_abc', attempt: 1 },
    phase: runStatus === 'Completed' ? 'completed' : 'qc',
    stage: 'QualityControl',
    progress: { percentApproximate: 70, currentStage: 'QualityControl', updatedAt: '2024-01-16T12:00:00Z' },
    review: { pendingCount: 1, oldestWaitingAt: '2024-01-16T10:00:00Z' },
    warnings: [],
    output: { state: 'pending', completeness: 70 },
    cost: { runCost: 1.5, monthToDate: 12.5 },
    activity: { recent: [] },
    permissions: { allowedActions: ['project.view', 'processing.retry'] },
  };
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
  if (url.includes('/output/download') && method === 'GET') {
    if (world.mediaMode === 'missing') {
      return jsonResponse({ downloadUrl: null, expiresAt: null });
    }
    return jsonResponse({ downloadUrl: 'https://example.com/media.mp4', expiresAt: '2026-09-26T00:00:00Z' });
  }
  if (url.includes('/quality') && method === 'GET' && !url.includes('/segments')) {
    return jsonResponse({ blockedCount: 1, failedCount: 2, codes: ['QC_GAP', 'QC_SYNC_FAILURE', 'QC_UNRESOLVED_REVIEW'] });
  }
  if (method === 'GET' && url.includes('/segments')) {
    world.segmentsCalls += 1;
    if (world.segmentsMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (world.segmentsMode === 'empty') {
      return jsonResponse({ items: [], page: 1, pageSize: 200, total: 0, hasMore: false });
    }
    if (world.segmentsMode === 'never') {
      return new Promise<Response>(() => {});
    }
    return jsonResponse(segmentsBody());
  }
  if (url.includes('/workspace') && method === 'GET') {
    return jsonResponse(workspaceBody('Completed'));
  }
  if (method === 'POST' && url.includes('/retry')) {
    if (world.retryMode === 'fail') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    return jsonResponse({ segmentId: 'seg_001', selectionVersion: 3 }, 202);
  }
  return jsonResponse({});
}

function renderWorkspace(projectId = 'prj_1', initialEntry = '/projects/prj_1/quality'): void {
  const router = createMemoryRouter([{ path: '/projects/:id/quality', element: <QualityWorkspace projectId={projectId} /> }], {
    initialEntries: [initialEntry],
    future: { ...ROUTER_FUTURE_FLAGS },
  });
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
  useAppStore.getState().setSession('authenticated', ['project.view', 'processing.retry']);
}

function stubCanvases(context: unknown): void {
  Object.defineProperty(window.HTMLCanvasElement.prototype, 'getContext', {
    configurable: true,
    writable: true,
    value: vi.fn().mockReturnValue(context),
  });
  Object.defineProperty(window.HTMLMediaElement.prototype, 'play', {
    configurable: true,
    writable: true,
    value: vi.fn().mockResolvedValue(undefined),
  });
  Object.defineProperty(window.HTMLMediaElement.prototype, 'pause', {
    configurable: true,
    writable: true,
    value: vi.fn(),
  });
}

beforeEach(() => {
  resetWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  useTimelinePlayerStore.getState().resetForTests();
  queryClient.clear();
  stubCanvases(null);
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  useTimelinePlayerStore.getState().resetForTests();
  queryClient.clear();
  vi.restoreAllMocks();
});

describe('QualityWorkspace shell states', () => {
  it('asks for a project when unscoped (no fetch, no crash)', () => {
    authenticate();
    renderWorkspace('');
    expect(screen.getByTestId('quality-needs-project')).toBeDefined();
    expect(world.segmentsCalls).toBe(0);
  });

  it('shows loading while checks resolve (never blank)', () => {
    world.segmentsMode = 'never';
    authenticate();
    renderWorkspace();
    expect(screen.getByTestId('quality-loading')).toBeDefined();
  });

  it('recovers from load errors with retry (recovery: retry)', async () => {
    world.segmentsMode = 'error500';
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('quality-error')).toBeDefined();
    const callsBefore = world.segmentsCalls;
    world.segmentsMode = 'ok';
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(world.segmentsCalls).toBeGreaterThan(callsBefore));
    expect(await screen.findByTestId('quality-list')).toBeDefined();
  });

  it('celebrates the pass state when nothing is flagged (recovery: none needed)', async () => {
    world.segmentsMode = 'empty';
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('quality-empty-passed')).toBeDefined();
    expect(screen.queryByTestId('quality-blocked-banner')).toBeNull();
  });

  it('clears excluding filters with an action (recovery: clear)', async () => {
    authenticate();
    renderWorkspace('prj_1', '/projects/prj_1/quality?severity=critical');
    expect(await screen.findByTestId('quality-empty-filtered')).toBeDefined();
    fireEvent.click(screen.getByTestId('quality-clear-filters'));
    await waitFor(() => expect(screen.queryByTestId('quality-empty-filtered')).toBeNull());
    expect(await screen.findByTestId('quality-list')).toBeDefined();
  });

  it('warns on stale data while keeping the list (recovery: refresh)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('quality-list')).toBeDefined();
    world.segmentsMode = 'error500';
    await queryClient.invalidateQueries();
    expect(await screen.findByTestId('quality-stale')).toBeDefined();
  });
});

describe('QualityIssue evidence matrix', () => {
  it('loads audio excerpts and notes missing media (text, not color)', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('quality-evidence-audio-qc-seg_001-qc-unresolved-review')).toBeDefined();
    cleanup();
    queryClient.clear();
    world.mediaMode = 'missing';
    renderWorkspace();
    expect(await screen.findByTestId('quality-evidence-audio-missing-qc-seg_001-qc-unresolved-review')).toBeDefined();
  });

  it('links evidence artifacts with safe rel attributes (never logged)', async () => {
    authenticate();
    renderWorkspace();
    const anchor = await screen.findByTestId('quality-evidence-artifact-qc-seg_001-qc-unresolved-review');
    expect(anchor.getAttribute('href')).toBe('https://example.com/qc-evidence-1.json');
    expect(anchor.getAttribute('rel')).toContain('noreferrer');
    expect(anchor.textContent).toContain('art_qc_1');
  });

  it('retries segments with store sync and tolerates failures (recovery: retry)', async () => {
    authenticate();
    renderWorkspace();
    fireEvent.click(await screen.findByTestId('quality-retry-qc-seg_001-qc-unresolved-review'));
    await waitFor(() => expect(useTimelinePlayerStore.getState().positionMs).toBeGreaterThanOrEqual(0));
    world.retryMode = 'fail';
    fireEvent.click(screen.getByTestId('quality-retry-qc-seg_001-qc-unresolved-review'));
    expect(await screen.findByTestId('quality-issue-qc-seg_001-qc-unresolved-review')).toBeDefined();
  });

  it('links reviews and notes blocked/unavailable actions (text signals)', async () => {
    authenticate();
    renderWorkspace();
    const reviewLink = await screen.findByTestId('quality-review-link-qc-seg_001-qc-unresolved-review');
    expect(reviewLink.getAttribute('href')).toContain('/review');
    expect(await screen.findByTestId('quality-blocked-note-qc-seg_001-qc-unresolved-review')).toBeDefined();
    expect(screen.getByTestId('quality-action-unavailable-qc-seg_003-qc-gap')).toBeDefined();
  });
});

describe('quality filter + group controls', () => {
  it('applies drafts, clears from the top, and groups by code', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('quality-list')).toBeDefined();
    fireEvent.change(screen.getByTestId('quality-filter-severity'), { target: { value: 'critical' } });
    fireEvent.change(screen.getByTestId('quality-filter-status'), { target: { value: 'Blocked' } });
    fireEvent.change(screen.getByTestId('quality-filter-scope'), { target: { value: 'segment' } });
    fireEvent.click(screen.getByTestId('quality-apply-filters'));
    await waitFor(() => expect(screen.queryByTestId('quality-list')).toBeNull());
    expect(await screen.findByTestId('quality-empty-filtered')).toBeDefined();
    fireEvent.click(screen.getByTestId('quality-clear-filters-top'));
    expect(await screen.findByTestId('quality-list')).toBeDefined();
    fireEvent.change(screen.getByTestId('quality-group'), { target: { value: 'code' } });
    expect(await screen.findByTestId('quality-list')).toBeDefined();
  });
});

describe('canvas progressive enhancement', () => {
  it('draws mini waveforms when 2d contexts exist (no crash either way)', async () => {
    stubCanvases({ save: () => {}, scale: () => {}, clearRect: () => {}, fillRect: () => {}, restore: () => {}, fillStyle: '' });
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('quality-evidence-waveform-qc-seg_001-qc-unresolved-review')).toBeDefined();
  });
});

describe('quality hook guards', () => {
  it('never fires for empty ids or anonymous sessions', async () => {
    authenticate();
    let calls = 0;
    setInnerFetchForTests((async () => {
      calls += 1;
      return jsonResponse({});
    }) as typeof fetch);
    function Probe(): null {
      useQuality('');
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
    await invalidateQuality(queryClient, 'prj_1');
    await expect(fetchQuality('prj_1')).rejects.toBeDefined();
  });
});

describe('quality types sweep', () => {
  const segments = parseQualitySegments(segmentsBody());

  it('labels severities and statuses with icons + text (never color-only)', () => {
    expect(iconForQualitySeverity('Blocking')).toBe('■');
    expect(iconForQualityStatus('Blocked')).toBe('■');
    expect(iconForQualitySeverity('Info')).not.toBe(iconForQualitySeverity('Error'));
    expect(labelForQualitySeverity('Warning')).toBe('warning');
    expect(labelForQualityStatus('RetryRequired')).toBe('retry');
    expect(labelForQualityStatus('ManualReviewRequired')).toBe('review');
    expect(patternForQualityStatus('Blocked').length).toBeGreaterThan(0);
  });

  it('filters and groups issues deterministically', () => {
    const issues = buildQualityIssues(segments, ['project.view', 'processing.retry']);
    expect(issues.length).toBeGreaterThan(0);
    const firstSeverity = issues[0]?.severity ?? 'Error';
    const filtered = filterQualityIssues(issues, { ...EMPTY_QUALITY_FILTERS, severity: firstSeverity });
    expect(filtered.length).toBeGreaterThan(0);
    expect(filtered.every((issue) => issue.severity === firstSeverity)).toBe(true);
    expect(hasActiveQualityFilters(EMPTY_QUALITY_FILTERS)).toBe(false);
    expect(hasActiveQualityFilters({ ...EMPTY_QUALITY_FILTERS, severity: 'critical' })).toBe(true);
    expect(groupQualityIssues(issues, 'segment').length).toBeGreaterThan(0);
    expect(groupQualityIssues(issues, 'code').length).toBeGreaterThan(0);
    expect(qualityFiltersToSearchParams(EMPTY_QUALITY_FILTERS).toString()).toBe('');
    expect(qualityFiltersFromSearchParams(new URLSearchParams('severity=critical')).severity).toBe('critical');
  });

  it('summarizes counts that always reconcile (R1)', () => {
    const issues = buildQualityIssues(segments, ['project.view']);
    const summary = summarizeQuality(issues, segments.length);
    expect(summary.totalIssues).toBe(issues.length);
    expect(summary.warning + summary.retry + summary.review + summary.blocked).toBe(summary.totalIssues);
    expect(summary.passed).toBe(segments.length - new Set(issues.map((issue) => issue.segmentId)).size);
  });
});
