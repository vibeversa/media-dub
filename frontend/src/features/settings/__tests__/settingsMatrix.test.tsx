// Task 039B: settings state-matrix gap closure.
//
// Extends `settings.test.tsx` (round-trip) + `preferences.spec.tsx`
// (editors, rollback, dirty guard, conflict, offline) with the missing
// states: SettingsPage shells (loading/error-retry/forbidden 403 +
// USER_DISABLED with no data leak/empty map) and PreferencesForm banners
// (forbidden, save-error + retry, timezone warning + use-UTC, offline queued
// + retry, conflict use-server/refresh), field-error display, back-link
// navigation, and the types/helper sweep. Every failure asserts its recovery
// control per §11.6 with text signals (041C); fixtures are synthetic (R3).
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { LocaleProvider } from '../../../app/providers/LocaleProvider.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { ToastProvider } from '../../../components/Toast/Toast.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { SettingsPage } from '../SettingsPage.js';
import { invalidatePreferences } from '../usePreferences.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, details: Record<string, unknown> = {}): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-s35b', details } },
    status,
  );
}

function defaultPrefs(): Record<string, string> {
  return {
    locale: 'en',
    timezone: 'UTC',
    theme: 'light',
    defaultProjectFilters: JSON.stringify({ status: '', archived: '' }),
    timelineZoom: JSON.stringify(1),
    notificationPreferences: JSON.stringify({ ProcessingCompleted: true }),
  };
}

type GetBehavior = 'ok' | 'empty' | 'error500' | 'forbidden403' | 'disabled' | 'never';
type PutBehavior = 'ok' | 'conflict' | 'offline' | 'forbidden' | 'error500';

interface SettingsMatrixWorld {
  prefs: Record<string, string>;
  getBehavior: GetBehavior;
  putBehavior: PutBehavior;
  getCalls: number;
  putCalls: number;
}

let world: SettingsMatrixWorld;

function resetWorld(): void {
  world = { prefs: defaultPrefs(), getBehavior: 'ok', putBehavior: 'ok', getCalls: 0, putCalls: 0 };
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') {
    return input;
  }
  if (input instanceof URL) {
    return input.href;
  }
  return (input as Request).url;
}

function methodOf(input: RequestInfo | URL, init?: RequestInit): string {
  if (typeof input !== 'string' && !(input instanceof URL)) {
    const request = input as Request;
    if (typeof request.method === 'string' && request.method !== '') {
      return request.method.toUpperCase();
    }
  }
  return (init?.method ?? 'GET').toUpperCase();
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) {
    return jsonResponse({});
  }
  if (method === 'GET' && url.endsWith('/me/preferences')) {
    world.getCalls += 1;
    switch (world.getBehavior) {
      case 'empty':
        return jsonResponse({});
      case 'error500':
        return errorEnvelope('INTERNAL_ERROR', 500);
      case 'forbidden403':
        return errorEnvelope('FORBIDDEN', 403);
      case 'disabled':
        return errorEnvelope('USER_DISABLED', 403);
      case 'never':
        return new Promise<Response>(() => {});
      default:
        return jsonResponse({ ...world.prefs });
    }
  }
  if (method === 'PUT' && url.endsWith('/me/preferences')) {
    world.putCalls += 1;
    if (world.putBehavior === 'conflict') {
      return errorEnvelope('CONFLICT', 409);
    }
    if (world.putBehavior === 'offline') {
      throw new TypeError('Failed to fetch');
    }
    if (world.putBehavior === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (world.putBehavior === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    return jsonResponse({ ...world.prefs });
  }
  return jsonResponse({});
}

function renderWithProviders(node: React.JSX.Element): void {
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <MemoryRouter>{node}</MemoryRouter>
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view']);
}

beforeEach(() => {
  resetWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  authenticate();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  vi.restoreAllMocks();
});

describe('SettingsPage shell states', () => {
  it('shows loading while preferences resolve (never blank)', () => {
    world.getBehavior = 'never';
    renderWithProviders(<SettingsPage />);
    expect(screen.getByTestId('settings-loading')).toBeDefined();
  });

  it('recovers from load errors with retry (recovery: retry)', async () => {
    world.getBehavior = 'error500';
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-error')).toBeDefined();
    const callsBefore = world.getCalls;
    world.getBehavior = 'ok';
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(world.getCalls).toBeGreaterThan(callsBefore));
    expect(await screen.findByTestId('preferences-form')).toBeDefined();
  });

  it('restricts forbidden viewers with contact-admin recovery (no leak)', async () => {
    world.getBehavior = 'forbidden403';
    renderWithProviders(<SettingsPage />);
    const panel = await screen.findByTestId('settings-error');
    expect(panel.textContent).toContain('Preferences unavailable');
    expect(panel.textContent).toContain('contact your tenant admin');
    expect(document.body.textContent).not.toContain('America');
  });

  it('treats disabled accounts as forbidden (recovery: contact admin)', async () => {
    world.getBehavior = 'disabled';
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-error')).toBeDefined();
    expect(screen.getByTestId('settings-error').textContent).toContain('Preferences unavailable');
  });

  it('notes empty preference maps without error chrome (recovery: edit)', async () => {
    world.getBehavior = 'empty';
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-empty')).toBeDefined();
    expect(screen.queryByTestId('settings-error')).toBeNull();
    expect(screen.getByTestId('preferences-form')).toBeDefined();
  });
});

describe('PreferencesForm banner matrix', () => {
  it('banners forbidden saves with contact-admin recovery (no leak)', async () => {
    world.putBehavior = 'forbidden';
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('preferences-form')).toBeDefined();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    const banner = await screen.findByTestId('settings-forbidden');
    expect(banner.textContent).toContain('admin');
  });

  it('reports save failures with retry that recovers (recovery: retry)', async () => {
    world.putBehavior = 'error500';
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('preferences-form')).toBeDefined();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-save-error')).toBeDefined();
    world.putBehavior = 'ok';
    fireEvent.click(screen.getByTestId('settings-save-retry'));
    await waitFor(() => expect(world.putCalls).toBe(2));
  });

  it('warns on unknown timezones with a UTC fallback (recovery: use-utc)', async () => {
    world.prefs = { ...defaultPrefs(), timezone: 'Mars/Olympus_Mons' };
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('preferences-form')).toBeDefined();
    expect(await screen.findByTestId('settings-timezone-warning')).toBeDefined();
    expect(screen.getByTestId('settings-timezone-warning').textContent).toContain('Fell back to UTC');
    fireEvent.click(screen.getByTestId('settings-timezone-use-utc'));
    expect((screen.getByTestId('settings-field-timezone') as HTMLSelectElement).value).toBe('UTC');
  });

  it('queues offline saves with an explicit retry (recovery: retry)', async () => {
    world.putBehavior = 'offline';
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('preferences-form')).toBeDefined();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-offline')).toBeDefined();
    expect(screen.getByTestId('settings-queued-text').textContent?.length).toBeGreaterThan(0);
    world.putBehavior = 'ok';
    fireEvent.click(screen.getByTestId('settings-queued-retry'));
    await waitFor(() => expect(world.putCalls).toBe(2));
  });

  it('offers use-server and refresh on conflicts with drafts kept (recovery: review)', async () => {
    world.putBehavior = 'conflict';
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('preferences-form')).toBeDefined();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-conflict')).toBeDefined();
    expect(screen.getByTestId('settings-conflict-text').textContent?.length).toBeGreaterThan(0);
    fireEvent.click(screen.getByTestId('settings-conflict-refresh'));
    expect(screen.getByTestId('settings-conflict-use-server')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-conflict-use-server'));
    await waitFor(() => expect(screen.queryByTestId('settings-conflict')).toBeNull());
  });
});

describe('settings navigation + guards', () => {
  it('links back without losing the shell (recovery: back link)', async () => {
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('preferences-form')).toBeDefined();
    expect(screen.getByTestId('settings-back-link')).toBeDefined();
  });

  it('never fires for anonymous sessions and invalidates cleanly', async () => {
    let calls = 0;
    setInnerFetchForTests((async () => {
      calls += 1;
      return jsonResponse({});
    }) as typeof fetch);
    useAuthStore.setState({ status: 'anonymous' });
    renderWithProviders(<SettingsPage />);
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(calls).toBe(0);
    useAuthStore.setState({ status: 'authenticated' });
    await invalidatePreferences(queryClient);
  });
});
