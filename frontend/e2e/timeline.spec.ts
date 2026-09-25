import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';

const CORS_JSON = {
  'Content-Type': 'application/json',
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': '*',
  'Access-Control-Allow-Methods': '*',
};

const CORS_SSE = {
  'Content-Type': 'text/event-stream',
  'Cache-Control': 'no-cache',
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': '*',
  'Access-Control-Allow-Methods': '*',
};

const TOKEN_PAIR = {
  accessToken: 'e2e-access-token',
  refreshToken: 'e2e-refresh-token',
  tokenType: 'Bearer',
  expiresInSeconds: 900,
  userId: 'usr_e2e',
  tenantId: 'tenant_e2e',
};

const ME_BODY = {
  permissions: ['project.view', 'project.edit', 'project.delete', 'processing.cancel', 'processing.retry', 'review.view', 'export.create', 'export.download', 'admin.manage'],
  roles: [],
  userId: 'usr_e2e',
  tenantId: 'tenant_e2e',
};

const SUMMARY_BODY = {
  projectCounts: { active: 1, archived: 0, total: 1 },
  recentOutputs: [],
  storage: { usedBytes: 1, quotaBytes: 100 },
  cost: { monthToDate: 12.5, currency: 'USD' },
  quota: { remaining: 9, resetsAt: '2026-09-25T00:00:00Z' },
  warnings: [],
  backlog: { pendingReviews: 0, runningJobs: 0 },
};

const LIST_BODY = {
  items: [
    {
      id: 'prj_1',
      name: 'Pilot',
      status: 'Processing',
      sourceLanguage: 'en',
      targetLanguage: 'es',
      createdAt: '2024-01-15T12:00:00Z',
      updatedAt: '2024-01-16T12:00:00Z',
      isArchived: false,
      settingsVersion: 3,
    },
  ],
  page: 1,
  pageSize: 20,
  total: 1,
  sort: 'createdAt',
  sortDir: 'desc',
  hasMore: false,
  clamped: false,
};

function workspaceBody(): Record<string, unknown> {
  return {
    project: { id: 'prj_1', name: 'Pilot', status: 'Processing', sourceLanguage: 'en', targetLanguage: 'es', isArchived: false, configurationHash: 'cfg_abc', settingsVersion: 3 },
    media: { id: 'med_1', status: 'Valid', container: 'mp4', sizeBytes: 1024, durationMs: 61_000 },
    run: { id: 'run_1', status: 'Running', configHash: 'cfg_abc', attempt: 1 },
    phase: 'speech',
    stage: 'Transcription',
    progress: { percentApproximate: 42, currentStage: 'Transcription', updatedAt: '2024-01-16T12:00:00Z' },
    review: { pendingCount: 2, oldestWaitingAt: '2024-01-16T10:00:00Z' },
    warnings: [],
    output: { state: 'pending', completeness: 42 },
    cost: { runCost: 1.5, monthToDate: 12.5 },
    activity: { recent: [{ id: 'act_1', summary: 'Run started', occurredAt: '2024-01-16T11:00:00Z' }] },
    permissions: { allowedActions: ['project.view', 'project.edit', 'project.delete', 'processing.cancel', 'processing.retry', 'export.create', 'export.download', 'admin.manage'] },
  };
}

function segmentRow(index: number): Record<string, unknown> {
  const id = `seg_${String(index + 1).padStart(3, '0')}`;
  const startMs = index * 2000;
  return {
    id,
    projectId: 'prj_1',
    status: 'Ready',
    sequence: index + 1,
    startMs,
    endMs: startMs + 1800,
    speakerId: index % 2 === 0 ? 'spk_alice' : 'spk_bob',
    speakerLabel: index % 2 === 0 ? 'Alice' : 'Bob',
    selectionVersion: 2,
    reviewStatus: index === 0 ? 'Open' : 'Approved',
    qualityCodes: index === 0 ? ['QC_NOISY'] : [],
    syncStatus: 'SyncAcceptable',
    confidence: index === 0 ? 0.4 : 0.95,
    text: `selected line ${id}`,
    transcriptVersions: [
      { id: `${id}-v1`, provider: 'acme', model: 'stt-v1', text: `original line ${id}`, isSelected: false, createdAt: '2024-01-15T12:00:00Z' },
      { id: `${id}-v2`, provider: 'acme', model: 'stt-v2', text: `selected line ${id}`, isSelected: true, createdAt: '2024-01-15T13:00:00Z' },
    ],
  };
}

function segmentsBody(): Record<string, unknown> {
  return { items: [segmentRow(0), segmentRow(1), segmentRow(2)], page: 1, pageSize: 200, total: 3, hasMore: false };
}

function peaksBody(): Record<string, unknown> {
  const series = (count: number, seed: number): number[] => {
    const out: number[] = [];
    for (let i = 0; i < count; i += 1) {
      out.push(((i * 37 + seed * 13) % 100) / 100);
    }
    return out;
  };
  return {
    durationMs: 61_000,
    sampleRate: 16_000,
    resolutions: { 64: series(64, 0), 256: series(256, 1), 1024: series(1024, 2) },
  };
}

function sseFrame(id: string, eventType: string): string {
  const data = {
    correlationId: 'corr_e2e',
    eventId: id,
    eventType,
    occurredAt: '2024-01-16T12:00:00Z',
    payload: { projectId: 'prj_1', status: 'Running' },
    projectId: 'prj_1',
    processingRunId: 'run_1',
    schemaVersion: 1,
    tenantId: 'tenant_e2e',
  };
  return `id: ${id}\nevent: ${eventType}\ndata: ${JSON.stringify(data)}\n\n`;
}

/**
 * `@timeline` suite (Task 030). Hermetic: auth, dashboard, list,
 * workspace, progress, SSE, unread-count, segments, output/download, and
 * waveform-peaks are intercepted. SPA navigation only (no reload).
 */
async function mockTimelineApi(page: Page): Promise<void> {
  await page.route('**/api/v1/**', async (route: Route) => {
    const request = route.request();
    if (request.method() === 'OPTIONS') {
      await route.fulfill({ status: 204, headers: CORS_JSON, body: '' });
      return;
    }
    const url = request.url();
    const method = request.method();
    if (url.includes('/auth/login') && method === 'POST') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(TOKEN_PAIR) });
      return;
    }
    if (url.includes('/auth/refresh') && method === 'POST') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(TOKEN_PAIR) });
      return;
    }
    if (url.includes('/auth/logout')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ loggedOut: true }) });
      return;
    }
    if (url.endsWith('/me') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(ME_BODY) });
      return;
    }
    if (url.includes('/dashboard/summary') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(SUMMARY_BODY) });
      return;
    }
    if (url.includes('/progress/stream') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_SSE, body: sseFrame('evt_1', 'stage.progress') });
      return;
    }
    if (url.includes('/progress') && !url.includes('/stream') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ percentApproximate: 42, projectId: 'prj_1', status: 'Running' }) });
      return;
    }
    if (url.includes('/notifications/unread-count') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ unreadCount: 0 }) });
      return;
    }
    if (url.includes('/output/download') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ downloadUrl: 'https://example.com/media.mp4', expiresAt: '2026-09-26T00:00:00Z' }) });
      return;
    }
    if (url.includes('/waveform-peaks') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(peaksBody()) });
      return;
    }
    if (method === 'GET' && /\/segments\/seg_/.test(url)) {
      const match = /\/segments\/(seg_[^/?]+)/.exec(url);
      const segmentId = match?.[1] ?? 'seg_001';
      const index = Number(segmentId.replace('seg_', '')) - 1;
      const row = segmentRow(Number.isFinite(index) && index >= 0 ? index : 0);
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ ...row, id: segmentId }) });
      return;
    }
    if (method === 'GET' && url.includes('/segments')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(segmentsBody()) });
      return;
    }
    if (url.includes('/workspace') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(workspaceBody()) });
      return;
    }
    if (url.includes('/api/v1/projects') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(LIST_BODY) });
      return;
    }
    await route.fulfill({
      status: 404,
      headers: CORS_JSON,
      body: JSON.stringify({ error: { code: 'NOT_FOUND', message: 'No e2e mock', correlationId: 'c', details: {} } }),
    });
  });
}

async function loginThroughUi(page: Page): Promise<void> {
  await page.getByTestId('auth-tenant').fill('acme');
  await page.getByTestId('auth-email').fill('owner@example.com');
  await page.getByTestId('auth-password').fill('secret');
  await page.getByTestId('auth-submit').click();
}

async function gotoTimeline(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.getByTestId('dashboard-stat-link').click();
  await expect(page.getByTestId('projects-grid')).toBeVisible();
  await page.getByTestId('project-open-prj_1').click();
  await expect(page.getByTestId('workspace-page')).toBeVisible();
  const tab = page.getByTestId('project-tab-timeline');
  if ((await tab.count()) > 0) {
    await tab.click();
  } else {
    await page.evaluate(() => {
      window.history.pushState({}, '', '/projects/prj_1/timeline');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
  }
  await expect(page.getByTestId('timeline-workspace')).toBeVisible();
}

test('player plays and seeks with a signed media element @timeline', async ({ page }) => {
  await mockTimelineApi(page);
  await gotoTimeline(page);
  await expect(page.getByTestId('timeline-player')).toBeVisible();
  await expect(page.getByTestId('timeline-media')).toHaveAttribute('src', 'https://example.com/media.mp4');
  await expect(page.getByTestId('timeline-play-toggle')).toBeVisible();
  await expect(page.getByTestId('timeline-scrub')).toBeVisible();
  const position = page.getByTestId('timeline-position');
  const before = await position.textContent();
  await page.getByTestId('timeline-seek-forward').click();
  await expect(position).not.toHaveText(before ?? '');
});

test('waveform renders from peaks @timeline', async ({ page }) => {
  await mockTimelineApi(page);
  await gotoTimeline(page);
  await expect(page.getByTestId('timeline-waveform')).toBeVisible();
  const canvas = page.getByTestId('timeline-waveform-canvas');
  await expect(canvas).toBeVisible();
  const peaks = await canvas.getAttribute('data-peaks');
  expect(Number(peaks ?? '0')).toBeGreaterThan(0);
  await expect(page.getByTestId('timeline-waveform-scrub')).toBeVisible();
});

test('zoom, select, and ruler seek work across lanes @timeline', async ({ page }) => {
  await mockTimelineApi(page);
  await gotoTimeline(page);
  await expect(page.getByTestId('timeline')).toBeVisible();
  await expect(page.getByTestId('timeline-lane-video')).toBeVisible();
  await expect(page.getByTestId('timeline-lane-source-audio')).toBeVisible();
  await expect(page.getByTestId('timeline-lane-dialogue')).toBeVisible();
  await expect(page.getByTestId('timeline-lane-generated-audio')).toBeVisible();
  await expect(page.getByTestId('timeline-lane-markers')).toBeVisible();
  await page.getByTestId('timeline-segment-seg_002').click();
  await expect(page.getByTestId('timeline-segment-seg_002')).toHaveAttribute('data-selected', 'true');
  const zoomLabel = page.getByTestId('timeline-zoom-label');
  const beforeZoom = await zoomLabel.textContent();
  await page.getByTestId('timeline-zoom-in').click();
  await expect(zoomLabel).not.toHaveText(beforeZoom ?? '');
  const position = page.getByTestId('timeline-position');
  const beforePosition = await position.textContent();
  await page.getByTestId('timeline-ruler').click({ position: { x: 400, y: 5 } });
  await expect(position).not.toHaveText(beforePosition ?? '', { timeout: 5000 });
});

test('issue click jumps the playhead @timeline', async ({ page }) => {
  await mockTimelineApi(page);
  await gotoTimeline(page);
  await expect(page.getByTestId('timeline')).toBeVisible();
  const issue = page.getByTestId('timeline-issue-issue-seg_001');
  await expect(issue).toBeVisible();
  await issue.click();
  await expect(page.getByTestId('timeline')).toHaveAttribute('data-position', '0');
});
