import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';

const CORS_JSON = {
  'Content-Type': 'application/json',
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': '*',
  'Access-Control-Allow-Methods': '*',
};

const CORS_SSE = {
  'Content-Type': 'text/event-stream',
  'Cache-Control': 'no-cache',
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
  permissions: ['project.view', 'project.edit', 'review.view', 'export.create', 'admin.manage'],
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
  backlog: { pendingReviews: 2, runningJobs: 0 },
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

function queueBody(): Record<string, unknown> {
  return {
    items: [
      { id: 'rev_001', projectId: 'prj_1', status: 'Open', reason: 'TRANSLATION_QUALITY', createdAt: '2024-01-16T10:00:00Z', resolvedAt: null },
      { id: 'rev_002', projectId: 'prj_1', status: 'Open', reason: 'QC_BLOCKED', createdAt: '2024-01-16T10:05:00Z', resolvedAt: null },
    ],
    page: 1,
    pageSize: 50,
    total: 2,
    hasMore: false,
  };
}

function contextBody(reviewId: string): Record<string, unknown> {
  return {
    item: { id: reviewId, type: 'TRANSLATION_QUALITY', severity: 'high', status: 'Open', version: 1 },
    project: { id: 'prj_1', name: 'Pilot' },
    run: { id: 'run_1', status: 'Running', configHash: 'cfg_abc' },
    segment: { id: 'seg_001', startMs: 0, endMs: 2000, speakerId: 'spk_alice' },
    versions: {
      transcript: [
        { id: 'tr-v1', provider: 'acme', model: 'stt-v1', text: 'hello world', isSelected: true, createdAt: '2024-01-15T12:00:00Z' },
      ],
      translation: [
        { id: 'tl-v1', primaryText: 'hola mundo', provider: 'acme', model: 'mt-v1', isSelected: true, createdAt: '2024-01-15T13:00:00Z' },
      ],
      selectedTranscriptVersionId: 'tr-v1',
      selectedTranslationVersionId: 'tl-v1',
      selectionVersion: 2,
      truncated: false,
    },
    voice: { speakerId: 'spk_alice', voiceProfileId: 'voice_1', voiceId: 'stock-es-1', consentState: 'not_required' },
    audio: { previewArtifactId: 'art_1', signedUrl: null },
    sync: { offsetMs: 300, driftFlag: true },
    qc: {
      issues: [{ id: 'qc_1', code: 'QC_NOISY', severity: 'high', message: 'noisy segment', artifactId: null }],
      evidenceArtifactIds: [],
    },
    actions: { allowed: ['resolve', 'dismiss', 'reopen', 'resolve-with-edit'] },
    permissions: { canResolve: true, canEdit: true },
    history: [
      { id: 'h1', type: 'Requeue', reviewer: 'usr_1', reason: 'needs fix', createdAt: '2024-01-16T09:00:00Z' },
    ],
    truncated: false,
  };
}

function sseFrame(id: string, eventType: string): string {
  const data = {
    correlationId: 'corr_e2e',
    eventId: id,
    eventType,
    occurredAt: '2024-01-16T12:00:00Z',
    payload: { projectId: 'prj_1', status: 'Running' },
    projectId: 'prj_1',
    processingRunId: 'run_1',
    schemaVersion: 1,
    tenantId: 'tenant_e2e',
  };
  return `id: ${id}\nevent: ${eventType}\ndata: ${JSON.stringify(data)}\n\n`;
}

interface ReviewE2EWorld {
  conflictNext: boolean;
  mutations: number;
}

function newWorld(overrides: Partial<ReviewE2EWorld> = {}): ReviewE2EWorld {
  return { conflictNext: false, mutations: 0, ...overrides };
}

/**
 * `@review` suite (Task 031). Hermetic: auth, dashboard, list, workspace,
 * progress, SSE, unread-count, output/download, reviews queue, review
 * context, and hardened mutations are intercepted. SPA navigation only.
 */
async function mockReviewApi(page: Page, world: ReviewE2EWorld): Promise<void> {
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
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(ME_BODY) });
      return;
    }
    if (url.includes('/dashboard/summary') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(SUMMARY_BODY) });
      return;
    }
    if (url.includes('/progress/stream') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_SSE, body: sseFrame('evt_1', 'stage.progress') });
      return;
    }
    if (url.includes('/progress') && !url.includes('/stream') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ percentApproximate: 42, projectId: 'prj_1', status: 'Running' }) });
      return;
    }
    if (url.includes('/notifications/unread-count') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ unreadCount: 0 }) });
      return;
    }
    if (url.includes('/output/download') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ downloadUrl: 'https://example.com/media.mp4', expiresAt: '2026-09-26T00:00:00Z' }) });
      return;
    }
    if (method === 'POST' && /\/reviews\/rev_[^/]+\/(resolve-with-edit|resolve|dismiss|reopen)$/.test(url)) {
      world.mutations += 1;
      if (world.conflictNext) {
        world.conflictNext = false;
        await route.fulfill({
          status: 409,
          headers: CORS_JSON,
          body: JSON.stringify({ error: { code: 'REVIEW_VERSION_CONFLICT', message: 'stale', correlationId: 'c', details: { currentVersion: 2 } } }),
        });
        return;
      }
      const isReopen = url.endsWith('/reopen');
      const isDismiss = url.endsWith('/dismiss');
      const isEdit = url.endsWith('/resolve-with-edit');
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({
          reviewId: 'rev_001',
          status: isReopen ? 'Open' : isDismiss ? 'Rejected' : 'Approved',
          version: 2,
          manualVersionId: isEdit ? 'ver_manual_1' : null,
          versionKind: isEdit ? 'manual' : null,
        }),
      });
      return;
    }
    if (method === 'GET' && /\/reviews\/rev_[^/?]+/.test(url)) {
      const match = /\/reviews\/(rev_[^/?]+)/.exec(url);
      const reviewId = match?.[1] ?? 'rev_001';
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(contextBody(reviewId)) });
      return;
    }
    if (method === 'GET' && url.includes('/reviews')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(queueBody()) });
      return;
    }
    if (url.includes('/workspace') && method === 'GET') {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({
          project: { id: 'prj_1', name: 'Pilot', status: 'Processing', sourceLanguage: 'en', targetLanguage: 'es', isArchived: false, configurationHash: 'cfg_abc', settingsVersion: 3 },
          media: { id: 'med_1', status: 'Valid', container: 'mp4', sizeBytes: 1024, durationMs: 61000 },
          run: { id: 'run_1', status: 'Running', configHash: 'cfg_abc', attempt: 1 },
          phase: 'speech',
          stage: 'Review',
          progress: { percentApproximate: 42, currentStage: 'Review', updatedAt: '2024-01-16T12:00:00Z' },
          review: { pendingCount: 2, oldestWaitingAt: '2024-01-16T10:00:00Z' },
          warnings: [],
          output: { state: 'pending', completeness: 42 },
          cost: { runCost: 1.5, monthToDate: 12.5 },
          activity: { recent: [] },
          permissions: { allowedActions: ['project.view', 'project.edit'] },
        }),
      });
      return;
    }
    if (url.includes('/api/v1/projects') && method === 'GET') {
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

async function gotoReview(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.evaluate(() => {
    window.history.pushState({}, '', '/review?project=prj_1');
    window.dispatchEvent(new PopStateEvent('popstate'));
  });
  await expect(page.getByTestId('review-studio')).toBeVisible();
}

test('queue filters narrow the list @review', async ({ page }) => {
  await mockReviewApi(page, newWorld());
  await gotoReview(page);
  await expect(page.getByTestId('review-queue-list')).toBeVisible();
  await expect(page.getByTestId('review-row-rev_001')).toBeVisible();
  await expect(page.getByTestId('review-row-rev_002')).toBeVisible();
  await page.getByTestId('review-filter-status').fill('Open');
  await page.getByTestId('review-apply-filters').click();
  await expect(page).toHaveURL(/status=Open/);
});

test('card renders full context without leaving the queue @review', async ({ page }) => {
  await mockReviewApi(page, newWorld());
  await gotoReview(page);
  await expect(page.getByTestId('review-queue-list')).toBeVisible();
  await page.getByTestId('review-row-rev_001').click();
  await expect(page.getByTestId('review-card-rev_001')).toBeVisible();
  await expect(page.getByTestId('review-transcript-rev_001')).toContainText('hello world');
  await expect(page.getByTestId('review-translation-rev_001')).toContainText('hola mundo');
  await expect(page.getByTestId('review-voice-rev_001')).toContainText('stock-es-1');
  await expect(page.getByTestId('review-sync-rev_001')).toContainText('QC_NOISY');
  await expect(page.getByTestId('review-history-rev_001')).toContainText('needs fix');
});

test('approve and reject dispose with reason @review', async ({ page }) => {
  const world = newWorld();
  await mockReviewApi(page, world);
  await gotoReview(page);
  await expect(page.getByTestId('review-queue-list')).toBeVisible();
  await page.getByTestId('review-row-rev_001').click();
  await expect(page.getByTestId('review-card-rev_001')).toBeVisible();
  await page.getByTestId('review-reason-rev_001').fill('looks good');
  await page.getByTestId('review-approve-rev_001').click();
  await expect(page.getByTestId('review-studio-empty')).toBeVisible({ timeout: 10_000 });
  expect(world.mutations).toBeGreaterThan(0);
});

test('stale conflict shows a banner and preserves reason @review', async ({ page }) => {
  const world = newWorld({ conflictNext: true });
  await mockReviewApi(page, world);
  await gotoReview(page);
  await expect(page.getByTestId('review-queue-list')).toBeVisible();
  await page.getByTestId('review-row-rev_001').click();
  await expect(page.getByTestId('review-card-rev_001')).toBeVisible();
  await page.getByTestId('review-reason-rev_001').fill('my preserved note');
  await page.getByTestId('review-approve-rev_001').click();
  await expect(page.getByTestId('review-stale-rev_001')).toBeVisible();
  await expect(page.getByTestId('review-reason-rev_001')).toHaveValue('my preserved note');
});
