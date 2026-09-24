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

function projectList(): Record<string, unknown> {
  return {
    items: [
      {
        id: 'prj_1',
        name: 'Pilot episode',
        status: 'Uploading',
        sourceLanguage: 'en',
        targetLanguage: 'es',
        createdAt: '2024-01-15T12:00:00Z',
        updatedAt: '2024-01-15T12:00:00Z',
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

interface UploadWorld {
  receivedParts: number[];
  partCount: number;
  putDelayMs: number;
  puts: number[];
  postedInitiate: unknown[];
  completeCode: string | null;
}

function newWorld(putDelayMs = 0): UploadWorld {
  return { receivedParts: [], partCount: 0, putDelayMs, puts: [], postedInitiate: [], completeCode: null };
}

/**
 * `@upload` suite (Task 023). Hermetic: login/identity, dashboard, the
 * project list/detail, and the bundle upload surface plus presigned PUTs are
 * intercepted, so no backend is required. CORS/preflight handling mirrors
 * `e2e/auth.spec.ts` (dev origin 5173 vs API base 5000).
 */
async function mockUploadApi(page: Page, world: UploadWorld): Promise<void> {
  await page.route('https://parts.example/**', async (route: Route) => {
    if (world.putDelayMs > 0) {
      await new Promise((resolve) => {
        setTimeout(resolve, world.putDelayMs);
      });
    }
    const part = Number.parseInt(route.request().url().split('/').pop() ?? '0', 10);
    world.puts.push(part);
    if (!world.receivedParts.includes(part)) {
      world.receivedParts.push(part);
    }
    await route.fulfill({ status: 200, headers: CORS_JSON, body: '' });
  });

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
    if (/\/api\/v1\/projects\/prj_1$/.test(url) && request.method() === 'GET') {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ id: 'prj_1', name: 'Pilot episode', status: 'MediaReady', settingsVersion: 1 }),
      });
      return;
    }
    if (url.includes('/api/v1/projects') && request.method() === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(projectList()) });
      return;
    }
    if (url.endsWith('/api/v1/projects/prj_1/uploads') && request.method() === 'POST') {
      const raw = request.postData();
      const parsed: unknown = raw !== null ? (JSON.parse(raw) as unknown) : null;
      world.postedInitiate.push(parsed);
      world.partCount = (parsed as { partCount?: number })?.partCount ?? 0;
      world.receivedParts = [];
      await route.fulfill({
        status: 201,
        headers: CORS_JSON,
        body: JSON.stringify({ id: 'upl_1', status: 'InProgress', receivedParts: [] }),
      });
      return;
    }
    if (url.includes('/uploads/upl_1/parts') && request.method() === 'POST') {
      const raw = request.postData();
      const parsed = (raw !== null ? JSON.parse(raw) : {}) as { partNumber?: number };
      const partNumber = parsed.partNumber ?? 0;
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ partNumber, url: `https://parts.example/${partNumber}` }),
      });
      return;
    }
    if (url.includes('/uploads/upl_1/complete') && request.method() === 'POST') {
      if (world.completeCode !== null) {
        await route.fulfill({
          status: 400,
          headers: CORS_JSON,
          body: JSON.stringify({
            error: { code: world.completeCode, message: 'Rejected by server.', correlationId: 'corr-9', details: {} },
          }),
        });
        return;
      }
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ id: 'upl_1', status: 'Completed', receivedParts: [...world.receivedParts] }),
      });
      return;
    }
    if (url.includes('/uploads/upl_1/abort') && request.method() === 'POST') {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ id: 'upl_1', status: 'Aborted', receivedParts: [] }),
      });
      return;
    }
    if (url.includes('/uploads/upl_1') && request.method() === 'GET') {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ id: 'upl_1', status: 'Completed', receivedParts: [...world.receivedParts] }),
      });
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

async function gotoMediaTab(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  // SPA navigation only: a full reload would drop the in-memory session
  // (Task 019 R1) and bounce back to /login.
  await page.getByTestId('dashboard-stat-link').click();
  await expect(page.getByTestId('projects-grid')).toBeVisible();
  await page.getByTestId('project-open-prj_1').click();
  await expect(page.getByTestId('project-layout')).toBeVisible();
  await page.getByTestId('project-tab-media').click();
  await expect(page.getByTestId('upload-uploader')).toBeVisible();
}

const NINE_MB = 9 * 1024 * 1024;
const SEVENTEEN_MB = 17 * 1024 * 1024;

test('picker upload completes to ready @upload', async ({ page }) => {
  const world = newWorld();
  await mockUploadApi(page, world);
  await gotoMediaTab(page);

  await page
    .getByTestId('upload-file-input')
    .setInputFiles({ name: 'clip.mp4', mimeType: 'video/mp4', buffer: Buffer.alloc(NINE_MB) });
  await expect(page.getByTestId('upload-ready')).toBeVisible();
  await expect(page.getByTestId('upload-open-project')).toHaveAttribute('href', '/projects/prj_1');
  expect(world.postedInitiate).toHaveLength(1);
  expect(world.postedInitiate[0]).toMatchObject({ fileName: 'clip.mp4', sizeBytes: NINE_MB, partCount: 2 });
  expect(world.puts).toHaveLength(2);
});

test('pause stops parts and resume finishes @upload', async ({ page }) => {
  const world = newWorld(1500);
  await mockUploadApi(page, world);
  await gotoMediaTab(page);

  await page
    .getByTestId('upload-file-input')
    .setInputFiles({ name: 'clip.mp4', mimeType: 'video/mp4', buffer: Buffer.alloc(SEVENTEEN_MB) });
  await expect(page.getByTestId('upload-pause')).toBeVisible();
  await page.getByTestId('upload-pause').click();
  await expect(page.getByTestId('upload-resume-prompt')).toBeVisible();
  const putsAtPause = world.puts.length;
  expect(putsAtPause).toBeLessThanOrEqual(3);
  await page.getByTestId('upload-resume').click();
  await expect(page.getByTestId('upload-ready')).toBeVisible();
  // Every part reached the server at least once; aborted in-flight parts are
  // retried (at most twice each), and nothing is lost or duplicated beyond
  // the single retry of an aborted attempt.
  for (const part of [1, 2, 3]) {
    const attempts = world.puts.filter((n) => n === part).length;
    expect(attempts).toBeGreaterThanOrEqual(1);
    expect(attempts).toBeLessThanOrEqual(2);
  }
  expect(world.puts.length).toBeLessThanOrEqual(6);
});

test('refresh keeps completed parts and prompts to re-attach @upload', async ({ page }) => {
  const world = newWorld(10000);
  await mockUploadApi(page, world);
  await gotoMediaTab(page);

  await page
    .getByTestId('upload-file-input')
    .setInputFiles({ name: 'clip.mp4', mimeType: 'video/mp4', buffer: Buffer.alloc(NINE_MB) });
  await expect(page.getByTestId('upload-active')).toBeVisible();

  // Reload drops the in-memory session AND the file bytes; the persisted
  // metadata (with completed parts, if any) survives in localStorage.
  // Post-login the app returns to the preserved destination (the media tab),
  // which shows the re-attach prompt instead of a stuck spinner.
  await page.reload();
  await loginThroughUi(page);
  await expect(page.getByTestId('upload-reattach-prompt')).toBeVisible();
  await expect(page.getByTestId('upload-reattach-prompt')).toContainText('of 2');
});

test('server rejection maps to reason guidance @upload', async ({ page }) => {
  const world = newWorld();
  world.completeCode = 'MEDIA_UNSUPPORTED';
  await mockUploadApi(page, world);
  await gotoMediaTab(page);

  await page
    .getByTestId('upload-file-input')
    .setInputFiles({ name: 'clip.weird', mimeType: 'video/mp4', buffer: Buffer.alloc(NINE_MB) });
  await expect(page.getByTestId('upload-rejected-unsupported')).toBeVisible();
  await expect(page.getByTestId('upload-rejected-unsupported')).toContainText('MP4');
});
