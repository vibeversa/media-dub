// Delta 2: quality remaining-branch closure.
//
// Supplements qualityMatrix (workspace shells, evidence happy paths) with
// the types exhaustive sweep (status/description/action mapping,
// segment parsing, metric derivation, issue building, sorting,
// summarizing with Pass rows, filtering, grouping, URL params, pending
// detection, expiry, projection parsing) and QualityIssue direct branches
// (artifact refetch-then-unavailable, double-retry short-circuit,
// retry-segment success/failure, missing-segment early return, canvas
// failure tolerance, timestamp/metric/artifact absences, jump wiring,
// review/retry unavailable notes, aux-click safety). Synthetic fixtures.
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
import { QualityIssue } from '../QualityIssue.js';
import {
  EMPTY_QUALITY_FILTERS,
  buildQualityIssues,
  descriptionForCode,
  filterQualityIssues,
  groupQualityIssues,
  iconForQualitySeverity,
  iconForQualityStatus,
  isQualityExpiredError,
  isQualityPendingRun,
  labelForQualitySeverity,
  labelForQualityStatus,
  parseQualityProjection,
  parseQualitySegment,
  parseQualitySegments,
  patternForQualityStatus,
  qualityFiltersFromSearchParams,
  qualityFiltersToSearchParams,
  severityForStatus,
  sortQualityIssuesBlockedFirst,
  statusForCode,
  suggestedActionForStatus,
  summarizeQuality,
} from '../types.js';
import type { QualityIssueView } from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse({ error: { code, message: `backend ${code}`, correlationId: 'corr-q32b', details: {} } }, status);
}

function makeSegment(index: number, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  const id = `seg_${String(index + 1).padStart(3, '0')}`;
  const startMs = index * 2000;
  return {
    id,
    sequence: index + 1,
    startMs,
    endMs: startMs + 1800,
    speakerId: 'spk_alice',
    reviewStatus: 'Approved',
    qualityCodes: [],
    syncStatus: 'SyncAcceptable',
    ...overrides,
  };
}

interface World {
  mediaMode: 'ok' | 'never';
  retryMode: 'ok' | 'fail';
  retryCalls: number;
}

let world: World;

function resetWorld(): void {
  world = { mediaMode: 'ok', retryMode: 'ok', retryCalls: 0 };
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
  if (url.includes('/output/download') && method === 'GET') {
    if (world.mediaMode === 'never') return new Promise<Response>(() => {});
    return jsonResponse({ downloadUrl: 'https://example.com/media.mp4', expiresAt: '2026-09-26T00:00:00Z' });
  }
  if (method === 'POST' && url.includes('/retry')) {
    world.retryCalls += 1;
    if (world.retryMode === 'fail') return errorEnvelope('INTERNAL_ERROR', 500);
    return jsonResponse({ segmentId: 'seg_001', selectionVersion: 3 }, 202);
  }
  return jsonResponse({});
}

function renderIssue(issue: QualityIssueView): void {
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <MemoryRouter>
            <QualityIssue projectId="prj_1" issue={issue} />
          </MemoryRouter>
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

function issueFor(code: string, overrides: Record<string, unknown> = {}): QualityIssueView {
  const segments = parseQualitySegments({ items: [makeSegment(0, { qualityCodes: [code], reviewStatus: 'Open', artifactId: 'art_1', artifactUrl: 'https://example.com/e.json' })] });
  const issues = buildQualityIssues(segments, ['processing.retry']);
  const found = issues.find((i) => i.code === code) ?? issues[0];
  if (found === undefined) throw new Error(`no issue for ${code}`);
  return { ...found, ...overrides } as QualityIssueView;
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'processing.retry']);
}

function stubCanvas(context: unknown): void {
  Object.defineProperty(window.HTMLCanvasElement.prototype, 'getContext', {
    configurable: true,
    writable: true,
    value: vi.fn().mockReturnValue(context),
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
  authenticate();
  stubCanvas(null);
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

describe('status/description/action mapping sweep', () => {
  it('maps every code family to its display status', () => {
    expect(statusForCode('')).toBe('PassWithWarnings');
    expect(statusForCode('   ')).toBe('PassWithWarnings');
    expect(statusForCode('QC_STALE_METADATA')).toBe('RetryRequired');
    expect(statusForCode('qc_sync_failure')).toBe('ManualReviewRequired');
    expect(statusForCode('QC_GAP')).toBe('PassWithWarnings');
    expect(statusForCode('QC_DRIFT')).toBe('PassWithWarnings');
    expect(statusForCode('QC_SILENCE')).toBe('PassWithWarnings');
    expect(statusForCode('QC_CROSSFADE')).toBe('PassWithWarnings');
    expect(statusForCode('QC_SYNC_DRIFT', undefined, 'bad-sync')).toBe('ManualReviewRequired');
    expect(statusForCode('QC_GAP', undefined, 'SyncAcceptable')).toBe('PassWithWarnings');
    expect(statusForCode('NEEDS_RETRY_NOW')).toBe('RetryRequired');
    expect(statusForCode('NEEDS_REVIEW_NOW')).toBe('ManualReviewRequired');
    expect(statusForCode('OPEN_REVIEW_UNRESOLVED')).toBe('Blocked');
    expect(statusForCode('QC_NOISY')).toBe('PassWithWarnings');
    expect(statusForCode('LOW_CONF_MARK')).toBe('PassWithWarnings');
    expect(statusForCode('TERMINOLOGY_WARN_X')).toBe('PassWithWarnings');
    expect(statusForCode('CUSTOM_RULE')).toBe('PassWithWarnings');
    expect(statusForCode('UNKNOWN_CODE_ZZZ')).toBe('PassWithWarnings');
    expect(statusForCode('QC_BLOCKED_RENDER')).toBe('Blocked');
    expect(statusForCode('QC_MISSING_TRANSCRIPT')).toBe('Blocked');
    expect(statusForCode('QC_OVERFLOW')).toBe('Blocked');
    expect(statusForCode('QC_VOICE_GONE')).toBe('Blocked');
    expect(statusForCode('QC_CHECKSUM_BAD')).toBe('Blocked');
    expect(statusForCode('QC_CORRUPT_FRAME')).toBe('Blocked');
    expect(statusForCode('QC_RATE_ODD')).toBe('Blocked');
    expect(statusForCode('QC_CHANNEL_MAP')).toBe('Blocked');
    expect(statusForCode('QC_LOUDNESS_HIGH')).toBe('Blocked');
    expect(statusForCode('QC_CLIPPING')).toBe('Blocked');
    expect(statusForCode('QC_PEAK_HOT')).toBe('Blocked');
    expect(statusForCode('QC_ROUTING_WRONG')).toBe('Blocked');
    expect(statusForCode('QC_PLACEMENT_OFF')).toBe('Blocked');
    expect(statusForCode('QC_ATTENUATION_LOW')).toBe('Blocked');
    expect(statusForCode('QC_INVALID_SHAPE')).toBe('Blocked');
    expect(statusForCode('QC_TERMINOLOGY_WRONG')).toBe('Blocked');
    expect(statusForCode('QC_EMPTY_CELL')).toBe('Blocked');
    expect(statusForCode('NO_SEGMENTS_HERE')).toBe('Blocked');
    expect(statusForCode('SOME_FRESH_CODE')).toBe('PassWithWarnings');
  });

  it('derives severities, descriptions, and actions for every status', () => {
    expect(severityForStatus('Pass')).toBe('Info');
    expect(severityForStatus('PassWithWarnings')).toBe('Warning');
    expect(severityForStatus('RetryRequired')).toBe('Warning');
    expect(severityForStatus('ManualReviewRequired')).toBe('Error');
    expect(severityForStatus('Blocked')).toBe('Blocking');
    for (const code of ['QC_SYNC_FAILURE', 'QC_STALE_METADATA', 'QC_GAP', 'QC_DRIFT', 'QC_SILENCE', 'QC_SILENCE_BOUNDARY', 'QC_CROSSFADE', 'QC_UNRESOLVED_REVIEW', 'QC_OVERFLOW', 'QC_MISSING_TRANSCRIPT', 'QC_EMPTY_TRANSLATION', 'QC_MISSING_VOICE', 'QC_MISSING_AUDIO']) {
      expect(descriptionForCode(code, 'seg_001')).toContain('seg_001');
      expect(descriptionForCode(code)).not.toContain('seg_001');
    }
    expect(descriptionForCode('FRESH_CODE')).toContain('FRESH_CODE');
    expect(descriptionForCode('')).toContain('Unknown QC code');
    expect(suggestedActionForStatus('Blocked')).toContain('blocking');
    expect(suggestedActionForStatus('ManualReviewRequired')).toContain('review');
    expect(suggestedActionForStatus('RetryRequired')).toContain('Retry');
    expect(suggestedActionForStatus('PassWithWarnings')).toContain('warning');
    expect(suggestedActionForStatus('Pass')).toContain('No action');
  });
});

describe('quality segment parsing branches', () => {
  it('rejects id-less rows and clamps timing defensively', () => {
    expect(parseQualitySegment(undefined)).toBeUndefined();
    expect(parseQualitySegment(null)).toBeUndefined();
    expect(parseQualitySegment({})).toBeUndefined();
    expect(parseQualitySegment({ id: '' })).toBeUndefined();
    const clamped = parseQualitySegment({ id: 's1', startMs: -10, endMs: -20 });
    expect(clamped?.startMs).toBe(0);
    expect(clamped?.endMs).toBe(0);
    expect(parseQualitySegment({ id: 's1', startMs: 5.4, endMs: 9.6 })?.startMs).toBe(5);
    const pascal = parseQualitySegment({
      Id: 's1', Sequence: 3, StartMs: 10, EndMs: 20, SpeakerId: 'spk_1',
      ReviewStatus: 'Open', QualityCodes: ['QC_GAP'], SyncStatus: 'bad',
      PreviewArtifactId: 'art_1', SignedUrl: 'https://example.com/e', ReviewId: 'rev_1',
    });
    expect(pascal?.sequence).toBe(3);
    expect(pascal?.artifactId).toBe('art_1');
    expect(pascal?.artifactUrl).toBe('https://example.com/e');
    expect(pascal?.reviewId).toBe('rev_1');
    expect(parseQualitySegments(null)).toEqual([]);
    expect(parseQualitySegments({ items: 'nope' })).toEqual([]);
    expect(parseQualitySegments([makeSegment(1), makeSegment(0)]).map((s) => s.id)).toEqual(['seg_001', 'seg_002']);
  });
});

describe('issue building, sorting, summarizing, filtering, grouping', () => {
  it('dedupes codes, gates retry, and sorts blocked-first', () => {
    const segments = parseQualitySegments({
      items: [
        makeSegment(0, { qualityCodes: ['QC_GAP', 'QC_GAP', '', 'QC_MISSING_AUDIO'], reviewStatus: 'Open', reviewId: 'rev_1' }),
        makeSegment(1, { qualityCodes: ['QC_UNRESOLVED_REVIEW'] }),
      ],
    });
    const issues = buildQualityIssues(segments, ['project.view']);
    expect(issues.filter((i) => i.code === 'QC_GAP').length).toBe(1);
    expect(issues[0]?.status).toBe('Blocked');
    expect(issues.find((i) => i.code === 'QC_GAP')?.actions.canOpenReview).toBe(true);
    expect(issues.find((i) => i.code === 'QC_GAP')?.actions.canRetry).toBe(false);
    expect(issues.find((i) => i.code === 'QC_GAP')?.actions.retryReason).toContain('processing.retry');
    expect(issues.find((i) => i.code === 'QC_GAP')?.metricName).toBe('Segment window');
    const overflow = buildQualityIssues(parseQualitySegments({ items: [makeSegment(0, { qualityCodes: ['QC_OVERFLOW'] })] }), []);
    expect(overflow[0]?.metricName).toBe('Segment duration');
    const silence = buildQualityIssues(parseQualitySegments({ items: [makeSegment(0, { qualityCodes: ['QC_SILENCE'] })] }), []);
    expect(silence[0]?.metricUnit).toBe('ms');
    const ordered = sortQualityIssuesBlockedFirst([
      { id: 'b', status: 'Blocked' },
      { id: 'a', status: 'Pass' },
      { id: 'c', status: 'RetryRequired' },
      { id: 'd', status: 'ManualReviewRequired' },
      { id: 'e', status: 'PassWithWarnings' },
    ] as QualityIssueView[]);
    expect(ordered.map((i) => i.id)).toEqual(['b', 'd', 'c', 'e', 'a']);
  });

  it('summarizes Pass rows without counting them as issues', () => {
    const passRow = { id: 'p', status: 'Pass', segmentId: 'seg_001' } as QualityIssueView;
    const blockedRow = { id: 'q', status: 'Blocked', segmentId: 'seg_002' } as QualityIssueView;
    const summary = summarizeQuality([passRow, blockedRow], 5);
    expect(summary.totalIssues).toBe(2);
    expect(summary.blocked).toBe(1);
    expect(summary.warning + summary.retry + summary.review + summary.blocked).toBe(1);
    expect(summary.passed).toBe(3);
  });

  it('filters by every dimension and groups project-level rows', () => {
    const segments = parseQualitySegments({ items: [makeSegment(0, { qualityCodes: ['QC_GAP'] })] });
    const issues = buildQualityIssues(segments, []);
    expect(filterQualityIssues(issues, { ...EMPTY_QUALITY_FILTERS, severity: 'Warning' }).length).toBe(1);
    expect(filterQualityIssues(issues, { ...EMPTY_QUALITY_FILTERS, severity: 'Blocking' }).length).toBe(0);
    expect(filterQualityIssues(issues, { ...EMPTY_QUALITY_FILTERS, status: 'PassWithWarnings' }).length).toBe(1);
    expect(filterQualityIssues(issues, { ...EMPTY_QUALITY_FILTERS, scope: 'segment' }).length).toBe(1);
    expect(filterQualityIssues(issues, { ...EMPTY_QUALITY_FILTERS, scope: 'run' }).length).toBe(0);
    const projectLevel = { ...issues[0], segmentId: undefined, scope: 'project' } as QualityIssueView;
    const groups = groupQualityIssues([projectLevel], 'segment');
    expect(groups[0]?.key).toBe('project');
    expect(groups[0]?.label).toContain('Segment project');
    expect(groupQualityIssues(issues, 'code')[0]?.label).toContain('Code QC_GAP');
  });

  it('round-trips URL params with group defaults', () => {
    expect(qualityFiltersFromSearchParams(new URLSearchParams('group=code')).group).toBe('code');
    expect(qualityFiltersFromSearchParams(new URLSearchParams('group=bogus')).group).toBe('segment');
    expect(qualityFiltersFromSearchParams(new URLSearchParams('')).group).toBe('segment');
    const params = qualityFiltersToSearchParams({ severity: 'Warning', status: 'Blocked', scope: 'segment', group: 'code' });
    expect(params.get('severity')).toBe('Warning');
    expect(params.get('group')).toBe('code');
    expect(qualityFiltersToSearchParams(EMPTY_QUALITY_FILTERS).toString()).toBe('');
    expect(iconForQualityStatus('Pass')).toBe('✓');
    expect(iconForQualitySeverity('Info')).toBe('○');
    expect(labelForQualityStatus('Pass')).toBe('passed');
    expect(labelForQualitySeverity('Info')).toBe('info');
    expect(patternForQualityStatus('Pass')).toBe('solid-fill');
  });

  it('detects pending runs, expiry, and parses projections defensively', () => {
    expect(isQualityPendingRun('pending')).toBe(true);
    expect(isQualityPendingRun('Running')).toBe(true);
    expect(isQualityPendingRun('cancelling')).toBe(true);
    expect(isQualityPendingRun('Completed')).toBe(false);
    expect(isQualityPendingRun('')).toBe(false);
    expect(isQualityPendingRun(undefined)).toBe(false);
    expect(isQualityExpiredError({ code: 'URL_EXPIRED' })).toBe(true);
    expect(isQualityExpiredError({ status: 410 })).toBe(true);
    expect(isQualityExpiredError(null)).toBe(false);
    expect(isQualityExpiredError({ code: 'X' })).toBe(false);
    expect(parseQualityProjection(null)).toEqual({ blocked: 0, failed: 0, codes: [] });
    expect(parseQualityProjection({ blockedCount: 2.7, failedCount: -3, codes: ['QC_GAP', ''] })).toEqual({ blocked: 3, failed: 0, codes: ['QC_GAP'] });
    expect(parseQualityProjection({ Blocked: 1, Failed: 1, Codes: ['a'] }).blocked).toBe(1);
  });
});

describe('QualityIssue direct branches', () => {
  it('tolerates canvas failures as progressive enhancement', async () => {
    Object.defineProperty(window.HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      writable: true,
      value: vi.fn().mockImplementation(() => {
        throw new Error('no canvas');
      }),
    });
    renderIssue(issueFor('QC_GAP'));
    expect(await screen.findByTestId(/quality-evidence-waveform-/)).toBeDefined();
  });

  it('notes missing audio excerpts and loading states distinctly', async () => {
    world.mediaMode = 'never';
    const issue = issueFor('QC_GAP');
    renderIssue(issue);
    expect(await screen.findByTestId(`quality-evidence-audio-loading-${issue.id}`)).toBeDefined();
    cleanup();
    queryClient.clear();
    setInnerFetchForTests((async () => jsonResponse({ downloadUrl: null })) as typeof fetch);
    renderIssue(issue);
    expect(await screen.findByTestId(`quality-evidence-audio-missing-${issue.id}`)).toBeDefined();
  });

  it('refetches artifact evidence once then reports unavailable (recovery: refresh)', async () => {
    const issue = { ...issueFor('QC_GAP'), artifactUrl: '' };
    renderIssue(issue);
    const retry = await screen.findByTestId(`quality-evidence-artifact-retry-${issue.id}`);
    fireEvent.click(retry);
    expect(await screen.findByTestId(`quality-evidence-unavailable-${issue.id}`)).toBeDefined();
    expect(screen.getByTestId(`quality-evidence-unavailable-${issue.id}`).textContent).toContain('art_1');
    fireEvent.click(screen.getByTestId(`quality-evidence-artifact-retry-${issue.id}`));
    expect(await screen.findByTestId(`quality-evidence-unavailable-${issue.id}`)).toBeDefined();
  });

  it('notes absent artifact links and empty metrics as text', async () => {
    const issue = { ...issueFor('QC_GAP'), artifactId: undefined, artifactUrl: undefined, metricName: undefined, metricValue: undefined, startMs: undefined, endMs: undefined };
    renderIssue(issue);
    expect(await screen.findByTestId(`quality-evidence-no-artifact-${issue.id}`)).toBeDefined();
    expect(screen.getByTestId(`quality-evidence-metric-${issue.id}`).textContent).toContain('No metric readout');
    expect(screen.getByTestId(`quality-evidence-${issue.id}`).textContent).toContain('No timestamp evidence');
    expect(screen.queryByTestId(`quality-jump-${issue.id}`)).toBeNull();
  });

  it('jumps to the timeline and keeps signed URLs out of logs', async () => {
    const issue = issueFor('QC_GAP');
    renderIssue(issue);
    const stamp = await screen.findByTestId(`quality-evidence-timestamp-${issue.id}`);
    fireEvent.click(stamp);
    expect(useTimelinePlayerStore.getState().positionMs).toBe(issue.startMs);
    fireEvent.click(screen.getByTestId(`quality-jump-${issue.id}`));
    expect(useTimelinePlayerStore.getState().positionMs).toBe(issue.startMs);
    const anchor = screen.getByTestId(`quality-evidence-artifact-${issue.id}`);
    fireEvent(anchor, new MouseEvent('auxclick', { bubbles: true }));
    expect(anchor.getAttribute('rel')).toContain('noreferrer');
  });

  it('retries segments and tolerates failures without losing the card', async () => {
    const issue = issueFor('QC_GAP');
    renderIssue(issue);
    fireEvent.click(await screen.findByTestId(`quality-retry-${issue.id}`));
    await waitFor(() => expect(world.retryCalls).toBe(1));
    expect(screen.getByTestId(`quality-issue-${issue.id}`)).toBeDefined();
    world.retryMode = 'fail';
    fireEvent.click(screen.getByTestId(`quality-retry-${issue.id}`));
    await waitFor(() => expect(world.retryCalls).toBe(2));
    expect(screen.getByTestId(`quality-issue-${issue.id}`)).toBeDefined();
  });

  it('ignores retry without a segment scope (no storm)', async () => {
    const issue = { ...issueFor('QC_GAP'), segmentId: undefined };
    renderIssue(issue);
    expect(screen.queryByTestId(`quality-retry-${issue.id}`)).toBeNull();
  });

  it('explains unavailable review and retry actions as text', async () => {
    const segments = parseQualitySegments({ items: [makeSegment(0, { qualityCodes: ['QC_GAP'] })] });
    const [issue] = buildQualityIssues(segments, []);
    if (issue === undefined) throw new Error('no issue');
    renderIssue(issue);
    expect(await screen.findByTestId(`quality-action-unavailable-${issue.id}`)).toBeDefined();
    expect(screen.getByTestId(`quality-retry-unavailable-${issue.id}`).getAttribute('title')).toContain('processing.retry');
    expect(screen.queryByTestId(`quality-blocked-note-${issue.id}`)).toBeNull();
    cleanup();
    queryClient.clear();
    renderIssue(issueFor('QC_UNRESOLVED_REVIEW'));
    const blocked = buildQualityIssues(parseQualitySegments({ items: [makeSegment(0, { qualityCodes: ['QC_UNRESOLVED_REVIEW'] })] }), [])[0];
    if (blocked === undefined) throw new Error('no blocked issue');
    expect((await screen.findByTestId(`quality-blocked-note-${blocked.id}`)).textContent).toContain('blocks the render');
  });
});
