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
  permissions: ['project.view', 'project.edit', 'processing.start'],
  roles: [],
  userId: 'usr_e2e',
  tenantId: 'tenant_e2e',
};

const SUMMARY = {
  projectCounts: { active: 1, archived: 0, total: 1 },
  recentOutputs: [],
  storage: { usedBytes: 1, quotaBytes: 100 },
  cost: { monthToDate: 0, currency: 'USD' },
  quota: { remaining: 9, resetsAt: '2026-09-25T00:00:00Z' },
  warnings: [],
  backlog: { pendingReviews: 0, runningJobs: 0 },
};

function listEnvelope(): Record<string, unknown> {
  return {
    items: [],
    page: 1,
    pageSize: 20,
    total: 0,
    sort: 'createdAt',
    sortDir: 'desc',
    hasMore: false,
    clamped: false,
  };
}

function createdProject(name: string): Record<string, unknown> {
  return {
    id: 'prj_new',
    name,
    status: 'Created',
    sourceLanguage: 'en',
    targetLanguage: 'es',
    settingsVersion: 1,
    configHash: 'server-hash',
    createdAt: '2024-01-15T12:00:00Z',
    updatedAt: '2024-01-15T12:00:00Z',
  };
}

/**
 * `@project-create` suite (Task 022). Hermetic: login/identity, the
 * dashboard aggregate (login lands there first), and `POST /projects` are
 * intercepted, so no backend is required. CORS/preflight handling mirrors
 * `e2e/auth.spec.ts` (dev origin 5173 vs API base 5000).
 */
async function mockCreateApi(
  page: Page,
  options?: { status?: number; body?: unknown; onCreate?: (body: unknown) => void },
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
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(SUMMARY) });
      return;
    }
    if (url.endsWith('/api/v1/projects') && request.method() === 'POST') {
      const raw = request.postData();
      const parsed: unknown = raw !== null ? (JSON.parse(raw) as unknown) : null;
      options?.onCreate?.(parsed);
      const status = options?.status ?? 201;
      const body = options?.body ?? createdProject((parsed as { name?: string })?.name ?? 'Pilot episode');
      await route.fulfill({ status, headers: CORS_JSON, body: JSON.stringify(body) });
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

async function gotoWizard(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  // SPA navigation only: a full reload would drop the in-memory session
  // (Task 019 R1) and bounce back to /login.
  await page.getByTestId('dashboard-stat-link').click();
  await expect(page.getByTestId('page-projects')).toBeVisible();
  await page.getByTestId('projects-new').click();
  await expect(page.getByTestId('page-project-create')).toBeVisible();
}

async function fillToReview(page: Page, name: string): Promise<void> {
  await page.getByTestId('wizard-name').fill(name);
  await page.getByTestId('wizard-next').click();
  await expect(page.getByTestId('wizard-step-language')).toBeVisible();
  await page.getByTestId('wizard-next').click();
  await expect(page.getByTestId('wizard-step-settings')).toBeVisible();
  await page.getByTestId('wizard-next').click();
  await expect(page.getByTestId('wizard-step-upload')).toBeVisible();
  await page.getByTestId('wizard-next').click();
  await expect(page.getByTestId('wizard-step-review')).toBeVisible();
}

test('full wizard creates and opens the workspace @project-create', async ({ page }) => {
  let posted: unknown = null;
  await mockCreateApi(page, {
    onCreate: (body) => {
      posted = body;
    },
  });
  await gotoWizard(page);
  await fillToReview(page, 'Pilot episode');

  await expect(page.getByTestId('wizard-config-version')).toContainText('1');
  await expect(page.getByTestId('wizard-config-hash')).not.toBeEmpty();
  await expect(page.getByTestId('wizard-summary-name')).toContainText('Pilot episode');
  await expect(page.getByTestId('wizard-immutable-repeat')).toContainText('es');

  await page.getByTestId('wizard-start').click();
  // The media tab is a Task 025 placeholder: the details shell renders while
  // the URL carries the deferred-upload `/media` deep link.
  await expect(page.getByTestId('page-project-details')).toBeVisible();
  expect(page.url()).toContain('/projects/prj_new/media');
  expect((posted as { name?: string })?.name).toBe('Pilot episode');
  expect((posted as { targetLanguage?: string })?.targetLanguage).toBe('es');
});

test('empty name blocks with an inline error @project-create', async ({ page }) => {
  await mockCreateApi(page);
  await gotoWizard(page);
  await expect(page.getByTestId('wizard-step-basics')).toBeVisible();
  await page.getByTestId('wizard-next').click();
  await expect(page.getByTestId('wizard-field-errors')).toBeVisible();
  await expect(page.getByTestId('wizard-step-language')).toHaveCount(0);
});

test('upload-later defers to the media tab @project-create', async ({ page }) => {
  await mockCreateApi(page);
  await gotoWizard(page);
  await fillToReview(page, 'Deferred media');
  await expect(page.getByTestId('wizard-summary-upload')).toContainText('Upload later');
  await page.getByTestId('wizard-start').click();
  await expect(page.getByTestId('page-project-details')).toBeVisible();
  expect(page.url()).toContain('/projects/prj_new/media');
});
