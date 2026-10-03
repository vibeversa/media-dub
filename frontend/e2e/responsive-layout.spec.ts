import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';

/**
 * Responsive layout suite (GAP-023, Plan B 11.5 / 15.8 R4 / 12.12).
 *
 * Two facts are pinned, and both used to fail:
 *
 * 1. Below the 768px tablet breakpoint the timeline degrades to list mode — the
 *    canvas waveform and the five-lane timeline are absent, and the same
 *    segments are readable as stacked rows. Playback stays available.
 * 2. From 768px up the canvas timeline renders and the sidebar is visible.
 *
 * Hermetic: every `/api/v1/**` call is intercepted, so no backend is required.
 * The viewport widths are the breakpoint constant from
 * `src/features/timeline/timelineResponsive.ts` — 767 is the last list width.
 */

const CORS_JSON = {
  'Content-Type': 'application/json',
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Headers': '*',
  'Access-Control-Allow-Methods': '*',
};

const ME_BODY = {
  permissions: [
    'project.view',
    'project.edit',
    'project.delete',
    'processing.cancel',
    'processing.retry',
    'review.view',
    'export.create',
    'export.download',
    'admin.manage',
  ],
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
};

function segment(index: number): Record<string, unknown> {
  const id = `seg_${String(index + 1).padStart(3, '0')}`;
  const startMs = index * 2000;
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
    reviewStatus: index === 0 ? 'Open' : 'Approved',
    qualityCodes: [],
    syncStatus: 'SyncAcceptable',
    confidence: 0.95,
    text: `line ${id}`,
  };
}

async function mockApi(page: Page): Promise<void> {
  await page.route('**/api/v1/**', async (route: Route) => {
    const request = route.request();
    const url = request.url();
    const method = request.method();

    if (url.includes('/auth/login')) {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({
          accessToken: 'e2e-access-token',
          refreshToken: 'e2e-refresh-token',
          tokenType: 'Bearer',
          expiresInSeconds: 900,
          userId: 'usr_e2e',
          tenantId: 'tenant_e2e',
        }),
      });
      return;
    }

    if (url.endsWith('/me') && method === 'GET') {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(ME_BODY) });
      return;
    }

    if (url.includes('/dashboard/summary')) {
      await route.fulfill({ status: 200, headers: CORS_JSON, body: JSON.stringify(SUMMARY_BODY) });
      return;
    }

    if (method === 'GET' && url.includes('/segments') && !url.includes('/segments/seg_')) {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({
          items: [segment(0), segment(1)],
          page: 1,
          pageSize: 200,
          total: 2,
          hasMore: false,
        }),
      });
      return;
    }

    if (method === 'GET' && url.includes('/output/download')) {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ downloadUrl: 'https://example.com/media.mp4', expiresAt: '2099-01-01T00:00:00Z' }),
      });
      return;
    }

    if (method === 'GET' && url.includes('waveform-peaks')) {
      await route.fulfill({
        status: 200,
        headers: CORS_JSON,
        body: JSON.stringify({ points: [], resolutions: [64], missing: true }),
      });
      return;
    }

    await route.fulfill({ status: 200, headers: CORS_JSON, body: '{}' });
  });
}

async function gotoTimeline(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByTestId('auth-tenant').fill('acme');
  await page.getByTestId('auth-email').fill('owner@example.com');
  await page.getByTestId('auth-password').fill('secret');
  await page.getByTestId('auth-submit').click();
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await page.evaluate(() => {
    window.history.pushState({}, '', '/projects/prj_1/timeline');
    window.dispatchEvent(new PopStateEvent('popstate'));
  });
  await expect(page.getByTestId('timeline-workspace')).toBeVisible();
}

test('timeline degrades to list mode below the tablet breakpoint @responsive', async ({ page }) => {
  await mockApi(page);
  await page.setViewportSize({ width: 375, height: 720 });
  await gotoTimeline(page);

  await expect(page.getByTestId('timeline-list-mode')).toBeVisible();
  await expect(page.getByTestId('timeline-list-row-seg_001')).toBeVisible();
  await expect(page.getByTestId('timeline-list-speaker-seg_001')).toHaveText('Alice');
  await expect(page.getByTestId('timeline-list-text-seg_001')).toHaveText('line seg_001');

  // The canvas surfaces are not rendered at all on a phone.
  await expect(page.getByTestId('timeline-workspace-waveform')).toHaveCount(0);
  await expect(page.getByTestId('timeline-workspace-timeline')).toHaveCount(0);

  // Playback survives the degradation.
  await expect(page.getByTestId('timeline-workspace-player')).toBeVisible();
});

test('mobile nav replaces the sidebar below the tablet breakpoint @responsive', async ({ page }) => {
  await mockApi(page);
  await page.setViewportSize({ width: 375, height: 720 });
  await gotoTimeline(page);

  await expect(page.getByTestId('mobile-nav')).toBeVisible();
  await expect(page.getByTestId('sidenav-projects')).toBeHidden();
});

test('canvas timeline and sidebar render from the tablet breakpoint up @responsive', async ({ page }) => {
  await mockApi(page);
  await page.setViewportSize({ width: 1024, height: 768 });
  await gotoTimeline(page);

  await expect(page.getByTestId('timeline-workspace-waveform')).toBeVisible();
  await expect(page.getByTestId('timeline-workspace-timeline')).toBeVisible();
  await expect(page.getByTestId('timeline-list-mode')).toHaveCount(0);
  await expect(page.getByTestId('sidenav-projects')).toBeVisible();
});