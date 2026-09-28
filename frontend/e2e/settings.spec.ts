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
  permissions: ['project.view', 'project.edit'],
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

interface SettingsState {
  prefs: Record<string, string>;
}

function initialState(): SettingsState {
  return {
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

/**
 * `@settings` suite (Task 035B). Hermetic: auth, identity, preferences, and
 * the dashboard aggregate are intercepted. SPA navigation only (no reload —
 * the session is in-memory by design, so persistence is asserted via
 * dashboard → settings round-trips backed by the mutable prefs mock).
 */
async function mockSettingsApi(page: Page, state: SettingsState): Promise<void> {
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

async function gotoSettings(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.getByTestId('nav-settings').click();
  await expect(page.getByTestId('page-settings')).toBeVisible();
  await expect(page.getByTestId('settings-page')).toBeVisible();
}

test('change locale and theme persists across navigation @settings', async ({ page }) => {
  const state = initialState();
  await mockSettingsApi(page, state);
  await gotoSettings(page);
  await expect(page.getByTestId('settings-field-locale')).toBeVisible();
  await expect(page.getByTestId('settings-field-locale')).toHaveValue('en');
  await page.getByTestId('settings-field-locale').selectOption('ar');
  await expect(page.getByTestId('settings-dirty-bar')).toBeVisible();
  await page.getByTestId('settings-field-theme-dark').click();
  await page.getByTestId('settings-save').click();
  await expect(page.getByTestId('settings-locale-note')).toBeVisible();
  expect(state.prefs['locale']).toBe('ar');
  expect(state.prefs['theme']).toBe('dark');
  await expect(page.locator('html')).toHaveAttribute('lang', 'ar');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await page.getByTestId('nav-dashboard').click();
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.getByTestId('nav-settings').click();
  await expect(page.getByTestId('settings-page')).toBeVisible();
  await expect(page.getByTestId('settings-field-locale')).toHaveValue('ar');
  await expect(page.getByTestId('settings-field-theme-dark')).toBeChecked();
});

test('dirty guard offers save discard cancel @settings', async ({ page }) => {
  const state = initialState();
  await mockSettingsApi(page, state);
  await gotoSettings(page);
  await expect(page.getByTestId('settings-field-locale')).toBeVisible();
  await page.getByTestId('settings-field-locale').selectOption('ru');
  await expect(page.getByTestId('settings-dirty-bar')).toBeVisible();
  await page.getByTestId('settings-back-link').click();
  await expect(page.getByTestId('settings-dirty-dialog')).toBeVisible();
  await page.getByTestId('settings-dirty-cancel').click();
  await expect(page.getByTestId('settings-dirty-dialog')).toHaveCount(0);
  await expect(page.getByTestId('settings-field-locale')).toHaveValue('ru');
  await page.getByTestId('settings-back-link').click();
  await expect(page.getByTestId('settings-dirty-dialog')).toBeVisible();
  await page.getByTestId('settings-dirty-discard').click();
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.getByTestId('nav-settings').click();
  await expect(page.getByTestId('settings-field-locale')).toHaveValue('en');
  expect(state.prefs['locale']).toBe('en');
});

test('timezone select falls back to UTC with warning @settings', async ({ page }) => {
  const state = initialState();
  state.prefs = { ...state.prefs, timezone: 'Mars/Olympus' };
  await mockSettingsApi(page, state);
  await gotoSettings(page);
  await expect(page.getByTestId('settings-timezone-warning')).toBeVisible();
  await expect(page.getByTestId('settings-field-timezone')).toHaveValue('UTC');
  await page.getByTestId('settings-field-timezone').selectOption('America/New_York');
  await page.getByTestId('settings-save').click();
  expect(state.prefs['timezone']).toBe('America/New_York');
  await expect(page.getByTestId('settings-timezone-note')).toBeVisible();
});
