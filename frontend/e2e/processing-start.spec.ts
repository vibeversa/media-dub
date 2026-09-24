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
  permissions: ['project.view', 'project.edit', 'processing.start', 'processing.retry', 'processing.cancel'],
  roles: [],
  userId: 'usr_e2e',
  tenantId: 'tenant_e2e',
};

interface ProcessingWorld {
  quotaRemaining: number;
  activeRun: { runId: string; status: string } | null;
  startPosts: { headers: Record<string, string>; body: unknown }[];
}

function newWorld(): ProcessingWorld {
  return { quotaRemaining: 9, activeRun: null, startPosts: [] };
}

function summaryBody(world: ProcessingWorld): Record<string, unknown> {
  return {
    projectCounts: { active: 1, archived: 0, total: 1 },
    recentOutputs: [],
    storage: { usedBytes: 1, quotaBytes: 100 },
    cost: { monthToDate: 12.5, currency: 'USD' },
    quota: { remaining: world.quotaRemaining, resetsAt: '2026-09-25T00:00:00Z' },
    warnings: [],
    backlog: { pendingReviews: 0, runningJobs: 0 },
  };
}

function listEnvelope(): Record<string, unknown> {
  return {
    items: [
      {
        id: 'prj_9',
        name: 'Retry candidate',
        status: 'Failed',
        sourceLanguage: 'en',
        targetLanguage: 'es',
        createdAt: '2024-01-15T12:00:00Z',
        updatedAt: '2024-01-16T12:00:00Z',
        isArchived: false,
        settingsVersion: 1,
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
}

/**
 * `@processing-start` suite (Task 024). Hermetic: login/identity, dashboard,
 * the project list/detail, speakers, the active-run probe, and the start POST
 * are intercepted, so no backend is required. CORS/preflight handling mirrors
 * `e2e/auth.spec.ts` (dev origin 5173 vs API base 5000).
 */
async function mockProcessingApi(page: Page, world: ProcessingWorld): Promise<void> {
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
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(summaryBody(world)) });
      return;
    }
    if (url.includes('/processing') && !url.includes('/cancel') && request.method() === 'POST') {
      const headers: Record<string, string> = {};
      for (const [key, value] of Object.entries(request.headers())) {
        headers[key.toLowerCase()] = value;
      }
      const raw = request.postData();
      world.startPosts.push({ headers, body: raw !== null ? (JSON.parse(raw) as unknown) : null });
      await route.fulfill({
        status: 202,
        headers: CORS_JSON,
        body: JSON.stringify({ runId: 'run_1', status: 'Pending', configHash: 'cfg_abc' }),
      });
      return;
    }
    if (url.includes('/processing/active') && request.method() === 'GET') {
      if (world.activeRun !== null) {
        await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(world.activeRun) });
      } else {
        await route.fulfill({
          status: 404,
          headers: CORS_JSON,
          body: JSON.stringify({ error: { code: 'NOT_FOUND', message: 'No active run', correlationId: 'c', details: {} } }),
        });
      }
      return;
    }
    if (url.includes('/speakers') && request.method() === 'GET') {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ items: [], page: 1, pageSize: 100, total: 0, hasMore: false }),
      });
      return;
    }
    if (/\/api\/v1\/projects\/prj_[^/]+$/.test(url) && request.method() === 'GET') {
      // Detail reports MediaReady (the list row stays Failed so the retry
      // action renders — a hermetic artifact, documented here).
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({
          id: 'prj_9',
          name: 'Retry candidate',
          status: 'MediaReady',
          sourceLanguage: 'en',
          targetLanguage: 'es',
          settingsVersion: 1,
          configHash: 'cfg_abc',
          createdAt: '2024-01-15T12:00:00Z',
          updatedAt: '2024-01-16T12:00:00Z',
        }),
      });
      return;
    }
    if (url.includes('/api/v1/projects') && request.method() === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(listEnvelope()) });
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

async function gotoProjects(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  // SPA navigation only: a full reload would drop the in-memory session
  // (Task 019 R1) and bounce back to /login.
  await page.getByTestId('dashboard-stat-link').click();
  await expect(page.getByTestId('projects-grid')).toBeVisible();
}

async function openPreflight(page: Page): Promise<void> {
  await page.getByTestId('project-action-retry-prj_9').click();
  await expect(page.getByTestId('preflight-dialog')).toBeVisible();
  await expect(page.getByTestId('preflight-confirm')).toBeVisible();
}

test('dialog flow starts the run and lands on the workspace @processing-start', async ({ page }) => {
  const world = newWorld();
  await mockProcessingApi(page, world);
  await gotoProjects(page);
  await openPreflight(page);

  // Estimate is labeled adjacent to the figure (R2); the confirm states cost.
  await expect(page.getByTestId('preflight-estimate-label')).toContainText(/estimate/i);
  await expect(page.getByTestId('preflight-estimate-amount')).toContainText(/estimate/i);
  await expect(page.getByTestId('preflight-confirm')).toContainText('est.');
  await expect(page.getByTestId('preflight-confirm')).toBeEnabled();

  // Attach the URL waiter before confirming: the ?started=1 entry is
  // transient by design (canonicalized on landing), so assert the transit.
  const landed = page.waitForURL('**/projects/prj_9?started=1');
  await page.getByTestId('preflight-confirm').click();
  await landed;
  await expect(page.getByTestId('page-project-details')).toBeVisible();
  await expect(page.getByText('Run started.')).toBeVisible();

  expect(world.startPosts).toHaveLength(1);
  expect(world.startPosts[0]?.headers['idempotency-key']).toMatch(/^[0-9a-f-]{36}$/);
  expect(world.startPosts[0]?.body).toEqual({ configHash: 'cfg_abc' });
});

test('active run switches the dialog to the conflict state @processing-start', async ({ page }) => {
  const world = newWorld();
  world.activeRun = { runId: 'run_9', status: 'Running' };
  await mockProcessingApi(page, world);
  await gotoProjects(page);
  await openPreflight(page);

  await expect(page.getByTestId('preflight-conflict')).toBeVisible();
  await expect(page.getByTestId('preflight-confirm')).toBeDisabled();
  await expect(page.getByTestId('preflight-block-conflict')).toBeVisible();
  await expect(page.getByTestId('preflight-conflict-workspace')).toHaveAttribute('href', '/projects/prj_9');
  expect(world.startPosts).toHaveLength(0);
});

test('exhausted quota blocks start until an override reason is entered @processing-start', async ({ page }) => {
  const world = newWorld();
  world.quotaRemaining = 0;
  await mockProcessingApi(page, world);
  await gotoProjects(page);
  await openPreflight(page);

  await expect(page.getByTestId('preflight-confirm')).toBeDisabled();
  await expect(page.getByTestId('preflight-block-quota')).toBeVisible();

  await page.getByTestId('preflight-override-input').fill('launch cannot wait');
  await expect(page.getByTestId('preflight-confirm')).toBeEnabled();

  const landed = page.waitForURL('**/projects/prj_9?started=1');
  await page.getByTestId('preflight-confirm').click();
  await landed;
  await expect(page.getByText('Run started.')).toBeVisible();
  expect(world.startPosts).toHaveLength(1);
});
