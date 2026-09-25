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
    stage: 'Voices',
    progress: { percentApproximate: 42, currentStage: 'Voices', updatedAt: '2024-01-16T12:00:00Z' },
    review: { pendingCount: 2, oldestWaitingAt: '2024-01-16T10:00:00Z' },
    warnings: [],
    output: { state: 'pending', completeness: 42 },
    cost: { runCost: 1.5, monthToDate: 12.5 },
    activity: { recent: [{ id: 'act_1', summary: 'Run started', occurredAt: '2024-01-16T11:00:00Z' }] },
    permissions: { allowedActions: ['project.view', 'project.edit', 'project.delete', 'processing.cancel', 'processing.retry', 'export.create', 'export.download', 'admin.manage'] },
  };
}

interface VoicesWorld {
  assigned: Record<string, string>;
}

function newWorld(): VoicesWorld {
  return { assigned: { spk_alice: 'stock-es-1' } };
}

function speakerRow(id: string, world: VoicesWorld): Record<string, unknown> {
  if (id === 'spk_alice') {
    return {
      id: 'spk_alice',
      projectId: 'prj_1',
      speakerKey: 'SPEAKER_00',
      displayName: 'Alice',
      segmentCount: 2,
      firstAppearanceMs: 1000,
      lastAppearanceMs: 5000,
      assignedVoice: world.assigned['spk_alice'] !== undefined
        ? { voiceProfileId: 'voice_1', voiceId: world.assigned['spk_alice'], provider: 'acme', language: 'es', type: 'Stock' }
        : null,
    };
  }
  if (id === 'spk_bob') {
    return {
      id: 'spk_bob',
      projectId: 'prj_1',
      speakerKey: 'SPEAKER_01',
      displayName: 'Bob',
      segmentCount: 1,
      firstAppearanceMs: 6000,
      lastAppearanceMs: 9000,
      assignedVoice: world.assigned['spk_bob'] !== undefined
        ? { voiceProfileId: 'voice_2', voiceId: world.assigned['spk_bob'], provider: 'acme', language: 'es', type: 'Stock' }
        : null,
    };
  }
  return {
    id: 'spk_zero',
    projectId: 'prj_1',
    speakerKey: 'SPEAKER_02',
    displayName: 'Zero',
    segmentCount: 0,
    assignedVoice: null,
  };
}

function speakersBody(world: VoicesWorld): Record<string, unknown> {
  return {
    items: [speakerRow('spk_alice', world), speakerRow('spk_bob', world), speakerRow('spk_zero', world)],
    page: 1,
    pageSize: 100,
    total: 3,
    hasMore: false,
  };
}

function availableBody(): Record<string, unknown> {
  return {
    voices: [
      { voiceProfileId: 'voice_1', voiceId: 'stock-es-1', provider: 'acme', language: 'es', type: 'Stock', cloningEnabled: false, consentStatus: 'valid', isDefault: true },
      { voiceProfileId: 'voice_2', voiceId: 'stock-es-2', provider: 'acme', language: 'es', type: 'Stock', cloningEnabled: false, consentStatus: 'valid' },
      { voiceProfileId: 'voice_3', voiceId: 'cloned-es-1', provider: 'acme', language: 'es', type: 'Cloned', cloningEnabled: true, consentStatus: 'consent-required', tenantPolicyMessage: 'Tenant policy: cloning consent required for cloned-es-1.' },
      { voiceProfileId: 'voice_4', voiceId: 'cloned-es-2', provider: 'acme', language: 'es', type: 'Cloned', cloningEnabled: true, consentStatus: 'revoked', tenantPolicyMessage: 'Tenant policy: consent revoked for cloned-es-2.' },
    ],
    excludedCount: 1,
    excluded: [{ voiceId: 'stock-fr-1', reasons: ["LANGUAGE_MISMATCH"] }],
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

/**
 * `@voices` suite (Task 029). Hermetic: auth, dashboard, list, workspace,
 * progress, SSE, unread-count, speakers, available-voices, assignment, and
 * previews are intercepted. SPA navigation only.
 */
async function mockVoicesApi(page: Page, world: VoicesWorld): Promise<void> {
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
    if (url.includes('/voice-assignment') && method === 'PUT') {
      const match = /\/speakers\/(spk_[^/?]+)/.exec(url);
      const speakerId = match?.[1] ?? 'spk_bob';
      try {
        const body = (await request.postDataJSON()) as { voiceId?: string };
        const voiceId = body.voiceId ?? 'voice_1';
        const inventory = voiceId === 'voice_1' ? 'stock-es-1' : voiceId === 'voice_2' ? 'stock-es-2' : voiceId;
        world.assigned[speakerId] = inventory;
      } catch {
        world.assigned[speakerId] = 'stock-es-1';
      }
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ speakerId, voiceProfileId: 'voice_2', voiceId: 'stock-es-2', changed: true, outputStale: false }) });
      return;
    }
    if (url.includes('/voice-previews/') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify({ previewId: 'vpv_1', status: 'Completed', downloadUrl: 'https://example.com/preview.wav' }) });
      return;
    }
    if (url.includes('/voice-previews') && method === 'POST') {
      await route.fulfill({ status: 202, headers: CORS_JSON, body: JSON.stringify({ previewId: 'vpv_1', status: 'Queued', isDuplicate: false }) });
      return;
    }
    if (url.includes('/available-voices') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(availableBody()) });
      return;
    }
    if (method === 'GET' && /\/speakers\/spk_/.test(url)) {
      const match = /\/speakers\/(spk_[^/?]+)/.exec(url);
      const speakerId = match?.[1] ?? 'spk_alice';
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(speakerRow(speakerId, world)) });
      return;
    }
    if (method === 'GET' && url.includes('/speakers')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(speakersBody(world)) });
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

async function gotoVoices(page: Page): Promise<void> {
  await page.goto('/login');
  await loginThroughUi(page);
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.getByTestId('dashboard-stat-link').click();
  await expect(page.getByTestId('projects-grid')).toBeVisible();
  await page.getByTestId('project-open-prj_1').click();
  await expect(page.getByTestId('workspace-page')).toBeVisible();
  const tab = page.getByTestId('project-tab-voices');
  if ((await tab.count()) > 0) {
    await tab.click();
  } else {
    await page.evaluate(() => {
      window.history.pushState({}, '', '/projects/prj_1/voices');
      window.dispatchEvent(new PopStateEvent('popstate'));
    });
  }
  await expect(page.getByTestId('voices-workspace')).toBeVisible();
}

test('speaker list renders with voice state @voices', async ({ page }) => {
  await mockVoicesApi(page, newWorld());
  await gotoVoices(page);
  await expect(page.getByTestId('voices-row-spk_alice')).toBeVisible();
  await expect(page.getByTestId('voices-segments-spk_alice')).toContainText('2 segments');
  await expect(page.getByTestId('voices-voice-spk_alice')).toContainText('stock-es-1');
  await expect(page.getByTestId('voices-segments-spk_zero')).toContainText('0 segments');
  await expect(page.getByTestId('voices-selector')).toBeVisible();
  await expect(page.getByTestId('voices-option-stock-es-1')).toBeVisible();
  await expect(page.getByTestId('voices-option-stock-fr-1')).toHaveCount(0);
});

test('preview plays the signed URL @voices', async ({ page }) => {
  await mockVoicesApi(page, newWorld());
  await gotoVoices(page);
  await expect(page.getByTestId('voices-option-stock-es-1')).toBeVisible();
  await page.getByTestId('voices-preview-request-stock-es-1').first().click();
  const audio = page.getByTestId('voices-preview-audio-stock-es-1');
  await expect(audio).toBeVisible();
  await expect(audio).toHaveAttribute('src', 'https://example.com/preview.wav');
});

test('assign shows impact dialog then updates voice @voices', async ({ page }) => {
  await mockVoicesApi(page, newWorld());
  await gotoVoices(page);
  await expect(page.getByTestId('voices-row-spk_bob')).toBeVisible();
  await page.getByTestId('voices-select-spk_bob').click();
  await expect(page.getByTestId('voices-current-voice')).toContainText('—');
  await page.getByTestId('voices-select-voice-stock-es-2').click();
  await expect(page.getByTestId('voices-impact-dialog')).toBeVisible();
  await expect(page.getByTestId('voices-impact-segments')).toContainText('1 segment');
  await expect(page.getByTestId('voices-impact-invalidation')).toContainText('invalidates');
  await page.getByTestId('voices-impact-confirm').click();
  await expect(page.getByTestId('voices-impact-dialog')).toHaveCount(0);
  await expect(page.getByTestId('voices-current-voice')).toContainText('stock-es-2');
});

test('revoked voice blocked with policy message @voices', async ({ page }) => {
  await mockVoicesApi(page, newWorld());
  await gotoVoices(page);
  await expect(page.getByTestId('voices-option-cloned-es-2')).toBeVisible();
  await expect(page.getByTestId('voices-option-policy-cloned-es-2')).toContainText('Tenant policy: consent revoked');
  await expect(page.getByTestId('voices-select-voice-cloned-es-2')).toBeDisabled();
});
