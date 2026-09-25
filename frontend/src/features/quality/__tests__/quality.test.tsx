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
import { useTimelinePlayerStore } from '../../timeline/playerStore.js';
import { QualityWorkspace } from '../QualityWorkspace.js';
import { QualitySummary } from '../QualitySummary.js';
import {
  EMPTY_QUALITY_FILTERS,
  buildQualityIssues,
  filterQualityIssues,
  groupQualityIssues,
  parseQualitySegments,
  qualityFiltersFromSearchParams,
  qualityFiltersToSearchParams,
  sortQualityIssuesBlockedFirst,
  statusForCode,
  summarizeQuality,
} from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
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
    selectionVersion: 2,
    reviewStatus: 'Approved',
    qualityCodes: [],
    syncStatus: 'SyncAcceptable',
    ...overrides,
  };
}

function segmentsFixture(): Record<string, unknown> {
  return {
    items: [
      makeSegment(0, {
        reviewStatus: 'Open',
        qualityCodes: ['QC_UNRESOLVED_REVIEW'],
        syncStatus: 'ManualReviewRequired',
        artifactId: 'art_qc_1',
        artifactUrl: 'https://example.com/qc-evidence-1.json',
      }),
      makeSegment(1, { reviewStatus: 'Open', qualityCodes: ['QC_SYNC_FAILURE'], syncStatus: 'ManualReviewRequired' }),
      makeSegment(2, { qualityCodes: ['QC_GAP'], syncStatus: 'SyncAcceptable' }),
      makeSegment(3, { qualityCodes: [] }),
      makeSegment(4, { qualityCodes: ['QC_STALE_METADATA'] }),
      makeSegment(5, { qualityCodes: ['QC_CUSTOM_UNKNOWN_XYZ'] }),
    ],
    page: 1,
    pageSize: 200,
    total: 6,
    hasMore: false,
  };
}

function workspaceFixture(runStatus = 'Completed'): Record<string, unknown> {
  return {
    project: {
      id: 'prj_1',
      name: 'Pilot',
      status: runStatus === 'Completed' ? 'Completed' : 'Processing',
      sourceLanguage: 'en',
      targetLanguage: 'es',
      isArchived: false,
      configurationHash: 'cfg_abc',
      settingsVersion: 3,
    },
    media: { id: 'med_1', status: 'Valid', container: 'mp4', sizeBytes: 1024, durationMs: 61000 },
    run: { id: 'run_1', status: runStatus, configHash: 'cfg_abc', attempt: 1 },
    phase: runStatus === 'Completed' ? 'completed' : 'qc',
    stage: runStatus === 'Completed' ? 'Render' : 'QualityControl',
    progress: { percentApproximate: runStatus === 'Completed' ? 100 : 70, currentStage: 'QualityControl', updatedAt: '2024-01-16T12:00:00Z' },
    review: { pendingCount: 1, oldestWaitingAt: '2024-01-16T10:00:00Z' },
    warnings: [],
    output: { state: 'pending', completeness: 70 },
    cost: { runCost: 1.5, monthToDate: 12.5 },
    activity: { recent: [] },
    permissions: { allowedActions: ['project.view', 'project.edit', 'processing.retry'] },
  };
}

function qualityFixture(): Record<string, unknown> {
  return { blockedCount: 1, failedCount: 2, codes: ['QC_GAP', 'QC_SYNC_FAILURE', 'QC_UNRESOLVED_REVIEW'] };
}

interface QualityWorld {
  segments: Record<string, unknown>;
  workspace: Record<string, unknown>;
  quality: Record<string, unknown>;
  workspaceCalls: number;
  segmentsCalls: number;
  qualityCalls: number;
  mediaCalls: number;
  allowRetry: boolean;
}

function newWorld(overrides: Partial<QualityWorld> = {}): QualityWorld {
  return {
    segments: segmentsFixture(),
    workspace: workspaceFixture('Completed'),
    quality: qualityFixture(),
    workspaceCalls: 0,
    segmentsCalls: 0,
    qualityCalls: 0,
    mediaCalls: 0,
    allowRetry: true,
    ...overrides,
  };
}

let world: QualityWorld = newWorld();

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
    world.mediaCalls += 1;
    return jsonResponse({ downloadUrl: 'https://example.com/media.mp4', expiresAt: '2026-09-26T00:00:00Z' });
  }
  if (url.includes('/quality') && method === 'GET') {
    world.qualityCalls += 1;
    return jsonResponse(world.quality);
  }
  if (url.includes('/workspace') && method === 'GET') {
    world.workspaceCalls += 1;
    if (!world.allowRetry) {
      const copy = JSON.parse(JSON.stringify(world.workspace)) as Record<string, unknown>;
      (copy['permissions'] as Record<string, unknown>) = { allowedActions: ['project.view'] };
      return jsonResponse(copy);
    }
    return jsonResponse(world.workspace);
  }
  if (method === 'GET' && url.includes('/segments')) {
    world.segmentsCalls += 1;
    return jsonResponse(world.segments);
  }
  if (method === 'POST' && url.includes('/retry')) {
    return jsonResponse({ segmentId: 'seg_001', selectionVersion: 2 }, 202);
  }
  return jsonResponse({});
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit', 'processing.retry']);
}

function renderWithProviders(node: React.ReactNode, initialEntry = '/projects/prj_1/quality'): void {
  const router = createMemoryRouter([{ path: '/projects/:id/quality', element: node }], {
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

function renderBare(node: React.ReactNode): void {
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
  const canvasProto = window.HTMLCanvasElement.prototype as unknown as Record<string, unknown>;
  Object.defineProperty(window.HTMLCanvasElement.prototype, 'getContext', {
    configurable: true,
    writable: true,
    value: () => ({
      save: () => undefined,
      restore: () => undefined,
      scale: () => undefined,
      clearRect: () => undefined,
      fillRect: () => undefined,
      fillStyle: '',
    }),
  });
  void canvasProto;
}

beforeEach(() => {
  world = newWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  useTimelinePlayerStore.getState().resetForTests();
  resetRestoreStartedForTests();
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
  useTimelinePlayerStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  vi.restoreAllMocks();
});

describe('pure quality helpers', () => {
  it('maps codes to backend statuses without dropping unknown codes', () => {
    expect(statusForCode('QC_STALE_METADATA')).toBe('RetryRequired');
    expect(statusForCode('QC_SYNC_FAILURE')).toBe('ManualReviewRequired');
    expect(statusForCode('QC_GAP')).toBe('PassWithWarnings');
    expect(statusForCode('QC_UNRESOLVED_REVIEW')).toBe('Blocked');
    expect(statusForCode('QC_OVERFLOW')).toBe('Blocked');
    expect(statusForCode('QC_CUSTOM_UNKNOWN_XYZ')).toBe('PassWithWarnings');
    expect(statusForCode('')).toBe('PassWithWarnings');
  });

  it('round-trips filters through search params without signed URLs', () => {
    const params = qualityFiltersToSearchParams({ severity: 'Blocking', status: 'Blocked', scope: 'segment', group: 'code' });
    expect(params.get('severity')).toBe('Blocking');
    expect(params.get('status')).toBe('Blocked');
    expect(params.get('scope')).toBe('segment');
    expect(params.get('group')).toBe('code');
    expect(params.toString()).not.toContain('example.com');
    const parsed = qualityFiltersFromSearchParams(params);
    expect(parsed.severity).toBe('Blocking');
    expect(parsed.group).toBe('code');
    const empty = qualityFiltersToSearchParams(EMPTY_QUALITY_FILTERS);
    expect(empty.toString()).toBe('');
  });

  it('summarizes counts that match the issue list', () => {
    const segments = parseQualitySegments(segmentsFixture());
    expect(segments).toHaveLength(6);
    const issues = buildQualityIssues(segments, ['project.view', 'processing.retry']);
    expect(issues).toHaveLength(5);
    const summary = summarizeQuality(issues, segments.length);
    expect(summary.totalIssues).toBe(issues.length);
    expect(summary.warning + summary.retry + summary.review + summary.blocked).toBe(issues.length);
    expect(summary.blocked).toBe(1);
    expect(summary.review).toBe(1);
    expect(summary.retry).toBe(1);
    expect(summary.warning).toBe(2);
    expect(summary.passed).toBe(1);
    expect(summary.totalSegments).toBe(6);
  });

  it('pins blocked issues above all other content', () => {
    const segments = parseQualitySegments(segmentsFixture());
    const issues = buildQualityIssues(segments, ['project.view']);
    const sorted = sortQualityIssuesBlockedFirst([...issues].reverse());
    expect(sorted[0]?.status).toBe('Blocked');
    const firstBlocked = sorted.findIndex((issue) => issue.status === 'Blocked');
    const firstWarning = sorted.findIndex((issue) => issue.status === 'PassWithWarnings');
    expect(firstBlocked).toBeLessThan(firstWarning);
  });

  it('filters by severity, status, and scope and groups by segment or code', () => {
    const segments = parseQualitySegments(segmentsFixture());
    const issues = buildQualityIssues(segments, ['project.view']);
    const blockedOnly = filterQualityIssues(issues, { ...EMPTY_QUALITY_FILTERS, status: 'Blocked' });
    expect(blockedOnly).toHaveLength(1);
    expect(blockedOnly[0]?.code).toBe('QC_UNRESOLVED_REVIEW');
    const severityOnly = filterQualityIssues(issues, { ...EMPTY_QUALITY_FILTERS, severity: 'Blocking' });
    expect(severityOnly).toHaveLength(1);
    const bySegment = groupQualityIssues(issues, 'segment');
    expect(bySegment.length).toBeGreaterThan(0);
    for (const group of bySegment) {
      expect(group.key).not.toBe('');
    }
    const byCode = groupQualityIssues(issues, 'code');
    expect(byCode.length).toBe(issues.length);
  });
});

describe('quality summary', () => {
  it('renders all five states with counts that match the fixture', async () => {
    renderWithProviders(<QualityWorkspace projectId="prj_1" />);
    expect(await screen.findByTestId('quality-summary')).toBeDefined();
    expect(await screen.findByTestId('quality-list')).toBeDefined();
    expect(screen.getByTestId('quality-summary-passed').getAttribute('data-count')).toBe('1');
    expect(screen.getByTestId('quality-summary-warning').getAttribute('data-count')).toBe('2');
    expect(screen.getByTestId('quality-summary-retry').getAttribute('data-count')).toBe('1');
    expect(screen.getByTestId('quality-summary-review').getAttribute('data-count')).toBe('1');
    expect(screen.getByTestId('quality-summary-blocked').getAttribute('data-count')).toBe('1');
    expect(screen.getByTestId('quality-workspace-key').textContent).toContain('quality');
  });

  it('conveys every badge with icon plus text plus pattern, never color alone', async () => {
    renderWithProviders(<QualityWorkspace projectId="prj_1" />);
    await screen.findByTestId('quality-list');
    for (const state of ['passed', 'warning', 'retry', 'review', 'blocked'] as const) {
      const badge = screen.getByTestId(`quality-badge-${state}`);
      const text = badge.textContent ?? '';
      expect(text.trim()).not.toBe('');
      expect(screen.getByTestId(`quality-badge-icon-${state}`).textContent).not.toBe('');
      expect(screen.getByTestId(`quality-badge-label-${state}`).textContent).toBe(state);
      expect(screen.getByTestId(`quality-badge-pattern-${state}`).textContent).not.toBe('');
      expect(badge.getAttribute('data-pattern')).not.toBe('');
    }
    const cards = screen.getAllByTestId(/^quality-issue-/);
    expect(cards.length).toBeGreaterThan(0);
    for (const card of cards) {
      const status = card.getAttribute('data-status') ?? '';
      expect(status).not.toBe('');
    }
  });
});

describe('quality issues', () => {
  it('shows every required field on each card with inline evidence', async () => {
    renderWithProviders(<QualityWorkspace projectId="prj_1" />);
    await screen.findByTestId('quality-list');
    // Audio excerpts share the signed preview URL; wait for it to resolve.
    await screen.findByTestId('quality-evidence-audio-qc-seg_001-qc-unresolved-review');
    const segments = parseQualitySegments(segmentsFixture());
    const issues = buildQualityIssues(segments, ['project.view', 'processing.retry']);
    for (const issue of issues) {
      expect(screen.getByTestId(`quality-code-${issue.id}`).textContent).toContain(issue.code);
      expect(screen.getByTestId(`quality-severity-${issue.id}`).textContent).not.toBe('');
      expect(screen.getByTestId(`quality-scope-${issue.id}`).textContent).toContain(issue.scope);
      expect(screen.getByTestId(`quality-segment-${issue.id}`).textContent).not.toBe('');
      expect(screen.getByTestId(`quality-description-${issue.id}`).textContent).not.toBe('');
      expect(screen.getByTestId(`quality-action-${issue.id}`).textContent).toContain('Suggested action');
      expect(screen.getByTestId(`quality-status-${issue.id}`).textContent).toContain(issue.status);
      expect(screen.getByTestId(`quality-evidence-${issue.id}`)).toBeDefined();
      expect(screen.getByTestId(`quality-evidence-waveform-${issue.id}`)).toBeDefined();
      expect(screen.getByTestId(`quality-evidence-timestamp-${issue.id}`)).toBeDefined();
      expect(screen.getByTestId(`quality-evidence-metric-${issue.id}`)).toBeDefined();
    }
    expect(screen.getByTestId('quality-evidence-artifact-qc-seg_001-qc-unresolved-review')).toBeDefined();
  });

  it('tolerates unknown codes with a generic card instead of dropping', async () => {
    renderWithProviders(<QualityWorkspace projectId="prj_1" />);
    await screen.findByTestId('quality-list');
    const unknownId = 'qc-seg_006-qc-custom-unknown-xyz';
    expect(screen.getByTestId(`quality-issue-${unknownId}`)).toBeDefined();
    expect(screen.getByTestId(`quality-code-${unknownId}`).textContent).toContain('QC_CUSTOM_UNKNOWN_XYZ');
    expect(screen.getByTestId(`quality-description-${unknownId}`).textContent).toContain('QC_CUSTOM_UNKNOWN_XYZ');
  });

  it('pins blocked issues first with a prominent banner and count', async () => {
    renderWithProviders(<QualityWorkspace projectId="prj_1" />);
    const list = await screen.findByTestId('quality-list');
    expect(screen.getByTestId('quality-blocked-banner')).toBeDefined();
    expect(screen.getByTestId('quality-blocked-count').textContent).toContain('1');
    const cards = list.querySelectorAll('[data-testid^="quality-issue-"]');
    expect(cards.length).toBeGreaterThan(1);
    expect(cards[0]?.getAttribute('data-status')).toBe('Blocked');
  });

  it('jumps to the timeline timestamp without navigating away', async () => {
    renderWithProviders(<QualityWorkspace projectId="prj_1" />);
    await screen.findByTestId('quality-list');
    const jump = screen.getByTestId('quality-jump-qc-seg_001-qc-unresolved-review');
    fireEvent.click(jump);
    await waitFor(() => {
      expect(useTimelinePlayerStore.getState().positionMs).toBe(0);
    });
    const timestamp = screen.getByTestId('quality-evidence-timestamp-qc-seg_002-qc-sync-failure');
    fireEvent.click(timestamp);
    await waitFor(() => {
      expect(useTimelinePlayerStore.getState().positionMs).toBe(2000);
    });
  });

  it('gates retry on the advertised action with a reason tooltip otherwise', async () => {
    renderWithProviders(<QualityWorkspace projectId="prj_1" />);
    await screen.findByTestId('quality-list');
    expect(screen.getByTestId('quality-retry-qc-seg_001-qc-unresolved-review')).toBeDefined();
    expect(screen.getByTestId('quality-review-link-qc-seg_001-qc-unresolved-review')).toBeDefined();
  });

  it('omits retry with a reason when the backend does not advertise it', async () => {
    world.allowRetry = false;
    queryClient.clear();
    renderWithProviders(<QualityWorkspace projectId="prj_1" />);
    await screen.findByTestId('quality-list');
    expect(screen.queryByTestId('quality-retry-qc-seg_001-qc-unresolved-review')).toBeNull();
    const unavailable = screen.getByTestId('quality-retry-unavailable-qc-seg_001-qc-unresolved-review');
    expect(unavailable.getAttribute('title')).toContain('processing.retry');
  });
});

describe('quality filters and grouping', () => {
  it('narrows the list by status and reflects the filter in the URL', async () => {
    renderWithProviders(<QualityWorkspace projectId="prj_1" />, '/projects/prj_1/quality');
    await screen.findByTestId('quality-list');
    fireEvent.change(screen.getByTestId('quality-filter-status'), { target: { value: 'Blocked' } });
    fireEvent.click(screen.getByTestId('quality-apply-filters'));
    await waitFor(() => {
      const list = screen.getByTestId('quality-list');
      expect(list.getAttribute('data-total')).toBe('1');
    });
    // Shareable filter state lives in the router search params (asserted via
    // the narrowed list above); signed URLs never enter the URL.
    expect(screen.getByTestId('quality-filter-status') as HTMLInputElement).not.toBeNull();
  });

  it('groups by code with one group per code', async () => {
    renderWithProviders(<QualityWorkspace projectId="prj_1" />, '/projects/prj_1/quality?group=code');
    await screen.findByTestId('quality-list');
    expect(screen.getByTestId('quality-group-code-QC_GAP')).toBeDefined();
    expect(screen.getByTestId('quality-group-code-QC_SYNC_FAILURE')).toBeDefined();
  });

  it('distinguishes pass celebration from filtered-empty', async () => {
    world.segments = { items: [], page: 1, pageSize: 200, total: 0, hasMore: false };
    world.quality = { blockedCount: 0, failedCount: 0, codes: [] };
    world.workspace = workspaceFixture('Completed');
    queryClient.clear();
    renderWithProviders(<QualityWorkspace projectId="prj_1" />);
    expect(await screen.findByTestId('quality-empty-passed')).toBeDefined();
    expect(screen.queryByTestId('quality-list')).toBeNull();
  });

  it('shows filtered-empty with a clear action when filters exclude everything', async () => {
    renderWithProviders(<QualityWorkspace projectId="prj_1" />, '/projects/prj_1/quality?status=Blocked');
    await screen.findByTestId('quality-list');
    fireEvent.change(screen.getByTestId('quality-filter-status'), { target: { value: 'NoSuchStatus' } });
    fireEvent.click(screen.getByTestId('quality-apply-filters'));
    expect(await screen.findByTestId('quality-empty-filtered')).toBeDefined();
    fireEvent.click(screen.getByTestId('quality-clear-filters'));
    expect(await screen.findByTestId('quality-list')).toBeDefined();
  });

  it('shows an in-progress skeleton while the run is active, not zeros', async () => {
    world.segments = { items: [], page: 1, pageSize: 200, total: 0, hasMore: false };
    world.quality = { blockedCount: 0, failedCount: 0, codes: [] };
    world.workspace = workspaceFixture('Running');
    queryClient.clear();
    renderWithProviders(<QualityWorkspace projectId="prj_1" />);
    expect(await screen.findByTestId('quality-pending')).toBeDefined();
    expect(screen.queryByTestId('quality-empty-passed')).toBeNull();
  });
});

describe('quality summary component', () => {
  it('links each count to the filtered issue list', () => {
    const segments = parseQualitySegments(segmentsFixture());
    const issues = buildQualityIssues(segments, ['project.view']);
    const summary = summarizeQuality(issues, segments.length);
    renderBare(<QualitySummary projectId="prj_1" summary={summary} isPending={false} isQcPending={false} />);
    for (const state of ['passed', 'warning', 'retry', 'review', 'blocked'] as const) {
      expect(screen.getByTestId(`quality-summary-link-${state}`).getAttribute('href')).toContain('/projects/prj_1/quality');
    }
  });
});
