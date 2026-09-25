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
  storage: { usedBytes: 1000, quotaBytes: 100000 },
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
    media: { id: 'med_1', status: 'Valid', container: 'mp4', sizeBytes: 1024, durationMs: 61000 },
    run: { id: 'run_1', status: 'Running', configHash: 'cfg_abc', attempt: 1 },
    phase: 'speech',
    stage: 'Transcription',
    progress: { percentApproximate: 42, currentStage: 'Transcription', updatedAt: '2024-01-16T12:00:00Z' },
    review: { pendingCount: 0, oldestWaitingAt: null },
    warnings: [],
    output: { state: 'pending', completeness: 42 },
    cost: { runCost: 1.5, monthToDate: 12.5 },
    activity: { recent: [] },
    permissions: { allowedActions: ['project.view', 'project.edit', 'processing.retry', 'export.create', 'admin.manage'] },
  };
}

interface ActivityState {
  items: Record<string, unknown>[];
  prefs: Record<string, string>;
}

function initialState(): ActivityState {
  return {
    items: [
      {
        id: 'act_1',
        summary: 'Run started',
        occurredAt: '2024-01-16T12:00:00Z',
        actor: 'System',
        action: 'RunStarted',
        run: 'run_1',
        stage: 'Transcription',
      },
      {
        id: 'act_2',
        summary: 'Translation completed',
        occurredAt: '2024-01-16T11:00:00Z',
        actor: 'owner',
        action: 'StageCompleted',
        stage: 'Translation',
      },
      {
        id: 'act_3',
        summary: 'Review requested',
        occurredAt: '2024-01-16T10:00:00Z',
        actor: 'System',
        action: 'ReviewRequested',
      },
    ],
    prefs: {
      locale: 'en',
      timezone: 'UTC',
      theme: 'light',
      defaultProjectFilters: JSON.stringify({ status: '', archived: '' }),
      timelineZoom: JSON.stringify(1),
      notificationPreferences: JSON.stringify({
        ProcessingCompleted: true,
        ProcessingFailed: true,
        ManualReviewRequired: true,
        ReviewResolved: true,
        ExportCompleted: true,
        ExportFailed: true,
        UploadRejected: true,
        QuotaWarning: true,
        ProviderPolicyWarning: true,
      }),
    },
  };
}

function sseFrame(): string {
  const data = {
    correlationId: 'corr_e2e',
    eventId: 'evt_1',
    eventType: 'stage.progress',
    occurredAt: '2024-01-16T12:00:00Z',
    payload: { projectId: 'prj_1', status: 'Running', percentApproximate: 42 },
    projectId: 'prj_1',
    processingRunId: 'run_1',
    schemaVersion: 1,
    tenantId: 'tenant_e2e',
  };
  return `id: evt_1\nevent: stage.progress\ndata: ${JSON.stringify(data)}\n\n`;
}

/**
 * `@activity` suite (Task 035). Hermetic: auth, dashboard, projects,
 * workspace, activity, cost (dashboard + workspace), prefs, progress, and
 * SSE are intercepted. SPA navigation only (no reload except the settings
 * persistence test, which re-logs in afterwards).
 */
async function mockActivityApi(page: Page, state: ActivityState): Promise<void> {
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
    if (url.endsWith('/me/preferences') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(state.prefs) });
      return;
    }
    if (url.endsWith('/me/preferences') && method === 'PUT') {
      let body: Record<string, string> = {};
      try {
        const parsed = (await request.postDataJSON()) as unknown;
        if (parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)) {
          body = parsed as Record<string, string>;
        }
      } catch {
        body = {};
      }
      for (const key of Object.keys(body)) {
        state.prefs[key] = body[key] as string;
      }
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(state.prefs) });
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
      await route.fulfill({ status: 200, headers: CORS_SSE, body: sseFrame() });
      return;
    }
    if (url.includes('/progress') && !url.includes('/stream') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ percentApproximate: 42, projectId: 'prj_1', status: 'Running' }) });
      return;
    }
    if (url.includes('/projects/prj_1/activity') && method === 'GET') {
      const sorted = [...state.items].sort((a, b) => String(b['occurredAt']).localeCompare(String(a['occurredAt'])));
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ items: sorted, page: 1, pageSize: 20, total: sorted.length, hasMore: false }),
      });
      return;
    }
    if (url.includes('/projects/prj_1/workspace') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(workspaceBody()) });
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

async function gotoActivity(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.getByTestId('nav-projects').click();
  await expect(page.getByTestId('projects-grid')).toBeVisible();
  await page.getByTestId('project-open-prj_1').click();
  await expect(page.getByTestId('workspace-page')).toBeVisible();
  await page.getByTestId('project-tab-activity').click();
  await expect(page.getByTestId('page-project-activity')).toBeVisible();
  await expect(page.getByTestId('activity-timeline')).toBeVisible();
}

async function gotoSettings(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.getByTestId('nav-settings').click();
  await expect(page.getByTestId('page-settings')).toBeVisible();
  await expect(page.getByTestId('settings-page')).toBeVisible();
}

test('timeline renders with shareable filters @activity', async ({ page }) => {
  const state = initialState();
  await mockActivityApi(page, state);
  await gotoActivity(page);
  await expect(page.getByTestId('activity-table')).toBeVisible();
  await expect(page.getByTestId('activity-row-act_1')).toBeVisible();
  await expect(page.getByTestId('activity-row-act_2')).toBeVisible();
  await expect(page.getByTestId('activity-row-act_3')).toBeVisible();
  await expect(page.getByTestId('activity-actor-act_1')).toContainText('System');
  await expect(page.getByTestId('activity-action-act_1')).toContainText('RunStarted');
  expect(await page.getByTestId('activity-advanced-act_1').count()).toBe(0);
  await page.getByTestId('activity-row-act_1-toggle').click();
  await expect(page.getByTestId('activity-advanced-act_1')).toBeVisible();
  await page.getByTestId('activity-filter-actor').fill('owner');
  await expect(page.getByTestId('activity-row-act_2')).toBeVisible();
  await expect(page.getByTestId('activity-row-act_1')).toHaveCount(0);
  await expect(page).toHaveURL(/actor=owner/);
  await page.getByTestId('activity-filters-reset').click();
  await expect(page.getByTestId('activity-row-act_1')).toBeVisible();
  await expect(page.getByTestId('activity-row-act_3')).toBeVisible();
});

test('cost summary shows estimate and actual with quota state @activity', async ({ page }) => {
  const state = initialState();
  await mockActivityApi(page, state);
  await gotoActivity(page);
  await expect(page.getByTestId('cost-summary')).toBeVisible();
  await expect(page.getByTestId('cost-estimate-label')).toContainText('Estimate');
  await expect(page.getByTestId('cost-estimated')).toContainText('Estimate');
  await expect(page.getByTestId('cost-actual')).toBeVisible();
  await expect(page.getByTestId('cost-actual-month')).toBeVisible();
  await expect(page.getByTestId('cost-quota-available')).toBeVisible();
  const body = await page.getByTestId('cost-summary').textContent();
  expect(body?.toLowerCase()).not.toContain('res_');
  expect(body?.toLowerCase()).not.toContain('reservation');
});

test('settings save persists across navigation @activity', async ({ page }) => {
  const state = initialState();
  await mockActivityApi(page, state);
  await gotoSettings(page);
  await expect(page.getByTestId('settings-field-locale')).toBeVisible();
  await expect(page.getByTestId('settings-field-locale')).toHaveValue('en');
  await page.getByTestId('settings-field-locale').selectOption('ar');
  await expect(page.getByTestId('settings-locale-note')).toBeVisible();
  expect(state.prefs['locale']).toBe('ar');
  await page.getByTestId('nav-dashboard').click();
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.getByTestId('nav-settings').click();
  await expect(page.getByTestId('settings-page')).toBeVisible();
  await expect(page.getByTestId('settings-field-locale')).toHaveValue('ar');
});
