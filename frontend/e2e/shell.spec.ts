import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';

/**
 * `@shell` smoke (Task 018): the authenticated shell renders product nav;
 * Admin stays unreachable for non-admin sessions. The session is seeded
 * through localStorage because login/session logic lands in Task 019.
 */
async function seedSession(page: Page, permissions: readonly string[]): Promise<void> {
  await page.addInitScript((perms) => {
    window.localStorage.setItem(
      'dubbing.e2e.session',
      JSON.stringify({ status: 'authenticated', permissions: perms }),
    );
  }, [...permissions]);
}

test('shell renders nav for authenticated users @shell', async ({ page }) => {
  await seedSession(page, []);
  await page.goto('/dashboard');
  await expect(page.getByTestId('app-shell')).toBeVisible();
  await expect(page.getByTestId('nav-dashboard')).toBeVisible();
  await expect(page.getByTestId('nav-projects')).toBeVisible();
  await expect(page.getByTestId('page-dashboard')).toBeVisible();
  await expect(page.getByText(/Version /).first()).toBeVisible();
});

test('admin hidden for non-admin sessions @shell', async ({ page }) => {
  await seedSession(page, []);
  await page.goto('/dashboard');
  await expect(page.getByTestId('nav-admin')).toHaveCount(0);
  await page.goto('/admin');
  await expect(page.getByTestId('page-forbidden')).toBeVisible();
});

test('admin reachable for admin sessions @shell', async ({ page }) => {
  await seedSession(page, ['admin.manage']);
  await page.goto('/dashboard');
  await expect(page.getByTestId('nav-admin')).toBeVisible();
  await page.goto('/admin');
  await expect(page.getByTestId('page-admin')).toBeVisible();
});
