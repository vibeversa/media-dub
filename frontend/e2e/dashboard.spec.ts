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
  permissions: ['project.view', 'project.edit', 'admin.manage'],
  roles: [],
  userId: 'usr_e2e',
  tenantId: 'tenant_e2e',
};

const FULL_SUMMARY = {
  projectCounts: { active: 2, archived: 1, total: 3 },
  recentOutputs: [
    {
      id: 'out-1',
      projectId: 'prj_1',
      mediaKind: 'audio',
      container: 'mp3',
      createdAt: '2024-01-15T12:00:00Z',
    },
  ],
  storage: { usedBytes: 8_000_000_000, quotaBytes: 100_000_000_000 },
  cost: { monthToDate: 1234.5, currency: 'USD' },
  quota: { remaining: 10, resetsAt: '2026-09-25T00:00:00Z' },
  warnings: [{ code: 'PROJECT_FAILED', message: 'Project failed.', projectId: 'prj_1' }],
  backlog: { pendingReviews: 4, runningJobs: 2 },
};

const EMPTY_SUMMARY = {
  ...FULL_SUMMARY,
  projectCounts: { active: 0, archived: 0, total: 0 },
  recentOutputs: [],
  warnings: [],
};

const WORKSPACE_BODY = {
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
  progress: { percentApproximate: 42, currentStage: 'Transcription', updatedAt: '2024-01-16T12:00:00Z' },
  review: { pendingCount: 2, oldestWaitingAt: '2024-01-16T10:00:00Z' },
  warnings: [],
  output: { state: 'pending', completeness: 42 },
  cost: { runCost: 1.5, monthToDate: 12.5 },
  activity: { recent: [{ id: 'act_1', summary: 'Run started', occurredAt: '2024-01-16T11:00:00Z' }] },
  permissions: { allowedActions: ['project.view', 'project.edit', 'admin.manage'] },
};

/**
 * `@dashboard` suite (Task 020). Hermetic: login/identity plus the single
 * `GET /dashboard/summary` aggregate are intercepted, so no backend is
 * required. CORS/preflight handling mirrors `e2e/auth.spec.ts` because the
 * dev origin (5173) differs from `VITE_API_BASE_URL` (5000).
 */
async function mockDashboardApi(
  page: Page,
  options?: { summary?: unknown; summaryStatus?: number; onSummary?: () => unknown },
): Promise<void> {
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
      const status = options?.summaryStatus ?? 200;
      const body = options?.onSummary?.() ?? options?.summary ?? FULL_SUMMARY;
      await route.fulfill({ status, headers: CORS_JSON, body: JSON.stringify(body) });
      return;
    }
    if (url.includes('/workspace') && request.method() === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(WORKSPACE_BODY) });
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

test('counts render @dashboard', async ({ page }) => {
  await mockDashboardApi(page);
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await expect(page.getByTestId('dashboard-stat-total')).toHaveText('3');
  await expect(page.getByTestId('dashboard-backlog-reviews')).toHaveText('4');
  await expect(page.getByTestId('dashboard-cost-total')).toContainText('1,234');
  await expect(page.getByTestId('dashboard-quota')).toBeVisible();
});

test('drill-down to the project list @dashboard', async ({ page }) => {
  await mockDashboardApi(page);
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('dashboard-stat-card')).toBeVisible();
  await page.getByTestId('dashboard-stat-link').click();
  await expect(page.getByTestId('page-projects')).toBeVisible();
});

test('drill-down to the project workspace @dashboard', async ({ page }) => {
  await mockDashboardApi(page);
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('dashboard-output-out-1')).toBeVisible();
  await page.getByTestId('dashboard-output-link-out-1').click();
  await expect(page.getByTestId('page-project-details')).toBeVisible();
});

test('empty-tenant CTA routes to the creation entry point @dashboard', async ({ page }) => {
  await mockDashboardApi(page, { summary: EMPTY_SUMMARY });
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('dashboard-empty')).toBeVisible();
  await expect(page.getByTestId('dashboard-grid')).toHaveCount(0);
  await page.getByTestId('dashboard-empty-cta').click();
  await expect(page.getByTestId('page-project-create')).toBeVisible();
});

test('partial state isolates the failed card with retry @dashboard', async ({ page }) => {
  let summary: unknown = { ...FULL_SUMMARY, storage: undefined };
  await mockDashboardApi(page, { onSummary: () => summary });
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('dashboard-stat-card')).toBeVisible();
  const storageCard = page.getByTestId('dashboard-storage-cost');
  await expect(storageCard).toBeVisible();
  await expect(storageCard).toHaveAttribute('role', 'alert');
  await expect(page.getByTestId('dashboard-backlog')).toBeVisible();

  summary = FULL_SUMMARY;
  await page.getByTestId('dashboard-storage-retry').click();
  await expect(page.getByTestId('dashboard-storage-used')).toBeVisible();
});
