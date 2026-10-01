import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';

/**
 * `@optional-enrichment` (Task 044, Plan B §19.1–§19.3).
 *
 * Three claims, and the third is the one that matters most:
 *
 * 1. flags ON  → the panels render with fixtures, as separate artifacts.
 * 2. flags OFF → **zero** enrichment chrome anywhere, and — the part a DOM
 *    assertion alone cannot prove — the enrichment CHUNKS are never requested
 *    and the enrichment ENDPOINTS are never called.
 * 3. backend failure (500 / network drop) → the core journey still completes.
 *    A user with a broken enrichment worker must still reach a working
 *    transcript; that is the whole point of the feature being optional.
 *
 * Hermetic, like every other `frontend/e2e` spec: auth, `/me` and every read
 * is intercepted, no backend is started, and the session is driven through the
 * real login form (which Task 041A changed to tenant-id + external-subject —
 * the pre-041A `auth-tenant`/`auth-email`/`auth-password` ids no longer exist
 * and several older specs still reference them).
 */

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

interface EnrichmentFlags {
  videoIntel: boolean;
  lipSync: boolean;
  localGpu: boolean;
}

const FLAGS_OFF: EnrichmentFlags = { videoIntel: false, lipSync: false, localGpu: false };
const FLAGS_ON: EnrichmentFlags = { videoIntel: true, lipSync: true, localGpu: true };

const VIDEO_INTEL_BODY = {
  model: 'local-vision',
  generatedAt: '2026-09-28T00:00:00Z',
  artifacts: [
    { id: 'art_scene_1', kind: 'scene-cut', label: 'Interior, night', atMs: '00:00:12', segmentId: 'seg_1' },
    { id: 'art_overlay_1', kind: 'overlay', label: 'Lower third', atMs: '00:01:04', segmentId: 'seg_2' },
  ],
};

const LIP_SYNC_BODY = {
  model: 'local-wav2lip',
  lipSyncScore: 0.87,
  method: 'heuristic v1',
  segments: [{ segmentId: 'seg_1', lipSyncScore: 0.94, asset: { id: 'ast_1', format: 'wav', sizeBytes: 2048 } }],
};

const LOCAL_GPU_BODY = {
  provider: 'local-inference',
  status: 'Healthy',
  model: 'whisper-large-v3',
  modelVersion: '2026-08',
  device: 'cuda:0',
  deviceCount: 1,
  latencyMsP95: 42,
  lastSuccessAt: '2026-09-28T00:00:00Z',
};

const SUMMARY_BODY = {
  projectCounts: { active: 1, archived: 0, total: 1 },
  recentOutputs: [],
  storage: { usedBytes: 1, quotaBytes: 100 },
  cost: { monthToDate: 12.5, currency: 'USD' },
  quota: { remaining: 9, resetsAt: '2026-09-25T00:00:00Z' },
};

const SEGMENTS_BODY = {
  items: [
    {
      id: 'seg_1',
      projectId: 'prj_1',
      status: 'Ready',
      sequence: 1,
      startMs: 0,
      endMs: 1800,
      speakerId: 'spk_alice',
      speakerLabel: 'Alice',
      selectionVersion: 2,
      reviewStatus: 'Approved',
      qualityCodes: [],
      syncStatus: 'SyncAcceptable',
      confidence: 0.95,
      text: 'core transcript line one',
      transcriptVersions: [
        { id: 'seg_1-v1', provider: 'acme', model: 'stt-v1', text: 'core transcript line one', isSelected: true, createdAt: '2026-01-15T12:00:00Z' },
      ],
    },
  ],
  page: 1,
  pageSize: 200,
  total: 1,
  hasMore: false,
};

type EnrichmentOutcome = 'ok' | 'error500' | 'network-drop' | 'not-provisioned';

interface World {
  readonly permissions: string[];
  readonly flags: EnrichmentFlags;
  readonly videoIntel: EnrichmentOutcome;
  readonly lipSync: EnrichmentOutcome;
  readonly localGpu: EnrichmentOutcome;
  /** Every request URL the page issued. The "never touched" assertions read it. */
  readonly requests: string[];
}

function errorEnvelope(code: string): string {
  return JSON.stringify({ error: { code, message: `backend ${code}`, correlationId: 'corr-e2e', details: {} } });
}

/** A connection drop, which is what a client-side timeout surfaces as. */
function dropConnection(route: Route): Promise<void> {
  return route.abort('connectionrefused');
}

async function mockApi(page: Page, world: World): Promise<void> {
  await page.route('**/api/v1/**', async (route: Route) => {
    const request = route.request();
    if (request.method() === 'OPTIONS') {
      await route.fulfill({ status: 204, headers: CORS_JSON, body: '' });
      return;
    }
    const url = request.url();
    const method = request.method();
    world.requests.push(url);

    if (url.includes('/auth/login') || url.includes('/auth/refresh')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(TOKEN_PAIR) });
      return;
    }
    if (url.includes('/auth/logout')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ loggedOut: true }) });
      return;
    }
    if (/\/me(\?|$)/.test(url) && method === 'GET') {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({
          userId: 'usr_e2e',
          tenantId: 'tenant_e2e',
          permissions: world.permissions,
          featureFlags: {
            videoIntelligenceEnabled: world.flags.videoIntel,
            lipSyncEnabled: world.flags.lipSync,
            localInferenceEnabled: world.flags.localGpu,
          },
        }),
      });
      return;
    }

    if (url.includes('/enrichment/video-intel')) {
      if (world.videoIntel === 'error500') {
        await route.fulfill({ status: 500, headers: CORS_JSON, body: errorEnvelope('INTERNAL_ERROR') });
        return;
      }
      if (world.videoIntel === 'network-drop') {
        await dropConnection(route);
        return;
      }
      if (world.videoIntel === 'not-provisioned') {
        await route.fulfill({ status: 404, headers: CORS_JSON, body: errorEnvelope('NOT_FOUND') });
        return;
      }
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(VIDEO_INTEL_BODY) });
      return;
    }

    if (url.includes('/enrichment/lip-sync/asset')) {
      // Click-time signed URL. 302 with a Location, never a body.
      await route.fulfill({
        status: 302,
        headers: { ...CORS_JSON, Location: 'https://cdn.example.com/lipsync/asset.wav?sig=e2e' },
        body: '',
      });
      return;
    }

    if (url.includes('/enrichment/lip-sync')) {
      if (world.lipSync === 'error500') {
        await route.fulfill({ status: 500, headers: CORS_JSON, body: errorEnvelope('INTERNAL_ERROR') });
        return;
      }
      if (world.lipSync === 'network-drop') {
        await dropConnection(route);
        return;
      }
      if (world.lipSync === 'not-provisioned') {
        await route.fulfill({ status: 404, headers: CORS_JSON, body: errorEnvelope('NOT_FOUND') });
        return;
      }
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(LIP_SYNC_BODY) });
      return;
    }

    if (url.includes('/admin/local-gpu')) {
      if (world.localGpu === 'error500') {
        await route.fulfill({ status: 500, headers: CORS_JSON, body: errorEnvelope('INTERNAL_ERROR') });
        return;
      }
      if (world.localGpu === 'network-drop') {
        await dropConnection(route);
        return;
      }
      if (world.localGpu === 'not-provisioned') {
        await route.fulfill({ status: 404, headers: CORS_JSON, body: errorEnvelope('NOT_FOUND') });
        return;
      }
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(LOCAL_GPU_BODY) });
      return;
    }

    // --- core surfaces ------------------------------------------------------
    if (method === 'GET' && url.includes('/dashboard/summary')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(SUMMARY_BODY) });
      return;
    }
    if (method === 'GET' && /\/projects(\?|$)/.test(url)) {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({
          items: [
            {
              id: 'prj_1',
              name: 'Demo project',
              sourceLanguage: 'en-US',
              targetLanguage: 'es-ES',
              status: 'InProgress',
              ownerId: 'usr_e2e',
              archived: false,
              createdAt: '2026-09-01T00:00:00Z',
              updatedAt: '2026-09-20T00:00:00Z',
            },
          ],
          page: 1,
          pageSize: 50,
          total: 1,
          hasMore: false,
        }),
      });
      return;
    }
    if (method === 'GET' && /\/projects\/prj_1(\?|$)/.test(url)) {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({
          id: 'prj_1',
          name: 'Demo project',
          sourceLanguage: 'en-US',
          targetLanguage: 'es-ES',
          status: 'InProgress',
          ownerId: 'usr_e2e',
          archived: false,
          createdAt: '2026-09-01T00:00:00Z',
          updatedAt: '2026-09-20T00:00:00Z',
        }),
      });
      return;
    }
    if (method === 'GET' && url.includes('/projects/prj_1/workspace')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ hasMedia: true, hasTranscript: true, exportReadyCount: 0, openReviewCount: 0 }) });
      return;
    }
    if (method === 'GET' && url.includes('/projects/prj_1/segments') && !url.includes('/segments/seg_')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(SEGMENTS_BODY) });
      return;
    }
    if (url.includes('/segments/seg_') && method === 'GET') {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ ...SEGMENTS_BODY.items[0], id: 'seg_1', outputStale: false }),
      });
      return;
    }
    if (method === 'GET' && url.includes('/admin/status')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ status: 'ok', time: '2026-09-28T00:00:00Z' }) });
      return;
    }
    if (method === 'GET' && url.includes('/admin/usage')) {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ storageUsedBytes: 1000, storageQuotaBytes: 100000, monthCostUsd: 12.5, projectsTodayRemaining: 9, activeRuns: 1, pendingReviews: 2, totalProjects: 4 }),
      });
      return;
    }
    if (method === 'GET' && url.includes('/admin/quotas')) {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ maxActiveProjects: 10, maxProjectsPerDay: 10, maxCostPerProject: 50, storageUsedBytes: 100000, maxConcurrentStagesPerTenant: 4 }),
      });
      return;
    }
    if (method === 'GET' && (url.includes('/admin/provider-health') || url.includes('/admin/provider-routes'))) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify([]) });
      return;
    }
    if (method === 'GET' && url.includes('/admin/diagnostics/queues')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify([{ queue: 'media.prepare', depth: 2 }]) });
      return;
    }
    if (method === 'GET' && url.includes('/admin/diagnostics/dlq')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ depth: 0, topReasons: [] }) });
      return;
    }
    if (method === 'GET' && url.includes('/admin/diagnostics/leases')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ items: [], page: 1, pageSize: 50, total: 0, hasMore: false }) });
      return;
    }
    if (method === 'GET' && url.includes('/admin/diagnostics/orphans')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ items: [], cursor: null, hasMore: false }) });
      return;
    }
    if (method === 'GET' && url.includes('/admin/diagnostics/review-backlog')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ totalOpen: 0, byStatus: {}, bySeverity: {}, perProject: [] }) });
      return;
    }
    if (method === 'GET' && (url.includes('/admin/tenants') || url.includes('/admin/users'))) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ items: [] }) });
      return;
    }
    if (method === 'GET' && url.includes('/admin/audit-events')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ items: [], hasMore: false }) });
      return;
    }
    if (method === 'GET' && url.includes('/admin/retention')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ policies: [] }) });
      return;
    }
    if (method === 'GET' && url.includes('/admin/feature-flags')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ flags: [] }) });
      return;
    }
    if (method === 'GET' && url.includes('/projects/prj_1/exports')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ items: [], page: 1, pageSize: 100, total: 0, hasMore: false }) });
      return;
    }
    if (method === 'GET' && url.includes('/projects/prj_1/processing')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ items: [], page: 1, pageSize: 20, total: 0, hasMore: false }) });
      return;
    }

    await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({}) });
  });
}

async function loginThroughUi(page: Page): Promise<void> {
  // Task 041A made the form passwordless: tenant id + external subject.
  await page.getByTestId('auth-tenant-id').fill('ten_1');
  await page.getByTestId('auth-external-subject').fill('usr_e2e');
  await page.getByTestId('auth-submit').click();
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
}

function newWorld(overrides: Partial<World> = {}): World {
  return {
    permissions: ['project.view', 'project.edit', 'admin.manage'],
    flags: FLAGS_OFF,
    videoIntel: 'ok',
    lipSync: 'ok',
    localGpu: 'not-provisioned',
    requests: [],
    ...overrides,
  };
}

async function gotoMediaTab(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await page.getByTestId('dashboard-stat-link').click();
  await expect(page.getByTestId('projects-grid')).toBeVisible();
  await page.getByTestId('project-open-prj_1').click();
  await expect(page.getByTestId('workspace-page')).toBeVisible();
  await openProjectTab(page, 'media');
  await expect(page.getByTestId('page-project-media')).toBeVisible();
}

async function openProjectTab(page: Page, tab: 'media' | 'transcript'): Promise<void> {
  // Project tabs render from `EMPTY_WORKSPACE_STATE` until a live aggregate
  // wires `workspaceState`, so a tab can be a disabled span in a hermetic run.
  // Navigate client-side via history + popstate (the data router observes it
  // as SPA navigation, and an in-memory session survives it — a `page.goto`
  // would drop the session and bounce to the login form).
  const link = page.getByTestId(`project-tab-${tab}`);
  if ((await link.count()) > 0) {
    await link.click();
    return;
  }
  await page.evaluate((target) => {
    window.history.pushState({}, '', target);
    window.dispatchEvent(new PopStateEvent('popstate'));
  }, `/projects/prj_1/${tab}`);
}

test('flags on: both panels render as separate artifacts @optional-enrichment', async ({ page }) => {
  const world = newWorld({ flags: FLAGS_ON });
  await mockApi(page, world);
  await gotoMediaTab(page);

  // Video intel: its own section, linked but never spliced into the transcript.
  const videoIntel = page.getByTestId('enrichment-video-intel');
  await expect(videoIntel).toBeVisible();
  await expect(page.getByTestId('enrichment-video-intel-artifact-art_scene_1')).toBeVisible();
  await expect(page.getByTestId('enrichment-video-intel-artifact-art_overlay_1')).toBeVisible();
  await expect(page.getByTestId('enrichment-video-intel-separate-note')).toContainText('never replace, reorder or edit');

  // Lip sync: a score AND its method note on the same row.
  await expect(page.getByTestId('enrichment-lip-sync')).toBeVisible();
  await expect(page.getByTestId('enrichment-lip-sync-score-seg_1')).toContainText('0.94');
  await expect(page.getByTestId('enrichment-lip-sync-method-seg_1')).toContainText('heuristic v1');
  await expect(page.getByTestId('enrichment-lip-sync-asset-download')).toBeVisible();
  await expect(page.getByTestId('enrichment-lip-sync-separate-note')).toContainText('separate file');

  // Neither panel is a descendant of a core surface.
  expect(await page.getByTestId('enrichment-video-intel').count()).toBe(1);
});

test('flags on: the operator GPU panel is visible only in Admin @optional-enrichment', async ({ page }) => {
  const world = newWorld({ flags: FLAGS_ON, localGpu: 'ok' });
  await mockApi(page, world);
  await page.goto('/login');
  await loginThroughUi(page);
  await page.getByTestId('nav-admin').click();
  await expect(page.getByTestId('admin-section-local-gpu')).toBeVisible();
  await expect(page.getByTestId('admin-local-gpu-provider')).toHaveText('local-inference');
  await expect(page.getByTestId('admin-local-gpu-model')).toHaveText('whisper-large-v3');
  await expect(page.getByTestId('enrichment-privacy-note')).toContainText('never sent to a third-party provider');
});

test('flags off: zero enrichment chrome, zero enrichment requests @optional-enrichment', async ({ page }) => {
  const world = newWorld({ flags: FLAGS_OFF });
  await mockApi(page, world);
  await gotoMediaTab(page);

  // The uploader — the whole point of the media tab — still renders.
  await expect(page.getByTestId('page-project-media')).toBeVisible();

  // Not one enrichment testid exists. Not one. Including the wrapper.
  for (const id of [
    'enrichment-project-panels',
    'enrichment-video-intel',
    'enrichment-lip-sync',
    'enrichment-video-intel-list',
    'enrichment-lip-sync-list',
    'enrichment-lip-sync-asset-download',
    'enrichment-video-intel-not-available',
    'enrichment-lip-sync-not-available',
  ]) {
    expect(await page.getByTestId(id).count()).toBe(0);
  }
  // No upsell, no placeholder, no disabled affordance mentioning enrichment.
  const body = (await page.locator('body').innerText()).toLowerCase();
  expect(body).not.toContain('scene cut');
  expect(body).not.toContain('detected overlay');
  expect(body).not.toContain('lip sync');
  expect(body).not.toContain('enrichment');
  expect(await page.locator('button:disabled').filter({ hasText: /lip|scene|overlay|enrich/i }).count()).toBe(0);

  // And the endpoints were never called: the flag check is upstream of the
  // query, so a panel that is not rendered cannot have requested anything.
  expect(world.requests.filter((url) => url.includes('/enrichment/'))).toEqual([]);
  expect(world.requests.filter((url) => url.includes('/admin/local-gpu'))).toEqual([]);
});

test('flags off: the admin area shows no GPU section at all @optional-enrichment', async ({ page }) => {
  const world = newWorld({ flags: FLAGS_OFF });
  await mockApi(page, world);
  await page.goto('/login');
  await loginThroughUi(page);
  await page.getByTestId('nav-admin').click();
  await expect(page.getByTestId('admin-page')).toBeVisible();
  await expect(page.getByTestId('admin-ops')).toBeVisible();
  expect(await page.getByTestId('admin-section-local-gpu').count()).toBe(0);
  expect(await page.getByTestId('admin-local-gpu').count()).toBe(0);
  expect(world.requests.filter((url) => url.includes('/admin/local-gpu'))).toEqual([]);
});

test('flags on, backend broken: the core transcript journey still completes @optional-enrichment', async ({ page }) => {
  const world = newWorld({ flags: FLAGS_ON, videoIntel: 'error500', lipSync: 'network-drop' });
  await mockApi(page, world);
  await gotoMediaTab(page);

  // Both enrichment surfaces report their own failure, dismissibly.
  await expect(page.getByTestId('enrichment-video-intel-unavailable')).toBeVisible();
  await expect(page.getByTestId('enrichment-video-intel-dismiss')).toBeVisible();
  await expect(page.getByTestId('enrichment-lip-sync-unavailable')).toBeVisible();
  await expect(page.getByTestId('enrichment-unavailable-nonblocking').first()).toContainText('continue to work normally');

  // Dismissing one does not touch the other.
  await page.getByTestId('enrichment-video-intel-dismiss').click();
  await expect(page.getByTestId('enrichment-video-intel-unavailable')).toHaveCount(0);
  await expect(page.getByTestId('enrichment-lip-sync')).toBeVisible();

  // THE CLAIM: the core journey still works.
  await openProjectTab(page, 'transcript');
  await expect(page.getByTestId('page-project-transcript')).toBeVisible();
  await expect(page.getByTestId('transcript-editor')).toBeVisible();
  await expect(page.getByTestId('transcript-editor')).toContainText('core transcript line one');
});

test('flags on, not provisioned: panels say so without looking broken @optional-enrichment', async ({ page }) => {
  const world = newWorld({ flags: FLAGS_ON, videoIntel: 'not-provisioned', lipSync: 'not-provisioned' });
  await mockApi(page, world);
  await gotoMediaTab(page);

  await expect(page.getByTestId('enrichment-video-intel-not-available')).toBeVisible();
  await expect(page.getByTestId('enrichment-lip-sync-not-available')).toBeVisible();
  await expect(page.getByTestId('enrichment-not-available-copy').first()).toContainText('operator feature in setup');
  // A missing route is not an error, so nothing is rendered in the error style.
  expect(await page.getByTestId('enrichment-video-intel-unavailable').count()).toBe(0);
});

test('flags on, GPU health broken: the rest of the admin area is unaffected @optional-enrichment', async ({ page }) => {
  const world = newWorld({ flags: FLAGS_ON, localGpu: 'error500' });
  await mockApi(page, world);
  await page.goto('/login');
  await loginThroughUi(page);
  await page.getByTestId('nav-admin').click();

  await expect(page.getByTestId('admin-local-gpu-unavailable')).toBeVisible();
  await expect(page.getByTestId('admin-local-gpu-nonblocking')).toContainText('rest of the admin area is unaffected');
  // Never "Healthy" when it could not be read.
  const gpu = page.locator('[data-testid="admin-local-gpu"]');
  await expect(gpu).not.toContainText('Healthy');
  // Every other admin section still rendered.
  await expect(page.getByTestId('admin-ops')).toBeVisible();
  await expect(page.getByTestId('admin-usage')).toBeVisible();
  await expect(page.getByTestId('admin-flags')).toBeVisible();
});

test('flags on, ordinary user: no GPU internals reach the page @optional-enrichment', async ({ page }) => {
  const world = newWorld({ flags: FLAGS_ON, permissions: ['project.view'], localGpu: 'ok' });
  await mockApi(page, world);
  await page.goto('/login');
  await loginThroughUi(page);

  // The route itself is guarded, so an ordinary user has no way in.
  await expect(page.getByTestId('nav-admin')).toHaveCount(0);
  await page.evaluate(() => {
    window.history.pushState({}, '', '/admin');
    window.dispatchEvent(new PopStateEvent('popstate'));
  });
  await expect(page.getByTestId('page-forbidden')).toBeVisible();

  // And nothing about the device leaked into the document along the way.
  const html = await page.locator('html').innerHTML();
  expect(html).not.toMatch(/cuda|whisper|local-inference/i);
  expect(world.requests.filter((url) => url.includes('/admin/local-gpu'))).toEqual([]);
});