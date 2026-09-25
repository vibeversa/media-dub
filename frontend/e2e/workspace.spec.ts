import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';

const CORS_JSON = {
  'Content-Type': 'application/json',
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

interface WorkspaceWorld {
  phase: string;
  stage: string | null;
  runStatus: string | undefined;
  runId: string | undefined;
}

function newWorld(): WorkspaceWorld {
  return { phase: 'speech', stage: 'Transcription', runStatus: 'Running', runId: 'run_1' };
}

function workspaceBody(world: WorkspaceWorld): Record<string, unknown> {
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
    run:
      world.runId === undefined
        ? { id: undefined, status: world.runStatus, configHash: undefined, attempt: 0 }
        : { id: world.runId, status: world.runStatus, configHash: 'cfg_abc', attempt: 1 },
    phase: world.phase,
    stage: world.stage,
    progress: { percentApproximate: 42, currentStage: 'Transcription', updatedAt: '2024-01-16T12:00:00Z' },
    review: { pendingCount: 2, oldestWaitingAt: '2024-01-16T10:00:00Z' },
    warnings: [{ code: 'REVIEW_OPEN', message: 'Two reviews await.' }],
    output: { state: 'pending', completeness: 42 },
    cost: { runCost: 1.5, monthToDate: 12.5 },
    activity: {
      recent: [{ id: 'act_1', summary: 'Run started', occurredAt: '2024-01-16T11:00:00Z' }],
    },
    permissions: {
      allowedActions: [
        'project.view',
        'project.edit',
        'project.delete',
        'processing.cancel',
        'processing.retry',
        'export.create',
        'export.download',
        'admin.manage',
      ],
    },
  };
}

/**
 * `@workspace` suite (Task 025). Hermetic: login/identity, dashboard, the
 * project list, and the single workspace aggregate are intercepted, so no
 * backend is required. CORS/preflight handling mirrors `e2e/auth.spec.ts`.
 */
async function mockWorkspaceApi(page: Page, world: WorkspaceWorld): Promise<void> {
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
    if (url.includes('/workspace') && request.method() === 'GET') {
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
  // SPA navigation only: a full reload would drop the in-memory session.
  await page.getByTestId('dashboard-stat-link').click();
  await expect(page.getByTestId('projects-grid')).toBeVisible();
  await page.getByTestId('project-open-prj_1').click();
  await expect(page.getByTestId('workspace-page')).toBeVisible();
  await expect(page.getByTestId('workspace-header')).toBeVisible();
}

test('stepper renders with parallel notes @workspace', async ({ page }) => {
  await mockWorkspaceApi(page, { ...newWorld(), phase: 'translation', stage: 'Translation' });
  await gotoWorkspace(page);
  await expect(page.getByTestId('workspace-stepper')).toBeVisible();
  await expect(page.getByTestId('workspace-stage-translation-parallel')).toContainText('runs in parallel');
  await expect(page.getByTestId('workspace-stage-voice-parallel')).toContainText('runs in parallel');
  await expect(page.getByTestId('workspace-stage-speech-parallel')).toHaveCount(0);
  await expect(page.getByTestId('workspace-stage-render-parallel')).toHaveCount(0);
  await expect(page.getByTestId('workspace-progress-value')).toContainText('42%');
});

test('deep links navigate to review, quality, exports, and activity @workspace', async ({ page }) => {
  await mockWorkspaceApi(page, newWorld());
  await gotoWorkspace(page);

  await page.getByTestId('workspace-review-link').click();
  await expect(page.getByTestId('page-review')).toBeVisible();
  await page.goBack();
  await expect(page.getByTestId('workspace-page')).toBeVisible();

  await page.getByTestId('workspace-quality-link').click();
  await expect(page).toHaveURL(/\/projects\/prj_1\/quality/);
  await expect(page.getByTestId('page-project-details')).toBeVisible();
  await page.goBack();
  await expect(page.getByTestId('workspace-page')).toBeVisible();

  await page.getByTestId('workspace-output-link').click();
  await expect(page).toHaveURL(/\/projects\/prj_1\/exports/);
  await expect(page.getByTestId('page-project-details')).toBeVisible();
  await page.goBack();
  await expect(page.getByTestId('workspace-page')).toBeVisible();

  await page.getByTestId('workspace-activity-link').click();
  await expect(page).toHaveURL(/\/projects\/prj_1\/activity/);
  await expect(page.getByTestId('page-project-details')).toBeVisible();
});

test('failed-state recovery is visible @workspace', async ({ page }) => {
  const world = newWorld();
  world.phase = 'failed';
  world.stage = 'Translation';
  world.runStatus = 'Failed';
  await mockWorkspaceApi(page, world);
  await gotoWorkspace(page);
  await expect(page.getByTestId('workspace-recovery')).toBeVisible();
  await expect(page.getByTestId('workspace-recovery-retry')).toBeVisible();
  await expect(page.getByTestId('workspace-action-retry')).toBeVisible();
});
