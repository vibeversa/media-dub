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
  permissions: ['project.view', 'project.edit', 'processing.retry', 'review.view', 'export.create', 'admin.manage'],
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
    permissions: { allowedActions: ['project.view', 'project.edit', 'processing.retry'] },
  };
}

function segmentRow(index: number, overrides: Record<string, unknown> = {}): Record<string, unknown> {
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

function segmentsBody(): Record<string, unknown> {
  return {
    items: [
      segmentRow(0, {
        reviewStatus: 'Open',
        qualityCodes: ['QC_UNRESOLVED_REVIEW'],
        syncStatus: 'ManualReviewRequired',
        artifactId: 'art_qc_1',
        artifactUrl: 'https://example.com/qc-evidence-1.json',
      }),
      segmentRow(1, { reviewStatus: 'Open', qualityCodes: ['QC_SYNC_FAILURE'], syncStatus: 'ManualReviewRequired' }),
      segmentRow(2, { qualityCodes: ['QC_GAP'] }),
      segmentRow(3, { qualityCodes: [] }),
    ],
    page: 1,
    pageSize: 200,
    total: 4,
    hasMore: false,
  };
}

function qualityBody(): Record<string, unknown> {
  return { blockedCount: 1, failedCount: 1, codes: ['QC_GAP', 'QC_SYNC_FAILURE', 'QC_UNRESOLVED_REVIEW'] };
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
 * `@quality` suite (Task 032). Hermetic: auth, dashboard, list, workspace,
 * progress, SSE, unread-count, segments, quality, and output/download are
 * intercepted. SPA navigation only (no reload).
 */
async function mockQualityApi(page: Page): Promise<void> {
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
    if (url.includes('/output/download') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ downloadUrl: 'https://example.com/media.mp4', expiresAt: '2026-09-26T00:00:00Z' }) });
      return;
    }
    if (url.includes('/quality') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(qualityBody()) });
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

async function gotoQuality(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.getByTestId('dashboard-stat-link').click();
  await expect(page.getByTestId('projects-grid')).toBeVisible();
  await page.getByTestId('project-open-prj_1').click();
  await expect(page.getByTestId('workspace-page')).toBeVisible();
  const tab = page.getByTestId('project-tab-quality');
  if ((await tab.count()) > 0) {
    await tab.click();
  } else {
    await page.evaluate(() => {
      window.history.pushState({}, '', '/projects/prj_1/quality');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
  }
  await expect(page.getByTestId('quality-workspace')).toBeVisible();
}

test('summary renders all five states with matching counts @quality', async ({ page }) => {
  await mockQualityApi(page);
  await gotoQuality(page);
  await expect(page.getByTestId('quality-summary')).toBeVisible();
  await expect(page.getByTestId('quality-summary-passed')).toContainText('1');
  await expect(page.getByTestId('quality-summary-warning')).toContainText('1');
  await expect(page.getByTestId('quality-summary-review')).toContainText('1');
  await expect(page.getByTestId('quality-summary-blocked')).toContainText('1');
  await expect(page.getByTestId('quality-badge-blocked')).toContainText('blocked');
  await expect(page.getByTestId('quality-badge-blocked')).toContainText('hatched-block');
});

test('issue evidence plays and jumps @quality', async ({ page }) => {
  await mockQualityApi(page);
  await gotoQuality(page);
  await expect(page.getByTestId('quality-list')).toBeVisible();
  const blockedId = 'qc-seg_001-qc-unresolved-review';
  await expect(page.getByTestId(`quality-issue-${blockedId}`)).toBeVisible();
  await expect(page.getByTestId(`quality-evidence-waveform-${blockedId}`)).toBeVisible();
  await expect(page.getByTestId(`quality-evidence-timestamp-${blockedId}`)).toContainText('00:00.000');
  await expect(page.getByTestId(`quality-evidence-audio-${blockedId}`)).toHaveAttribute('src', 'https://example.com/media.mp4');
  await expect(page.getByTestId(`quality-evidence-metric-${blockedId}`)).toContainText('Segment');
  await expect(page.getByTestId(`quality-evidence-artifact-${blockedId}`)).toBeVisible();
  await page.getByTestId(`quality-jump-${blockedId}`).click();
  await expect(page.getByTestId('quality-workspace')).toBeVisible();
});

test('blocked banner is visible with blocked issues pinned first @quality', async ({ page }) => {
  await mockQualityApi(page);
  await gotoQuality(page);
  await expect(page.getByTestId('quality-list')).toBeVisible();
  await expect(page.getByTestId('quality-blocked-banner')).toBeVisible();
  await expect(page.getByTestId('quality-blocked-count')).toContainText('1');
  const first = page.locator('[data-testid^="quality-issue-"]').first();
  await expect(first).toHaveAttribute('data-status', 'Blocked');
});

test('filters narrow the list @quality', async ({ page }) => {
  await mockQualityApi(page);
  await gotoQuality(page);
  await expect(page.getByTestId('quality-list')).toBeVisible();
  await page.getByTestId('quality-filter-status').fill('Blocked');
  await page.getByTestId('quality-apply-filters').click();
  await expect(page).toHaveURL(/status=Blocked/);
  await expect(page.getByTestId('quality-list')).toHaveAttribute('data-total', '1');
});
