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

interface AdminState {
  mePermissions: string[];
  redrivePosts: { entryId: string; reason: string }[];
}

function initialState(permissions: string[] = ['admin.manage']): AdminState {
  return { mePermissions: permissions, redrivePosts: [] };
}

const USAGE_BODY = {
  correlationId: 'c',
  storageUsedBytes: 1000,
  storageQuotaBytes: 100000,
  monthCostUsd: 12.5,
  projectsTodayRemaining: 9,
  activeRuns: 1,
  pendingReviews: 2,
  totalProjects: 4,
};

const QUOTAS_BODY = {
  correlationId: 'c',
  maxActiveProjects: 10,
  maxProjectsPerDay: 10,
  maxCostPerProject: 50,
  maxCostPerSegment: 5,
  maxSegmentCount: 2000,
  maxStorageBytes: 100000,
  maxConcurrentStagesPerTenant: 4,
};

/**
 * `@admin` suite (Task 036). Hermetic: auth, identity, and every admin read
 * is intercepted. SPA navigation only (no reload — the session is in-memory
 * by design).
 */
async function mockAdminApi(page: Page, state: AdminState): Promise<void> {
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
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ permissions: state.mePermissions, roles: [], userId: 'usr_e2e', tenantId: 'tenant_e2e' }),
      });
      return;
    }
    if (url.includes('/admin/status') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ status: 'ok', time: '2026-09-28T00:00:00Z' }) });
      return;
    }
    if (url.includes('/admin/usage') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(USAGE_BODY) });
      return;
    }
    if (url.includes('/admin/quotas') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(QUOTAS_BODY) });
      return;
    }
    if (url.includes('/admin/provider-health') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify([]) });
      return;
    }
    if (url.includes('/admin/provider-routes') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify([]) });
      return;
    }
    if (url.includes('/admin/diagnostics/queues') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify([{ correlationId: 'c', queue: 'media.prepare', depth: 2 }]) });
      return;
    }
    if (url.includes('/admin/diagnostics/dlq') && method === 'GET' && !url.includes('/redrive') && !url.includes('/discard')) {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({
          correlationId: 'c',
          depth: 2,
          oldestEnqueuedAt: '2026-09-27T00:00:00Z',
          oldestEntryAge: '1d 0h',
          topReasons: [{ code: 'PROVIDER_TIMEOUT', count: 2, actions: ['redrive'] }],
        }),
      });
      return;
    }
    if (url.includes('/admin/diagnostics/dlq/redrive') && method === 'POST') {
      let body: { entryId?: string; reason?: string } = {};
      try {
        body = (await request.postDataJSON()) as { entryId?: string; reason?: string };
      } catch {
        body = {};
      }
      state.redrivePosts.push({ entryId: body.entryId ?? '', reason: body.reason ?? '' });
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ actionId: 'act-e2e-1', timestamp: '2026-09-28T00:00:01Z' }) });
      return;
    }
    if (url.includes('/admin/diagnostics/leases') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ items: [], page: 1, pageSize: 50, total: 0, hasMore: false }) });
      return;
    }
    if (url.includes('/admin/diagnostics/orphans') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ items: [], cursor: null, hasMore: false }) });
      return;
    }
    if (url.includes('/admin/diagnostics/review-backlog') && method === 'GET') {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ correlationId: 'c', totalOpen: 0, byStatus: {}, bySeverity: {}, oldestWaitingAt: null, perProject: [] }),
      });
      return;
    }
    if (url.includes('/admin/tenants') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ items: [] }) });
      return;
    }
    if (url.includes('/admin/users') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ items: [] }) });
      return;
    }
    if (url.includes('/admin/audit-events') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ items: [], hasMore: false }) });
      return;
    }
    if (url.includes('/admin/retention') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ policies: [] }) });
      return;
    }
    if (url.includes('/admin/feature-flags') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ flags: [] }) });
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

async function gotoAdmin(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.getByTestId('nav-admin').click();
  await expect(page.getByTestId('page-admin')).toBeVisible();
  await expect(page.getByTestId('admin-page')).toBeVisible();
}

test('elevated login reaches the ops dashboard @admin', async ({ page }) => {
  const state = initialState(['admin.manage']);
  await mockAdminApi(page, state);
  await gotoAdmin(page);
  await expect(page.getByTestId('admin-ops')).toBeVisible();
  await expect(page.getByTestId('admin-ops-queues-list')).toBeVisible();
  await expect(page.getByTestId('admin-ops-dlq-list')).toBeVisible();
  await expect(page.getByTestId('admin-ops-dlq-depth')).toContainText('Depth: 2');
  await expect(page.getByTestId('admin-usage-quota-available')).toBeVisible();
});

test('destructive flow requires confirm plus reason @admin', async ({ page }) => {
  const state = initialState(['admin.manage']);
  await mockAdminApi(page, state);
  await gotoAdmin(page);
  await expect(page.getByTestId('admin-ops-dlq-list')).toBeVisible();
  await page.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-open').click();
  await expect(page.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-dialog')).toBeVisible();
  await expect(page.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-confirm')).toBeDisabled();
  await page.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-confirm-input').fill('PROVIDER_TIMEOUT');
  await expect(page.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-confirm')).toBeDisabled();
  await page.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-reason-input').fill('short');
  await expect(page.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-confirm')).toBeDisabled();
  await page.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-reason-input').fill('Requeue after the provider recovered');
  await expect(page.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-confirm')).toBeEnabled();
  await page.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-confirm').click();
  await expect(page.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-receipt')).toBeVisible();
  await expect(page.getByTestId('admin-ops-dlq-redrive-PROVIDER_TIMEOUT-0-receipt-id')).toContainText('act-e2e-1');
  expect(state.redrivePosts.length).toBe(1);
  expect(state.redrivePosts[0]?.reason).toBe('Requeue after the provider recovered');
});

test('non-elevated login sees the 403 state @admin', async ({ page }) => {
  const state = initialState([]);
  await mockAdminApi(page, state);
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await expect(page.getByTestId('nav-admin')).toHaveCount(0);
  await page.evaluate((path: string) => {
    window.history.pushState({}, '', path);
    window.dispatchEvent(new PopStateEvent('popstate'));
  }, '/admin');
  await expect(page.getByTestId('page-forbidden')).toBeVisible();
  await expect(page.getByText(/ask your tenant admin/)).toBeVisible();
});
