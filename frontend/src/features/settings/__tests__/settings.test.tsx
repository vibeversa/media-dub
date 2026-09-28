import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
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
import {
  hasSecretMaterial,
  isAllowedPreferenceKey,
  isPreferenceValueTooLarge,
  normalizeLocale,
  normalizeTheme,
  normalizeThemePreference,
  parseDefaultProjectFilters,
  parseTimelineZoom,
  resolveEffectiveTheme,
  resolveStoredTimezone,
} from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-s35', details: {} } },
    status,
  );
}

interface SettingsWorld {
  prefs: Record<string, string>;
  putBehavior: 'ok' | 'unknown-key' | 'too-large';
  calls: { get: number; put: number };
  lastPutBody: unknown;
}

function defaultPrefs(): Record<string, string> {
  return {
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
  };
}

function newWorld(overrides: Partial<SettingsWorld> = {}): SettingsWorld {
  return {
    prefs: defaultPrefs(),
    putBehavior: 'ok',
    calls: { get: 0, put: 0 },
    lastPutBody: undefined,
    ...overrides,
  };
}

let world: SettingsWorld = newWorld();

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

function bodyOf(init?: RequestInit): unknown {
  if (typeof init?.body !== 'string') {
    return undefined;
  }
  try {
    return JSON.parse(init.body as string) as unknown;
  } catch {
    return undefined;
  }
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) {
    return jsonResponse({});
  }
  if (method === 'GET' && url.endsWith('/me/preferences')) {
    world.calls.get += 1;
    return jsonResponse({ ...world.prefs });
  }
  if (method === 'PUT' && url.endsWith('/me/preferences')) {
    world.calls.put += 1;
    const body = bodyOf(init) as Record<string, string>;
    world.lastPutBody = body;
    if (world.putBehavior === 'unknown-key') {
      return errorEnvelope('PREFERENCE_KEY_UNKNOWN', 400);
    }
    if (world.putBehavior === 'too-large') {
      return errorEnvelope('PREFERENCE_VALUE_TOO_LARGE', 413);
    }
    for (const key of Object.keys(body ?? {})) {
      if (!isAllowedPreferenceKey(key)) {
        return errorEnvelope('PREFERENCE_KEY_UNKNOWN', 400);
      }
      const value = (body as Record<string, string>)[key] ?? '';
      if (isPreferenceValueTooLarge(value)) {
        return errorEnvelope('PREFERENCE_VALUE_TOO_LARGE', 413);
      }
      world.prefs[key] = value;
    }
    return jsonResponse({ ...world.prefs });
  }
  return jsonResponse({});
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view']);
}

function renderWithProviders(node: React.ReactNode): void {
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

beforeEach(() => {
  world = newWorld();
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
});

describe('settings round-trip', () => {
  it('loads whitelisted keys and persists locale through save with a confirmation note', async () => {
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-page')).toBeDefined();
    const localeSelect = (await screen.findByTestId('settings-field-locale')) as HTMLSelectElement;
    expect(localeSelect.value).toBe('en');
    fireEvent.change(localeSelect, { target: { value: 'ar' } });
    expect(await screen.findByTestId('settings-dirty-bar')).toBeDefined();
    expect(world.calls.put).toBe(0);
    fireEvent.click(screen.getByTestId('settings-save'));
    await waitFor(() => {
      expect(world.calls.put).toBe(1);
    });
    expect(world.prefs['locale']).toBe('ar');
    expect(await screen.findByTestId('settings-locale-note')).toBeDefined();
    expect(screen.getByTestId('settings-prefs-key').textContent).toContain('preferences');
    const body = world.lastPutBody as Record<string, string>;
    expect(Object.keys(body)).toEqual(['locale']);
    expect(useAppStore.getState().locale).toBe('ar');
  });

  it('applies theme on save without reload', async () => {
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-field-theme-dark')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-field-theme-dark'));
    expect(await screen.findByTestId('settings-dirty-bar')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-save'));
    await waitFor(() => {
      expect(world.calls.put).toBe(1);
    });
    expect(useAppStore.getState().theme).toBe('dark');
    expect(world.prefs['theme']).toBe('dark');
    expect(await screen.findByTestId('settings-theme-note')).toBeDefined();
  });

  it('supports system theme and resolves without reload', async () => {
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-field-theme-system')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-field-theme-system'));
    fireEvent.click(screen.getByTestId('settings-save'));
    await waitFor(() => {
      expect(world.calls.put).toBe(1);
    });
    expect(world.prefs['theme']).toBe('system');
    expect(['light', 'dark']).toContain(useAppStore.getState().theme);
    expect(normalizeThemePreference('system')).toBe('system');
    expect(['light', 'dark']).toContain(resolveEffectiveTheme('system'));
  });

  it('falls back to UTC with an inline warning for unknown timezones', async () => {
    world.prefs = { ...defaultPrefs(), timezone: 'Mars/Olympus' };
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-timezone-warning')).toBeDefined();
    expect(resolveStoredTimezone('Mars/Olympus')).toEqual({ timeZone: 'UTC', fellBack: true });
    expect(resolveStoredTimezone('America/New_York').fellBack).toBe(false);
    expect(screen.getByTestId('settings-field-timezone')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-timezone-use-utc'));
    expect((screen.getByTestId('settings-field-timezone') as HTMLSelectElement).value).toBe('UTC');
  });

  it('rejects unknown keys without sending them and keeps other fields', async () => {
    world.putBehavior = 'unknown-key';
    renderWithProviders(<SettingsPage />);
    const localeSelect = await screen.findByTestId('settings-field-locale');
    fireEvent.change(localeSelect, { target: { value: 'ru' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-field-error-locale')).toBeDefined();
    expect(screen.getByTestId('settings-field-error-message-locale').textContent).toContain('Unknown preference key');
    expect(world.prefs['locale']).toBe('en');
  });

  it('caps values at 4KB and rejects secrets with guidance', async () => {
    expect(isPreferenceValueTooLarge('x'.repeat(5000))).toBe(true);
    expect(isPreferenceValueTooLarge('en')).toBe(false);
    expect(hasSecretMaterial(JSON.stringify({ api_key: 'abc' }))).toBe(true);
    expect(hasSecretMaterial(JSON.stringify({ status: 'Draft' }))).toBe(false);
    expect(isAllowedPreferenceKey('locale')).toBe(true);
    expect(isAllowedPreferenceKey('evilKey')).toBe(false);
    expect(normalizeLocale('ar')).toBe('ar');
    expect(normalizeLocale('xx')).toBe('en');
    expect(normalizeTheme('dark')).toBe('dark');
    expect(normalizeTheme('neon')).toBe('light');
    expect(parseDefaultProjectFilters(JSON.stringify({ status: 'Draft' })).status).toBe('Draft');
    expect(parseTimelineZoom(JSON.stringify(3))).toBe(3);
    expect(parseTimelineZoom('nope')).toBe(1);
  });

  it('shows future-only channels as disabled with an explanatory note', async () => {
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-channel-email')).toBeDefined();
    expect((screen.getByTestId('settings-channel-email') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByTestId('settings-channel-webhook') as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByTestId('settings-channels-note').textContent).toMatch(/future-only/);
  });
});
