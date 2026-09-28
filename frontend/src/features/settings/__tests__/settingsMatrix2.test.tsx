// Delta 2: settings remaining-branch closure.
//
// Supplements settingsMatrix (shells, banners, offline, conflict) with the
// PreferencesForm save-error taxonomy (unknown keys, oversized values,
// validation statuses, generic failures), conflict refetch tolerance, the
// dirty-guard dialog lifecycle (stay/discard/save-and-leave), and the
// settings types exhaustive sweep. Synthetic fixtures, fetch intercepted.
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
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
import { PreferencesForm } from '../PreferencesForm.js';
import {
  buildPreferencePayload,
  collectDirtyKeys,
  draftFromServerMap,
  fieldErrorsFromDetails,
  hasSecretMaterial,
  isAllowedPreferenceKey,
  isDraftDirty,
  isPreferenceConflictError,
  isPreferenceForbiddenError,
  isPreferenceOfflineError,
  isPreferenceValueTooLarge,
  isValidThemePreference,
  isValidTimezone,
  normalizeLocale,
  normalizeTheme,
  normalizeThemePreference,
  normalizeTimezoneOption,
  parseDefaultProjectFilters,
  parseTimelineZoom,
  preferenceByteLength,
  resolveEffectiveTheme,
  resolveStoredTimezone,
  serializeDefaultProjectFilters,
  serializeDraft,
  serializeLocale,
  serializeThemePreference,
  serializeTimelineZoom,
  serializeTimezone,
} from '../types.js';
import type { PreferenceMap } from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, message?: string, details: Record<string, unknown> = {}): Response {
  return jsonResponse(
    { error: { code, message: message ?? `backend ${code}`, correlationId: 'corr-s35c', details } },
    status,
  );
}

function defaultPrefs(): PreferenceMap {
  return {
    locale: 'en',
    timezone: 'UTC',
    theme: 'light',
    defaultProjectFilters: JSON.stringify({ status: '', archived: '' }),
    timelineZoom: JSON.stringify(1),
    notificationPreferences: JSON.stringify({ ProcessingCompleted: true }),
  };
}

type PutBehavior = 'ok' | 'conflict' | 'unknown-key' | 'too-large' | 'bad-request' | 'bad-request-empty' | 'error500';

interface World {
  putBehavior: PutBehavior;
  putCalls: number;
  refetchRejects: boolean;
}

let world: World;

function resetWorld(): void {
  world = { putBehavior: 'ok', putCalls: 0, refetchRejects: false };
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.href;
  return (input as Request).url;
}

function methodOf(input: RequestInfo | URL, init?: RequestInit): string {
  if (typeof input !== 'string' && !(input instanceof URL)) {
    const request = input as Request;
    if (typeof request.method === 'string' && request.method !== '') return request.method.toUpperCase();
  }
  return (init?.method ?? 'GET').toUpperCase();
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) return jsonResponse({});
  if (method === 'PUT' && url.endsWith('/me/preferences')) {
    world.putCalls += 1;
    switch (world.putBehavior) {
      case 'conflict':
        return errorEnvelope('CONFLICT', 409);
      case 'unknown-key':
        return errorEnvelope('PREFERENCE_KEY_UNKNOWN', 400);
      case 'too-large':
        return errorEnvelope('PREFERENCE_VALUE_TOO_LARGE', 413);
      case 'bad-request':
        return errorEnvelope('BAD_REQUEST', 400, 'Locale not supported.');
      case 'bad-request-empty':
        return errorEnvelope('BAD_REQUEST', 422, '');
      case 'error500':
        return errorEnvelope('INTERNAL_ERROR', 500, 'Boom.');
      default:
        return jsonResponse({ ...defaultPrefs() });
    }
  }
  return jsonResponse({});
}

async function refetch(): Promise<unknown> {
  if (world.refetchRejects) throw new Error('refetch failed');
  await queryClient.invalidateQueries();
  return undefined;
}

function renderForm(serverMap: PreferenceMap = defaultPrefs()): void {
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <MemoryRouter initialEntries={['/settings']}>
            <Routes>
              <Route path="/settings" element={<PreferencesForm serverMap={serverMap} refetch={refetch} />} />
              <Route path="/dashboard" element={<div data-testid="page-dashboard" />} />
            </Routes>
          </MemoryRouter>
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

describe('PreferencesForm save-error taxonomy', () => {
  it('maps unknown keys to field errors (recovery: refresh)', async () => {
    world.putBehavior = 'unknown-key';
    renderForm();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-field-error-locale')).toBeDefined();
    expect(screen.getByTestId('settings-field-error-message-locale').textContent).toContain('Unknown preference key');
  });

  it('maps oversized values to field errors (recovery: shorten)', async () => {
    world.putBehavior = 'too-large';
    renderForm();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-field-error-locale')).toBeDefined();
    expect(screen.getByTestId('settings-field-error-message-locale').textContent).toContain('too large');
  });

  it('renders server validation messages verbatim on 400/422', async () => {
    world.putBehavior = 'bad-request';
    renderForm();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-field-error-message-locale')).toBeDefined();
    expect(screen.getByTestId('settings-field-error-message-locale').textContent).toContain('Locale not supported');
    cleanup();
    queryClient.clear();
    world.putBehavior = 'bad-request-empty';
    renderForm();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-field-error-locale')).toBeDefined();
  });

  it('reports generic failures with retry recovery', async () => {
    world.putBehavior = 'error500';
    renderForm();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-save-error')).toBeDefined();
    expect(screen.getByTestId('settings-save-error-text').textContent).toContain('Boom');
    world.putBehavior = 'ok';
    fireEvent.click(screen.getByTestId('settings-save-retry'));
    await waitFor(() => expect(world.putCalls).toBe(2));
  });

  it('keeps drafts when conflict refetch fails (recovery: review)', async () => {
    world.putBehavior = 'conflict';
    world.refetchRejects = true;
    renderForm();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-conflict')).toBeDefined();
    expect((screen.getByTestId('settings-field-locale') as HTMLSelectElement).value).toBe('ar');
  });
});

describe('dirty-guard dialog lifecycle', () => {
  it('stays on cancel without losing the draft', async () => {
    renderForm();
    expect(await screen.findByTestId('preferences-form')).toBeDefined();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-back-link'));
    expect(await screen.findByTestId('settings-dirty-dialog')).toBeDefined();
    expect(screen.getByTestId('settings-dirty-dialog-text').textContent).toContain('dashboard');
    fireEvent.click(screen.getByTestId('settings-dirty-cancel'));
    await waitFor(() => expect(screen.queryByTestId('settings-dirty-dialog')).toBeNull());
    expect((screen.getByTestId('settings-field-locale') as HTMLSelectElement).value).toBe('ar');
    expect(screen.queryByTestId('page-dashboard')).toBeNull();
  });

  it('discards and leaves on confirm', async () => {
    renderForm();
    expect(await screen.findByTestId('preferences-form')).toBeDefined();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-back-link'));
    expect(await screen.findByTestId('settings-dirty-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-dirty-discard'));
    expect(await screen.findByTestId('page-dashboard')).toBeDefined();
  });

  it('saves through the guard then leaves', async () => {
    renderForm();
    expect(await screen.findByTestId('preferences-form')).toBeDefined();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-back-link'));
    expect(await screen.findByTestId('settings-dirty-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-dirty-save'));
    expect(await screen.findByTestId('page-dashboard')).toBeDefined();
    expect(world.putCalls).toBe(1);
  });

  it('leaves immediately when clean', async () => {
    renderForm();
    expect(await screen.findByTestId('preferences-form')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-back-link'));
    expect(await screen.findByTestId('page-dashboard')).toBeDefined();
  });
});

describe('settings types sweep', () => {
  it('gates keys and sizes defensively', () => {
    expect(isAllowedPreferenceKey('locale')).toBe(true);
    expect(isAllowedPreferenceKey('bogus')).toBe(false);
    expect(preferenceByteLength('abc')).toBe(3);
    expect(isPreferenceValueTooLarge('x'.repeat(5000))).toBe(true);
    expect(isPreferenceValueTooLarge('ok')).toBe(false);
    expect(hasSecretMaterial('not json')).toBe(false);
    expect(hasSecretMaterial('[1,2]')).toBe(false);
    expect(hasSecretMaterial(JSON.stringify({ api_key: 'x' }))).toBe(true);
    expect(hasSecretMaterial(JSON.stringify({ ApiKey: 'x' }))).toBe(true);
    expect(hasSecretMaterial(JSON.stringify({ theme: 'dark' }))).toBe(false);
  });

  it('normalizes locales, themes, and timezones with fallbacks', () => {
    expect(normalizeLocale('ar')).toBe('ar');
    expect(normalizeLocale('ru-RU')).toBe('ru');
    expect(normalizeLocale('en-US')).toBe('en');
    expect(normalizeLocale('fr')).toBe('en');
    expect(normalizeLocale(7)).toBe('en');
    expect(normalizeTheme('dark')).toBe('dark');
    expect(normalizeTheme('bogus')).toBe('light');
    expect(normalizeThemePreference('system')).toBe('system');
    expect(normalizeThemePreference(' Dark ')).toBe('dark');
    expect(normalizeThemePreference('bogus')).toBe('light');
    expect(normalizeThemePreference(7)).toBe('light');
    expect(isValidThemePreference('system')).toBe(true);
    expect(isValidThemePreference('bogus')).toBe(false);
    expect(resolveEffectiveTheme('dark')).toBe('dark');
    expect(resolveEffectiveTheme('light')).toBe('light');
    expect(['light', 'dark']).toContain(resolveEffectiveTheme('system'));
    expect(isValidTimezone('America/New_York')).toBe(true);
    expect(isValidTimezone('Mars/Olympus')).toBe(false);
    expect(isValidTimezone('')).toBe(false);
    expect(resolveStoredTimezone('America/New_York')).toEqual({ timeZone: 'America/New_York', fellBack: false });
    expect(resolveStoredTimezone('')).toEqual({ timeZone: 'UTC', fellBack: false });
    expect(resolveStoredTimezone('Mars/X')).toEqual({ timeZone: 'UTC', fellBack: true });
    expect(resolveStoredTimezone(7)).toEqual({ timeZone: 'UTC', fellBack: false });
    expect(normalizeTimezoneOption('Europe/Paris')).toBe('Europe/Paris');
    expect(normalizeTimezoneOption('Mars/X')).toBe('UTC');
    expect(normalizeTimezoneOption('')).toBe('UTC');
  });

  it('parses filters and zoom windows defensively', () => {
    expect(parseDefaultProjectFilters('')).toEqual({ status: '', archived: '' });
    expect(parseDefaultProjectFilters(7)).toEqual({ status: '', archived: '' });
    expect(parseDefaultProjectFilters('not json')).toEqual({ status: '', archived: '' });
    expect(parseDefaultProjectFilters('[1]')).toEqual({ status: '', archived: '' });
    expect(parseDefaultProjectFilters(JSON.stringify({ status: 'x'.repeat(100) })).status.length).toBe(40);
    expect(parseDefaultProjectFilters(JSON.stringify({ Status: 'Draft', Archived: 'active' }))).toEqual({ status: 'Draft', archived: 'active' });
    expect(serializeDefaultProjectFilters({ status: 'Draft', archived: '' })).toContain('Draft');
    expect(parseTimelineZoom('')).toBe(1);
    expect(parseTimelineZoom(7 as never)).toBe(1);
    expect(parseTimelineZoom('nope')).toBe(1);
    expect(parseTimelineZoom('3')).toBe(3);
    expect(parseTimelineZoom('99')).toBe(4);
    expect(parseTimelineZoom(JSON.stringify(2))).toBe(2);
    expect(parseTimelineZoom(JSON.stringify('nope'))).toBe(1);
    expect(serializeTimelineZoom(2.4)).toBe('2');
    expect(serializeTimelineZoom(Number.NaN)).toBe('1');
    expect(serializeLocale('ar')).toBe('ar');
    expect(serializeTimezone('')).toBe('UTC');
    expect(serializeTimezone('Europe/Paris')).toBe('Europe/Paris');
    expect(serializeThemePreference('system')).toBe('system');
  });

  it('builds drafts and dirty payloads on whitelisted keys only', () => {
    const server: PreferenceMap = { ...defaultPrefs(), timezone: '"Europe/Paris"', theme: 'bogus', timelineZoom: 'nope' };
    const draft = draftFromServerMap(server);
    expect(draft.timezone).toBe('Europe/Paris');
    expect(draft.theme).toBe('light');
    expect(draft.zoom).toBe(1);
    expect(draftFromServerMap({ ...defaultPrefs(), timezone: 'not json at all' }).timezone).toBe('UTC');
    expect(draftFromServerMap({}).locale).toBe('en');
    const changed = { ...draft, locale: 'ar' };
    expect(isDraftDirty(changed, server)).toBe(true);
    expect(isDraftDirty(draft, server)).toBe(false);
    expect(collectDirtyKeys(changed, server)).toEqual(['locale']);
    expect(Object.keys(buildPreferencePayload(draft, server))).toEqual([]);
    expect(serializeDraft(changed).locale).toBe('ar');
  });

  it('classifies save errors and extracts field messages', () => {
    expect(isPreferenceConflictError(undefined)).toBe(false);
    expect(isPreferenceConflictError(null)).toBe(false);
    expect(isPreferenceConflictError({ status: 409 })).toBe(true);
    expect(isPreferenceConflictError({ code: 'REVIEW_VERSION_CONFLICT' })).toBe(true);
    expect(isPreferenceConflictError({ code: 'X_CONFLICT' })).toBe(true);
    expect(isPreferenceConflictError({ code: 'X' })).toBe(false);
    expect(isPreferenceOfflineError(undefined)).toBe(false);
    expect(isPreferenceOfflineError({ code: 'NETWORK_ERROR' })).toBe(true);
    expect(isPreferenceOfflineError({ message: 'Failed to fetch' })).toBe(true);
    expect(isPreferenceOfflineError({ message: 'you are offline' })).toBe(true);
    expect(isPreferenceOfflineError({ message: 'Load failed' })).toBe(true);
    expect(isPreferenceOfflineError({ message: 'network unavailable now' })).toBe(true);
    expect(isPreferenceOfflineError({ message: 'fetch failed badly' })).toBe(true);
    expect(isPreferenceOfflineError({ message: 'server exploded' })).toBe(false);
    expect(isPreferenceForbiddenError(undefined)).toBe(false);
    expect(isPreferenceForbiddenError({ status: 403 })).toBe(true);
    expect(isPreferenceForbiddenError({ code: 'FORBIDDEN' })).toBe(true);
    expect(isPreferenceForbiddenError({ code: 'USER_DISABLED' })).toBe(true);
    expect(isPreferenceForbiddenError({ code: 'X' })).toBe(false);
    expect(fieldErrorsFromDetails(null)).toEqual({});
    expect(fieldErrorsFromDetails('nope')).toEqual({});
    expect(fieldErrorsFromDetails({ locale: 'bad locale', bogus: 'dropped' })).toEqual({ locale: 'bad locale' });
    expect(fieldErrorsFromDetails({ theme: { message: 'bad theme' } })).toEqual({ theme: 'bad theme' });
    expect(fieldErrorsFromDetails({ timezone: { error: 'bad tz' } })).toEqual({ timezone: 'bad tz' });
    expect(fieldErrorsFromDetails({ locale: 7 })).toEqual({});
  });
});
