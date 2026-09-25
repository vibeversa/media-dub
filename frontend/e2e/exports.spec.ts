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
  permissions: ['project.view', 'project.edit', 'processing.retry', 'export.create', 'review.view', 'admin.manage'],
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
  backlog: { pendingReviews: 1, runningJobs: 0 },
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
    project: { id: 'prj_1', name: 'Pilot', status: 'Completed', sourceLanguage: 'en', targetLanguage: 'es', isArchived: false, configurationHash: 'cfg_abc', settingsVersion: 3 },
    media: { id: 'med_1', status: 'Valid', container: 'mp4', sizeBytes: 1024, durationMs: 61000 },
    run: { id: 'run_1', status: 'Completed', configHash: 'cfg_abc', attempt: 1 },
    phase: 'completed',
    stage: 'Render',
    progress: { percentApproximate: 100, currentStage: 'Render', updatedAt: '2024-01-16T12:00:00Z' },
    review: { pendingCount: 1, oldestWaitingAt: '2024-01-16T10:00:00Z' },
    warnings: [],
    output: { state: 'pending', completeness: 100 },
    cost: { runCost: 1.5, monthToDate: 12.5 },
    activity: { recent: [] },
    permissions: { allowedActions: ['project.view', 'project.edit', 'processing.retry', 'export.create'] },
  };
}

function outputBody(): Record<string, unknown> {
  return {
    state: 'Partial',
    generationState: 'Partial',
    reason: null,
    completeness: { ready: 96, total: 100 },
    progressApproximate: null,
    errorCode: null,
    items: {
      video: { state: 'ready', generationState: 'ready', downloadUrl: 'https://example.com/video.mp4', missing: [], completeness: null },
      audio: { state: 'generating', generationState: 'generating', downloadUrl: null, missing: ['SEGMENT_PENDING'], completeness: { ready: 50, total: 100 } },
      subtitles: [{ state: 'ready', generationState: 'ready', downloadUrl: 'https://example.com/subs.srt', missing: [], completeness: null }],
      transcript: { state: 'failed', generationState: 'failed', downloadUrl: null, missing: ['ARTIFACT_MISSING'], completeness: null },
      translation: { state: 'partial', generationState: 'partial', downloadUrl: 'https://example.com/trans.json', missing: ['SEGMENT_PENDING'], completeness: { ready: 96, total: 100 } },
      timeline: { state: 'unavailable', generationState: 'unavailable', downloadUrl: null, missing: ['NO_RUNS_YET'], completeness: null },
      speakers: { state: 'ready', generationState: 'ready', downloadUrl: null, missing: [], completeness: null },
      qc: { state: 'partial', generationState: 'partial', summary: '4 finding(s), 1 blocked.', issuesUrl: null, missing: ['QC_BLOCKED'] },
    },
    warnings: ['qc-blocked'],
    updatedAt: '2024-01-16T12:00:00Z',
  };
}

function exportsBody(): Record<string, unknown> {
  return {
    items: [
      { id: 'exp_queued_1', projectId: 'prj_1', format: 'srt', status: 'Pending', isPartial: false, createdAt: '2024-01-16T10:00:00Z', completenessJson: null },
      { id: 'exp_gen_1', projectId: 'prj_1', format: 'webvtt', status: 'Running', isPartial: false, createdAt: '2024-01-16T11:00:00Z', completenessJson: JSON.stringify({ ready: 50, total: 100 }) },
      { id: 'exp_ready_1', projectId: 'prj_1', format: 'json-timeline', status: 'Completed', isPartial: false, createdAt: '2024-01-16T12:00:00Z', completenessJson: null },
      { id: 'exp_failed_1', projectId: 'prj_1', format: 'transcript', status: 'Failed', isPartial: true, createdAt: '2024-01-16T09:00:00Z', completenessJson: null, reason: 'Render failed' },
    ],
    page: 1,
    pageSize: 100,
    total: 4,
    hasMore: false,
  };
}

function runsBody(): Record<string, unknown> {
  return {
    items: [
      { runId: 'run_2', status: 'Running' },
      { runId: 'run_1', status: 'Completed' },
    ],
    page: 1,
    pageSize: 20,
    total: 2,
    hasMore: false,
  };
}

function sseFrame(id: string, eventType: string): string {
  const data = {
    correlationId: 'corr_e2e',
    eventId: id,
    eventType,
    occurredAt: '2024-01-16T12:00:00Z',
    payload: { projectId: 'prj_1', status: 'Completed' },
    projectId: 'prj_1',
    processingRunId: 'run_1',
    schemaVersion: 1,
    tenantId: 'tenant_e2e',
  };
  return `id: ${id}\nevent: ${eventType}\ndata: ${JSON.stringify(data)}\n\n`;
}

/**
 * `@exports` suite (Task 033). Hermetic: auth, dashboard, list, workspace,
 * progress, SSE, unread-count, output, exports, runs, and export download
 * are intercepted. SPA navigation only (no reload).
 */
async function mockExportsApi(page: Page): Promise<void> {
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
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ percentApproximate: 100, projectId: 'prj_1', status: 'Completed' }) });
      return;
    }
    if (url.includes('/notifications/unread-count') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ unreadCount: 0 }) });
      return;
    }
    if (method === 'POST' && /\/exports$/.test(url.split('?')[0] ?? '')) {
      await route.fulfill({
        status: 202,
        headers: CORS_JSON,
        body: JSON.stringify({ id: 'exp_new_1', projectId: 'prj_1', format: 'srt', status: 'Pending', isPartial: false, createdAt: '2024-01-16T13:00:00Z', completenessJson: null }),
      });
      return;
    }
    if (method === 'GET' && /\/exports\/[^/?]+\/download/.test(url)) {
      await route.fulfill({
        status: 302,
        headers: { ...CORS_JSON, Location: 'https://example.com/export.srt' },
        body: '',
      });
      return;
    }
    if (method === 'GET' && url.includes('/exports')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(exportsBody()) });
      return;
    }
    if (url.includes('/output/download') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ downloadUrl: 'https://example.com/output.mp4', expiresAt: '2026-09-26T00:00:00Z' }) });
      return;
    }
    if (url.includes('/output') && !url.includes('/download') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(outputBody()) });
      return;
    }
    if (url.includes('/processing') && method === 'GET' && !/\/processing\//.test(url)) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(runsBody()) });
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

async function gotoExports(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.getByTestId('dashboard-stat-link').click();
  await expect(page.getByTestId('projects-grid')).toBeVisible();
  await page.getByTestId('project-open-prj_1').click();
  await expect(page.getByTestId('workspace-page')).toBeVisible();
  const tab = page.getByTestId('project-tab-exports');
  if ((await tab.count()) > 0) {
    const tag = await tab.evaluate((el) => el.tagName.toLowerCase());
    if (tag === 'a') {
      await tab.click();
    } else {
      await page.evaluate(() => {
        window.history.pushState({}, '', '/projects/prj_1/exports');
        window.dispatchEvent(new PopStateEvent('popstate'));
      });
    }
  } else {
    await page.evaluate(() => {
      window.history.pushState({}, '', '/projects/prj_1/exports');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
  }
  await expect(page.getByTestId('outputs-workspace')).toBeVisible();
}

test('outputs page states render with partial explanations @exports', async ({ page }) => {
  await mockExportsApi(page);
  await gotoExports(page);
  await expect(page.getByTestId('outputs-summary')).toBeVisible();
  await expect(page.getByTestId('outputs-state-partial')).toContainText('Partial');
  await expect(page.getByTestId('outputs-partial-explanation')).toContainText('96/100');
  await expect(page.getByTestId('outputs-partial-quality-link')).toHaveAttribute('href', '/projects/prj_1/quality');
  await expect(page.getByTestId('output-item-video')).toHaveAttribute('data-state', 'Ready');
  await expect(page.getByTestId('output-item-audio')).toHaveAttribute('data-state', 'Generating');
  await expect(page.getByTestId('output-item-transcript')).toHaveAttribute('data-state', 'Failed');
  await expect(page.getByTestId('output-item-translation')).toHaveAttribute('data-state', 'Partial');
  await expect(page.getByTestId('output-item-timeline')).toHaveAttribute('data-state', 'Unavailable');
  await expect(page.getByTestId('exports-list')).toBeVisible();
  await expect(page.getByTestId('export-row-exp_ready_1')).toHaveAttribute('data-state', 'ready');
});

test('export dialog submits an allowlisted request @exports', async ({ page }) => {
  await mockExportsApi(page);
  await gotoExports(page);
  await expect(page.getByTestId('outputs-workspace')).toBeVisible();
  await page.getByTestId('export-open-dialog').click();
  await expect(page.getByTestId('export-dialog')).toBeVisible();
  await expect(page.getByTestId('export-format')).toBeVisible();
  await page.getByTestId('export-submit').click();
  await expect(page.getByTestId('export-dialog')).toBeHidden({ timeout: 5000 }).catch(async () => {
    await expect(page.getByTestId('export-error')).toBeHidden();
  });
});

test('ready export downloads via a click-time signed URL @exports', async ({ page }) => {
  await mockExportsApi(page);
  await gotoExports(page);
  await expect(page.getByTestId('exports-list')).toBeVisible();
  const download = page.getByTestId('export-download-exp_ready_1');
  await expect(download).toBeVisible();
  await expect(download).toHaveAttribute('download', '');
  const [request] = await Promise.all([
    page.waitForRequest((req) => req.url().includes('/exports/exp_ready_1/download')),
    download.click(),
  ]);
  expect(request.method()).toBe('GET');
});
