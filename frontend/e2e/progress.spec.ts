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
  permissions: [
    'project.view',
    'project.edit',
    'project.delete',
    'processing.cancel',
    'processing.retry',
    'review.view',
    'export.create',
    'export.download',
    'admin.manage',
  ],
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

interface ProgressWorld {
  workspacePercent: number;
  workspaceCalls: number;
  progressCalls: number;
  streamCalls: number;
  unreadCalls: number;
  unreadCount: number;
  streamMode: 'progress' | 'completion' | 'blocked';
  streamAuth: string[];
  streamUrls: string[];
}

function newWorld(): ProgressWorld {
  return {
    workspacePercent: 42,
    workspaceCalls: 0,
    progressCalls: 0,
    streamCalls: 0,
    unreadCalls: 0,
    unreadCount: 0,
    streamMode: 'progress',
    streamAuth: [],
    streamUrls: [],
  };
}

function workspaceBody(world: ProgressWorld): Record<string, unknown> {
  return {
    project: {
      id: 'prj_1',
      name: 'Pilot',
      status: 'Processing',
      sourceLanguage: 'en',
      targetLanguage: 'es',
      isArchived: false,
      configurationHash: 'cfg_abc',
      settingsVersion: 3,
    },
    media: { id: 'med_1', status: 'Valid', container: 'mp4', sizeBytes: 1024, durationMs: 61000 },
    run: { id: 'run_1', status: 'Running', configHash: 'cfg_abc', attempt: 1 },
    phase: 'speech',
    stage: 'Transcription',
    progress: { percentApproximate: world.workspacePercent, currentStage: 'Transcription', updatedAt: '2024-01-16T12:00:00Z' },
    review: { pendingCount: 2, oldestWaitingAt: '2024-01-16T10:00:00Z' },
    warnings: [],
    output: { state: 'pending', completeness: world.workspacePercent },
    cost: { runCost: 1.5, monthToDate: 12.5 },
    activity: { recent: [{ id: 'act_1', summary: 'Run started', occurredAt: '2024-01-16T11:00:00Z' }] },
    permissions: {
      allowedActions: ['project.view', 'project.edit', 'project.delete', 'processing.cancel', 'processing.retry', 'export.create', 'export.download', 'admin.manage'],
    },
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
 * `@progress` suite (Task 026). Hermetic: login/identity, dashboard, list,
 * workspace aggregate, progress snapshot, SSE stream, and unread-count are
 * intercepted. The stream mock emits frozen-envelope frames; the workspace
 * mock bumps its percent on refetch so invalidations move the live bar.
 */
async function mockProgressApi(page: Page, world: ProgressWorld): Promise<void> {
  await page.route('**/api/v1/**', async (route: Route) => {
    const request = route.request();
    if (request.method() === 'OPTIONS') {
      await route.fulfill({ status: 204, headers: CORS_JSON, body: '' });
      return;
    }
    const url = request.url();
    if (url.includes('/auth/login') && request.method() === 'POST') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(TOKEN_PAIR) });
      return;
    }
    if (url.includes('/auth/refresh') && request.method() === 'POST') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(TOKEN_PAIR) });
      return;
    }
    if (url.includes('/auth/logout')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ loggedOut: true }) });
      return;
    }
    if (url.endsWith('/me') && request.method() === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(ME_BODY) });
      return;
    }
    if (url.includes('/dashboard/summary') && request.method() === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(SUMMARY_BODY) });
      return;
    }
    if (url.includes('/progress/stream') && request.method() === 'GET') {
      world.streamCalls += 1;
      world.streamUrls.push(url);
      world.streamAuth.push(request.headers()['authorization'] ?? '');
      if (world.streamMode === 'blocked') {
        await route.fulfill({
          status: 500,
          headers: CORS_JSON,
          body: JSON.stringify({ error: { code: 'INTERNAL_ERROR', message: 'boom', correlationId: 'c', details: {} } }),
        });
        return;
      }
      if (world.streamMode === 'completion') {
        await route.fulfill({ status: 200, headers: CORS_SSE, body: sseFrame('evt_done', 'run.status_changed') });
        return;
      }
      await route.fulfill({ status: 200, headers: CORS_SSE, body: sseFrame(`evt_${String(world.streamCalls)}`, 'stage.completed') });
      return;
    }
    if (url.includes('/progress') && !url.includes('/stream') && request.method() === 'GET') {
      world.progressCalls += 1;
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ percentApproximate: world.workspacePercent, projectId: 'prj_1', status: 'Running' }),
      });
      return;
    }
    if (url.includes('/notifications/unread-count') && request.method() === 'GET') {
      world.unreadCalls += 1;
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ unreadCount: world.unreadCount }) });
      return;
    }
    if (url.includes('/workspace') && request.method() === 'GET') {
      world.workspaceCalls += 1;
      // Live bar movement: every refetch after the first bumps the percent.
      if (world.workspaceCalls > 1) {
        world.workspacePercent = 80;
      }
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(workspaceBody(world)) });
      return;
    }
    if (url.includes('/api/v1/projects') && request.method() === 'GET') {
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

async function gotoWorkspace(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.getByTestId('dashboard-stat-link').click();
  await expect(page.getByTestId('projects-grid')).toBeVisible();
  await page.getByTestId('project-open-prj_1').click();
  await expect(page.getByTestId('workspace-page')).toBeVisible();
  await expect(page.getByTestId('workspace-header')).toBeVisible();
}

test('live bar moves on streamed events with approximate display @progress', async ({ page }) => {
  const world = newWorld();
  world.streamMode = 'progress';
  await mockProgressApi(page, world);
  await gotoWorkspace(page);

  await expect(page.getByTestId('workspace-progress-value')).toContainText('42%');
  await expect(page.getByTestId('workspace-progress-value')).toContainText('~');
  await expect(page.getByTestId('workspace-progress-approx')).toContainText('approximate');

  // The SSE frame invalidates the workspace; the refetch bumps to 80%.
  await expect(page.getByTestId('workspace-progress-value')).toContainText('80%', { timeout: 15000 });
  expect(world.streamCalls).toBeGreaterThanOrEqual(1);
  expect(world.workspaceCalls).toBeGreaterThanOrEqual(2);
  // Header auth only: token never appears in the stream URL.
  for (const streamUrl of world.streamUrls) {
    expect(streamUrl).not.toContain('e2e-access-token');
    expect(streamUrl).not.toContain('access_token');
  }
  for (const auth of world.streamAuth) {
    expect(auth).toBe('Bearer e2e-access-token');
  }
});

test('completion updates the notification badge @progress', async ({ page }) => {
  const world = newWorld();
  world.streamMode = 'completion';
  world.unreadCount = 3;
  await mockProgressApi(page, world);
  await gotoWorkspace(page);

  await expect(page.getByTestId('workspace-page')).toBeVisible();
  // Completion invalidates the badge query; the shell bell shows the count.
  await expect(page.getByTestId('nav-bell-badge')).toContainText('3', { timeout: 15000 });
  expect(world.unreadCalls).toBeGreaterThanOrEqual(1);
});

test('polling fallback engages when the stream is blocked @progress', async ({ page }) => {
  const world = newWorld();
  world.streamMode = 'blocked';
  await mockProgressApi(page, world);
  await gotoWorkspace(page);

  await expect(page.getByTestId('workspace-page')).toBeVisible();
  // Blocked SSE (500) backs off then yields to the adaptive progress poll.
  await expect.poll(() => world.progressCalls, { timeout: 25000 }).toBeGreaterThan(0);
  expect(world.streamCalls).toBeGreaterThanOrEqual(1);
});
