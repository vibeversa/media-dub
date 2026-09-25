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
    },
    warnings: [],
    updatedAt: '2024-01-16T12:00:00Z',
  };
}

function exportsBody(): Record<string, unknown> {
  return {
    items: [
      { id: 'exp_1', projectId: 'prj_1', format: 'srt', status: 'Completed', isPartial: false, createdAt: '2024-01-16T12:00:00Z', completenessJson: null },
    ],
    page: 1,
    pageSize: 100,
    total: 1,
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

interface NotificationsState {
  items: Record<string, unknown>[];
  prefs: Record<string, string>;
}

function initialNotifications(): NotificationsState {
  return {
    items: [
      {
        id: 'ntf_1',
        type: 'ProcessingCompleted',
        severity: 'Info',
        title: 'Processing completed',
        body: 'Run abc12345 completed.',
        resourceType: 'ProcessingRun',
        resourceId: 'run_1',
        projectId: 'prj_1',
        readAt: null,
        createdAt: '2024-01-16T12:00:00Z',
        expiresAt: null,
      },
      {
        id: 'ntf_2',
        type: 'ManualReviewRequired',
        severity: 'Warning',
        title: 'Manual review required',
        body: 'Stage Render needs review.',
        resourceType: 'ReviewItem',
        resourceId: 'rev_1',
        projectId: 'prj_1',
        readAt: null,
        createdAt: '2024-01-16T11:00:00Z',
        expiresAt: null,
      },
      {
        id: 'ntf_3',
        type: 'ExportCompleted',
        severity: 'Info',
        title: 'Export completed',
        body: 'Export abc12345 completed.',
        resourceType: 'ExportJob',
        resourceId: 'exp_1',
        projectId: 'prj_1',
        readAt: null,
        createdAt: '2024-01-16T10:00:00Z',
        expiresAt: null,
      },
    ],
    prefs: {
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

/**
 * `@notifications` suite (Task 034). Hermetic: auth, dashboard, list,
 * workspace, progress, SSE, notifications, prefs, output, exports, and
 * reviews are intercepted. SPA navigation only (no reload).
 */
async function mockNotificationsApi(page: Page, state: NotificationsState): Promise<void> {
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
      const body = (await request.postDataJSON().catch(() => ({}))) as Record<string, string>;
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
      await route.fulfill({ status: 200, headers: CORS_SSE, body: sseFrame('evt_1', 'stage.progress') });
      return;
    }
    if (url.includes('/progress') && !url.includes('/stream') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ percentApproximate: 100, projectId: 'prj_1', status: 'Completed' }) });
      return;
    }
    if (url.includes('/notifications/unread-count') && method === 'GET') {
      const unread = state.items.filter((entry) => entry['readAt'] === null || entry['readAt'] === undefined).length;
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ unreadCount: unread }) });
      return;
    }
    if (method === 'POST' && url.includes('/notifications/read-all')) {
      for (const entry of state.items) {
        entry['readAt'] = '2024-01-16T13:00:00Z';
      }
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ marked: state.items.length, markedCount: state.items.length }) });
      return;
    }
    if (method === 'POST' && /\/notifications\/[^/?]+\/read/.test(url)) {
      const match = /\/notifications\/([^/?]+)\/read/.exec(url);
      const id = match?.[1] ?? '';
      const target = state.items.find((entry) => entry['id'] === id);
      if (target !== undefined) {
        target['readAt'] = '2024-01-16T13:00:00Z';
      }
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ marked: true }) });
      return;
    }
    if (method === 'GET' && url.includes('/notifications')) {
      const sorted = [...state.items].sort((a, b) => String(b['createdAt']).localeCompare(String(a['createdAt'])));
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ items: sorted, page: 1, pageSize: 20, total: sorted.length, hasMore: false }),
      });
      return;
    }
    if (method === 'GET' && url.includes('/output') && !url.includes('/download')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(outputBody()) });
      return;
    }
    if (method === 'GET' && url.includes('/exports')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(exportsBody()) });
      return;
    }
    if (method === 'GET' && url.includes('/processing') && !/\/processing\//.test(url)) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ items: [], page: 1, pageSize: 20, total: 0, hasMore: false }) });
      return;
    }
    if (method === 'GET' && url.includes('/projects/prj_1/reviews')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ items: [], page: 1, pageSize: 50, total: 0, hasMore: false }) });
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

async function gotoNotifications(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await expect(page.getByTestId('nav-bell-badge')).toBeVisible();
  await page.getByTestId('nav-bell').click();
  await expect(page.getByTestId('page-notifications')).toBeVisible();
  await expect(page.getByTestId('notifications-center')).toBeVisible();
}

test('bell badge appears and the Center lists durable notifications @notifications', async ({ page }) => {
  const state = initialNotifications();
  await mockNotificationsApi(page, state);
  await gotoNotifications(page);
  await expect(page.getByTestId('nav-bell-badge')).toContainText('3');
  await expect(page.getByTestId('notifications-list')).toBeVisible();
  await expect(page.getByTestId('notification-row-ntf_1')).toBeVisible();
  await expect(page.getByTestId('notification-row-ntf_2')).toBeVisible();
  await expect(page.getByTestId('notification-row-ntf_3')).toBeVisible();
  await expect(page.getByTestId('notifications-unread-count')).toContainText('3 unread');
});

test('every notification type deep-links to its target @notifications', async ({ page }) => {
  const state = initialNotifications();
  await mockNotificationsApi(page, state);
  await gotoNotifications(page);
  await expect(page.getByTestId('notifications-list')).toBeVisible();

  await expect(page.getByTestId('notification-link-ntf_1')).toHaveAttribute('href', '/projects/prj_1');
  await expect(page.getByTestId('notification-link-ntf_2')).toHaveAttribute('href', /\/review\?project=prj_1/);
  await expect(page.getByTestId('notification-link-ntf_3')).toHaveAttribute('href', '/projects/prj_1/exports');

  await page.getByTestId('notification-link-ntf_1').click();
  await expect(page.getByTestId('workspace-page')).toBeVisible();

  await page.getByTestId('nav-bell').click();
  await expect(page.getByTestId('notifications-center')).toBeVisible();
  await page.getByTestId('notification-link-ntf_2').click();
  await expect(page.getByTestId('page-review')).toBeVisible();

  await page.getByTestId('nav-bell').click();
  await expect(page.getByTestId('notifications-center')).toBeVisible();
  await page.getByTestId('notification-link-ntf_3').click();
  await expect(page.getByTestId('outputs-workspace')).toBeVisible();
});

test('read-all clears the badge @notifications', async ({ page }) => {
  const state = initialNotifications();
  await mockNotificationsApi(page, state);
  await gotoNotifications(page);
  await expect(page.getByTestId('nav-bell-badge')).toBeVisible();
  await page.getByTestId('notifications-read-all').click();
  await expect(page.getByTestId('notifications-unread-count')).toContainText('0 unread');
  await expect(page.getByTestId('nav-bell-badge')).toBeHidden({ timeout: 5000 }).catch(async () => {
    await expect(page.getByTestId('nav-bell-badge')).toHaveCount(0);
  });
});
