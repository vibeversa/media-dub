import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';

const CORS_JSON = {
  'Content-Type': 'application/json',
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': '*',
  'Access-Control-Allow-Methods': '*',
};

interface AuthMocks {
  refreshStatus?: number;
  refreshBody?: unknown;
  mePermissions?: readonly string[];
  meStatus?: number;
}

const TOKEN_PAIR = {
  accessToken: 'e2e-access-token',
  refreshToken: 'e2e-refresh-token',
  tokenType: 'Bearer',
  expiresInSeconds: 900,
  userId: 'usr_e2e',
  tenantId: 'tenant_e2e',
};

const ME_BODY = {
  permissions: ['project.view'],
  roles: [],
  userId: 'usr_e2e',
  tenantId: 'tenant_e2e',
};

/**
 * `@auth` suite (Task 019). Hermetic: every `/api/v1` call is intercepted, so
 * no backend is required. The dev server origin (5173) differs from
 * `VITE_API_BASE_URL` (5000), therefore mocked fulfillments carry CORS
 * headers and answer preflights.
 */
async function mockAuthApi(page: Page, mocks?: AuthMocks): Promise<void> {
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
      const status = mocks?.refreshStatus ?? 200;
      const body = mocks?.refreshBody ?? TOKEN_PAIR;
      await route.fulfill({ status, headers: CORS_JSON, body: JSON.stringify(body) });
      return;
    }
    if (url.includes('/auth/logout')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ loggedOut: true }) });
      return;
    }
    if (url.endsWith('/me') && request.method() === 'GET') {
      const status = mocks?.meStatus ?? 200;
      const body =
        status === 200
          ? { ...ME_BODY, permissions: [...(mocks?.mePermissions ?? ME_BODY.permissions)] }
          : { error: { code: 'TOKEN_EXPIRED', message: 'expired', correlationId: 'corr-e2e', details: {} } };
      await route.fulfill({ status, headers: CORS_JSON, body: JSON.stringify(body) });
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

async function storedValues(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const values: string[] = [];
    for (let i = 0; i < window.localStorage.length; i += 1) {
      const key = window.localStorage.key(i);
      if (key !== null) {
        values.push(window.localStorage.getItem(key) ?? '');
      }
    }
    for (let i = 0; i < window.sessionStorage.length; i += 1) {
      const key = window.sessionStorage.key(i);
      if (key !== null) {
        values.push(window.sessionStorage.getItem(key) ?? '');
      }
    }
    return values;
  });
}

test('login happy path @auth', async ({ page }) => {
  await mockAuthApi(page);
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await expect(page.getByTestId('app-shell')).toBeVisible();
  for (const value of await storedValues(page)) {
    expect(value).not.toContain('e2e-access-token');
    expect(value).not.toContain('e2e-refresh-token');
  }
});

test('expiry redirects to login and returns to the destination @auth', async ({ page }) => {
  await mockAuthApi(page, {
    refreshStatus: 401,
    refreshBody: { error: { code: 'TOKEN_EXPIRED', message: 'expired', correlationId: 'c', details: {} } },
  });
  await page.goto('/login?next=%2Fprojects%2Fprj_1');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-project-details')).toBeVisible();

  await page.evaluate(() => {
    window.dispatchEvent(new CustomEvent('auth:expired', { detail: { correlationId: 'c', status: 401 } }));
  });
  await expect(page).toHaveURL(/\/login\?next=/);
  await expect(page.getByTestId('session-expired-dialog')).toBeVisible();

  await loginThroughUi(page);
  await expect(page.getByTestId('page-project-details')).toBeVisible();
});

test('logout clears data and back-button reveals nothing @auth', async ({ page }) => {
  await mockAuthApi(page);
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();

  await page.getByTestId('user-menu-signout').click();
  await expect(page.getByTestId('page-logged-out')).toBeVisible();
  await expect(page.getByTestId('app-shell')).toHaveCount(0);
  for (const value of await storedValues(page)) {
    expect(value).not.toContain('e2e-access-token');
    expect(value).not.toContain('e2e-refresh-token');
  }

  await page.goBack();
  await expect(page.getByTestId('page-login')).toBeVisible();
  await expect(page.getByTestId('page-dashboard')).toHaveCount(0);
});

test('forbidden page carries a request-access hint @auth', async ({ page }) => {
  await mockAuthApi(page, { mePermissions: [] });
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();

  // In-SPA navigation (a full reload would drop the in-memory session by
  // design, R1): push state + popstate so the router resolves client-side.
  await page.evaluate((path: string) => {
    window.history.pushState({}, '', path);
    window.dispatchEvent(new PopStateEvent('popstate'));
  }, '/admin');
  await expect(page.getByTestId('page-forbidden')).toBeVisible();
  await expect(page.getByText(/ask your tenant admin/)).toBeVisible();
});
