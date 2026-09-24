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
    'export.create',
  ],
  roles: [],
  userId: 'usr_e2e',
  tenantId: 'tenant_e2e',
};

const SUMMARY = {
  projectCounts: { active: 3, archived: 0, total: 3 },
  recentOutputs: [],
  storage: { usedBytes: 1, quotaBytes: 100 },
  cost: { monthToDate: 0, currency: 'USD' },
  quota: { remaining: 9, resetsAt: '2026-09-25T00:00:00Z' },
  warnings: [],
  backlog: { pendingReviews: 0, runningJobs: 0 },
};

interface ProjectRow {
  id: string;
  name: string;
  status: string;
}

let items: ProjectRow[];

function row(id: string, name: string, status: string): ProjectRow {
  return { id, name, status };
}

function toEnvelope(url: string): Record<string, unknown> {
  const parsed = new URL(url);
  const page = Number.parseInt(parsed.searchParams.get('page') ?? '1', 10);
  const pageSize = Number.parseInt(parsed.searchParams.get('pageSize') ?? '20', 10);
  return {
    items: items.map((item) => ({
      id: item.id,
      name: item.name,
      status: item.status,
      sourceLanguage: 'en',
      targetLanguage: 'es',
      createdAt: '2024-01-15T12:00:00Z',
      updatedAt: '2024-01-16T12:00:00Z',
      isArchived: false,
      settingsVersion: 1,
    })),
    page,
    pageSize,
    total: items.length,
    sort: 'createdAt',
    sortDir: 'desc',
    hasMore: false,
    clamped: false,
  };
}

/**
 * `@projects` suite (Task 021). Hermetic: login/identity, the dashboard
 * aggregate (login lands there first), and the project list + row mutations
 * are intercepted, so no backend is required. CORS/preflight handling
 * mirrors `e2e/auth.spec.ts` (dev origin 5173 vs API base 5000).
 */
async function mockProjectsApi(page: Page): Promise<{ requests: string[] }> {
  const requests: string[] = [];
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
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(SUMMARY) });
      return;
    }
    if (request.method() === 'DELETE' && /\/projects\/prj_/.test(url)) {
      const id = url.split('/').pop()?.split('?')[0] ?? '';
      items = items.filter((i) => i.id !== id);
      await route.fulfill({ status: 202, headers: CORS_JSON, body: JSON.stringify({ id, deleted: true }) });
      return;
    }
    if (request.method() === 'POST' && /\/projects\/prj_/.test(url)) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ ok: true }) });
      return;
    }
    if (url.includes('/api/v1/projects') && request.method() === 'GET') {
      requests.push(url);
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(toEnvelope(url)) });
      return;
    }
    await route.fulfill({
      status: 404,
      headers: CORS_JSON,
      body: JSON.stringify({ error: { code: 'NOT_FOUND', message: 'No e2e mock', correlationId: 'c', details: {} } }),
    });
  });
  return { requests };
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

test.beforeEach(() => {
  items = [row('prj_1', 'Pilot episode', 'Completed'), row('prj_2', 'Trailer', 'Processing')];
});

test('filter syncs to URL and paginates server-side @projects', async ({ page }) => {
  items = Array.from({ length: 45 }, (_, i) => row(`prj_${i + 1}`, `Project ${i + 1}`, 'Completed'));
  const { requests } = await mockProjectsApi(page);
  await gotoProjects(page);

  await page.getByTestId('filter-status').selectOption('Failed');
  await expect.poll(() => page.url()).toContain('status=Failed');
  await expect
    .poll(() => requests.some((url) => url.includes('status=Failed')))
    .toBe(true);

  await page.getByTestId('filters-clear').click();
  await page.getByRole('button', { name: 'Next' }).click();
  await expect.poll(() => page.url()).toContain('page=2');
  await expect
    .poll(() => requests.some((url) => url.includes('page=2')))
    .toBe(true);
  await expect(page.getByTestId('projects-pagination')).toContainText('Page 2 of 3');
});

test('row opens the workspace @projects', async ({ page }) => {
  await mockProjectsApi(page);
  await gotoProjects(page);
  await page.getByTestId('project-open-prj_1').click();
  await expect(page.getByTestId('page-project-details')).toBeVisible();
});

test('valid-actions-only behavior @projects', async ({ page }) => {
  await mockProjectsApi(page);
  await gotoProjects(page);
  await expect(page.getByTestId('project-action-cancel-prj_2')).toBeVisible();
  await expect(page.getByTestId('project-action-delete-prj_2')).toHaveCount(0);
  await expect(page.getByTestId('project-action-delete-prj_1')).toBeVisible();
  await expect(page.getByTestId('project-action-export-prj_1')).toBeVisible();
});

test('delete requires typed name confirmation @projects', async ({ page }) => {
  await mockProjectsApi(page);
  await gotoProjects(page);
  await page.getByTestId('project-action-delete-prj_1').click();
  await expect(page.getByTestId('projects-delete-dialog')).toBeVisible();
  await expect(page.getByTestId('delete-confirm-button')).toBeDisabled();

  await page.getByTestId('delete-confirm-name').fill('Wrong name');
  await expect(page.getByTestId('delete-confirm-button')).toBeDisabled();

  await page.getByTestId('delete-confirm-name').fill('Pilot episode');
  await expect(page.getByTestId('delete-confirm-button')).toBeEnabled();
  await page.getByTestId('delete-confirm-button').click();
  await expect(page.getByText('Project deleted.')).toBeVisible();
  await expect(page.getByTestId('project-actions-prj_1')).toHaveCount(0);
  await expect(page.getByTestId('project-actions-prj_2')).toBeVisible();
});
