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
  permissions: ['project.view', 'project.edit', 'project.delete', 'processing.cancel', 'processing.retry', 'review.view', 'export.create', 'export.download', 'admin.manage'],
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
  backlog: { pendingReviews: 0, runningJobs: 0 },
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

function workspaceBody(): Record<string, unknown> {
  return {
    project: { id: 'prj_1', name: 'Pilot', status: 'Processing', sourceLanguage: 'en', targetLanguage: 'es', isArchived: false, configurationHash: 'cfg_abc', settingsVersion: 3 },
    media: { id: 'med_1', status: 'Valid', container: 'mp4', sizeBytes: 1024, durationMs: 61000 },
    run: { id: 'run_1', status: 'Running', configHash: 'cfg_abc', attempt: 1 },
    phase: 'speech',
    stage: 'Translation',
    progress: { percentApproximate: 42, currentStage: 'Translation', updatedAt: '2024-01-16T12:00:00Z' },
    review: { pendingCount: 2, oldestWaitingAt: '2024-01-16T10:00:00Z' },
    warnings: [],
    output: { state: 'pending', completeness: 42 },
    cost: { runCost: 1.5, monthToDate: 12.5 },
    activity: { recent: [{ id: 'act_1', summary: 'Run started', occurredAt: '2024-01-16T11:00:00Z' }] },
    permissions: { allowedActions: ['project.view', 'project.edit', 'project.delete', 'processing.cancel', 'processing.retry', 'export.create', 'export.download', 'admin.manage'] },
  };
}

interface TranslationWorld {
  selectBehavior: 'ok' | 'conflict';
  selectBodies: unknown[];
  selected: Record<string, string>;
}

function newWorld(): TranslationWorld {
  return { selectBehavior: 'ok', selectBodies: [], selected: {} };
}

function segmentRow(index: number, world: TranslationWorld): Record<string, unknown> {
  const id = `seg_00${String(index + 1)}`;
  const startMs = index * 2000;
  const selectedTr = world.selected[id] ?? `${id}-tr2`;
  return {
    id,
    projectId: 'prj_1',
    status: 'Ready',
    sequence: index + 1,
    startMs,
    endMs: startMs + 1800,
    speakerId: index % 2 === 0 ? 'spk_alice' : 'spk_bob',
    speakerLabel: index % 2 === 0 ? 'Alice' : 'Bob',
    selectionVersion: 2,
    reviewStatus: 'Approved',
    qualityCodes: [],
    syncStatus: 'SyncAcceptable',
    selectedTranscriptVersionId: `${id}-t2`,
    selectedTranslationVersionId: selectedTr,
    transcriptVersions: [
      { id: `${id}-t1`, provider: 'acme', model: 'stt-v1', text: `source line ${id}`, isSelected: false, createdAt: '2024-01-15T12:00:00Z' },
      { id: `${id}-t2`, provider: 'acme', model: 'stt-v2', text: `selected source ${id}`, isSelected: true, createdAt: '2024-01-15T13:00:00Z' },
    ],
    translationVersions: [
      { id: `${id}-tr1`, provider: 'acme', model: 'mt-v1', text: `candidate A ${id}`, isSelected: selectedTr === `${id}-tr1`, score: 0.81, createdAt: '2024-01-15T12:00:00Z' },
      { id: `${id}-tr2`, provider: 'acme', model: 'mt-v2', text: `candidate B ${id}`, isSelected: selectedTr === `${id}-tr2`, score: 0.92, createdAt: '2024-01-15T13:00:00Z' },
    ],
    glossaryHits: index === 0 ? [{ term: 'Pilot', definition: 'Project codename' }] : [],
    assignedVoice: index === 0 ? { voiceId: 'voice_1', label: 'Voice One' } : null,
  };
}

function segmentsBody(world: TranslationWorld): Record<string, unknown> {
  return { items: [segmentRow(0, world), segmentRow(1, world), segmentRow(2, world)], page: 1, pageSize: 200, total: 3, hasMore: false };
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

/**
 * `@translation` suite (Task 028). Hermetic: auth, dashboard, list,
 * workspace, progress, SSE, unread-count, segments list/detail, and
 * translation selection/edits are intercepted. SPA navigation only.
 */
async function mockTranslationApi(page: Page, world: TranslationWorld): Promise<void> {
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
    if (url.includes('/segments/') && url.includes('/translation-selection') && method === 'POST') {
      const match = /\/segments\/(seg_[^/?]+)/.exec(url);
      const segmentId = match?.[1] ?? 'seg_001';
      try {
        const body = (await request.postDataJSON()) as { selectedVersionIds?: string[] };
        world.selectBodies.push(body);
        const next = body.selectedVersionIds?.[0];
        if (next !== undefined && next !== '') {
          world.selected[segmentId] = next;
        }
      } catch {
        world.selectBodies.push({});
      }
      if (world.selectBehavior === 'conflict') {
        await route.fulfill({
          status: 409,
          headers: CORS_JSON,
          body: JSON.stringify({ error: { code: 'SELECTION_CONFLICT', message: 'stale', correlationId: 'c', details: { currentSelectionVersion: 9 } } }),
        });
        return;
      }
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ segmentId, selectionVersion: 3 }) });
      return;
    }
    if (url.includes('/segments/') && url.includes('/translation-edits') && method === 'POST') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ segmentId: 'seg_001', selectionVersion: 4, newVersionId: 'v-manual' }) });
      return;
    }
    if (method === 'GET' && /\/segments\/seg_/.test(url)) {
      const match = /\/segments\/(seg_[^/?]+)/.exec(url);
      const segmentId = match?.[1] ?? 'seg_001';
      const index = Number(segmentId.replace('seg_00', '')) - 1;
      const row = segmentRow(Number.isFinite(index) && index >= 0 ? index : 0, world);
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ ...row, id: segmentId }) });
      return;
    }
    if (method === 'GET' && url.includes('/segments')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(segmentsBody(world)) });
      return;
    }
    if (url.includes('/workspace') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(workspaceBody()) });
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

async function gotoTranslation(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.getByTestId('dashboard-stat-link').click();
  await expect(page.getByTestId('projects-grid')).toBeVisible();
  await page.getByTestId('project-open-prj_1').click();
  await expect(page.getByTestId('workspace-page')).toBeVisible();
  const tab = page.getByTestId('project-tab-translation');
  if ((await tab.count()) > 0) {
    await tab.click();
  } else {
    await page.evaluate(() => {
      window.history.pushState({}, '', '/projects/prj_1/translation');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
  }
  await expect(page.getByTestId('translation-workspace')).toBeVisible();
}

test('side-by-side renders source, selected, and alternatives @translation', async ({ page }) => {
  await mockTranslationApi(page, newWorld());
  await gotoTranslation(page);
  await expect(page.getByTestId('translation-side-source')).toBeVisible();
  await expect(page.getByTestId('translation-side-selected')).toBeVisible();
  await expect(page.getByTestId('translation-side-alternatives')).toBeVisible();
  await expect(page.getByTestId('translation-source')).toContainText('selected source');
  await expect(page.getByTestId('translation-source-version')).toContainText('transcript v');
  await expect(page.getByTestId('translation-selected')).toContainText('candidate B');
  await expect(page.getByTestId('translation-candidate-seg_001-tr1')).toBeVisible();
  // Candidates render without editable inputs (immutability).
  await expect(page.getByTestId('translation-candidate-seg_001-tr1').locator('input, textarea')).toHaveCount(0);
});

test('select updates the selected card @translation', async ({ page }) => {
  await mockTranslationApi(page, newWorld());
  await gotoTranslation(page);
  await expect(page.getByTestId('translation-row-seg_001')).toBeVisible();
  await expect(page.getByTestId('translation-selected')).toContainText('candidate B seg_001');
  await page.getByTestId('translation-select-version-seg_001-tr1').click();
  await expect(page.getByTestId('translation-selected')).toContainText('candidate A seg_001');
});

test('dirty guard blocks navigation until resolved @translation', async ({ page }) => {
  await mockTranslationApi(page, newWorld());
  await gotoTranslation(page);
  await expect(page.getByTestId('translation-draft')).toBeVisible();
  await page.getByTestId('translation-draft').fill('dirty draft blocks leave');
  await page.getByTestId('translation-back-link').click();
  await expect(page.getByTestId('translation-dirty-dialog')).toBeVisible();
  await page.getByTestId('translation-dirty-cancel').click();
  await expect(page.getByTestId('translation-workspace')).toBeVisible();
  await expect(page.getByTestId('translation-dirty-dialog')).toHaveCount(0);
  await page.getByTestId('translation-back-link').click();
  await expect(page.getByTestId('translation-dirty-dialog')).toBeVisible();
  await page.getByTestId('translation-dirty-discard').click();
  await expect(page.getByTestId('workspace-page')).toBeVisible();
});

test('409 shows the stale banner with refresh @translation', async ({ page }) => {
  const world = newWorld();
  world.selectBehavior = 'conflict';
  await mockTranslationApi(page, world);
  await gotoTranslation(page);
  await expect(page.getByTestId('translation-row-seg_001')).toBeVisible();
  await page.getByTestId('translation-select-version-seg_001-tr1').click();
  await expect(page.getByTestId('translation-stale-banner')).toBeVisible();
  await expect(page.getByTestId('translation-stale-refresh')).toBeVisible();
  await page.getByTestId('translation-stale-refresh').click();
  await expect(page.getByTestId('translation-stale-banner')).toHaveCount(0);
});
