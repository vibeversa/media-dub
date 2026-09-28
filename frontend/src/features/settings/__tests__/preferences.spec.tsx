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
  buildPreferencePayload,
  collectDirtyKeys,
  draftFromServerMap,
  fieldErrorsFromDetails,
  isDraftDirty,
  isPreferenceConflictError,
  isPreferenceForbiddenError,
  isPreferenceOfflineError,
  serializeDraft,
} from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, details: Record<string, unknown> = {}): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-p35b', details } },
    status,
  );
}

interface PrefsWorld {
  prefs: Record<string, string>;
  putBehavior: 'ok' | 'conflict' | 'offline' | 'forbidden' | 'validation';
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

function newWorld(overrides: Partial<PrefsWorld> = {}): PrefsWorld {
  return {
    prefs: defaultPrefs(),
    putBehavior: 'ok',
    calls: { get: 0, put: 0 },
    lastPutBody: undefined,
    ...overrides,
  };
}

let world: PrefsWorld = newWorld();

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
    world.lastPutBody = bodyOf(init);
    if (world.putBehavior === 'conflict') {
      return errorEnvelope('CONFLICT', 409);
    }
    if (world.putBehavior === 'offline') {
      throw new TypeError('Failed to fetch');
    }
    if (world.putBehavior === 'forbidden') {
      return errorEnvelope('FORBIDDEN', 403);
    }
    if (world.putBehavior === 'validation') {
      return errorEnvelope('VALIDATION_FAILED', 400, { locale: 'Unsupported locale for this tenant.' });
    }
    const body = (world.lastPutBody ?? {}) as Record<string, string>;
    for (const key of Object.keys(body)) {
      world.prefs[key] = body[key] ?? '';
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

describe('editors round-trip all six keys', () => {
  it('edits filters, zoom, and notification toggles through one save', async () => {
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-page')).toBeDefined();
    expect(await screen.findByTestId('settings-field-filters-status')).toBeDefined();
    fireEvent.change(screen.getByTestId('settings-field-filters-status'), { target: { value: 'Draft' } });
    fireEvent.change(screen.getByTestId('settings-field-zoom'), { target: { value: '3' } });
    fireEvent.click(screen.getByTestId('settings-pref-ProcessingCompleted'));
    expect(await screen.findByTestId('settings-dirty-bar')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-save'));
    await waitFor(() => {
      expect(world.calls.put).toBe(1);
    });
    const body = world.lastPutBody as Record<string, string>;
    expect(Object.keys(body).sort()).toEqual(
      ['defaultProjectFilters', 'notificationPreferences', 'timelineZoom'].sort(),
    );
    expect(body['defaultProjectFilters']).toContain('Draft');
    expect(body['timelineZoom']).toBe(JSON.stringify(3));
    expect(body['notificationPreferences']).toContain('ProcessingCompleted');
    expect(world.prefs['defaultProjectFilters']).toContain('Draft');
  });

  it('never sends unlisted keys and computes dirty per key', () => {
    const server = defaultPrefs();
    const draft = draftFromServerMap(server);
    expect(isDraftDirty(draft, server)).toBe(false);
    expect(collectDirtyKeys(draft, server)).toEqual([]);
    const edited = { ...draft, locale: 'ar' };
    expect(isDraftDirty(edited, server)).toBe(true);
    expect(collectDirtyKeys(edited, server)).toEqual(['locale']);
    const payload = buildPreferencePayload(edited, server);
    expect(Object.keys(payload)).toEqual(['locale']);
    expect(serializeDraft(edited)['locale']).toBe('ar');
  });
});

describe('optimistic rollback', () => {
  it('rolls back store and cache on validation failure and keeps the draft', async () => {
    world.putBehavior = 'validation';
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-field-locale')).toBeDefined();
    const beforeLocale = useAppStore.getState().locale;
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-field-error-locale')).toBeDefined();
    expect(screen.getByTestId('settings-field-error-message-locale').textContent).toContain('Unsupported locale');
    expect(world.prefs['locale']).toBe('en');
    expect(useAppStore.getState().locale).toBe(beforeLocale);
    expect((screen.getByTestId('settings-field-locale') as HTMLSelectElement).value).toBe('ar');
  });

  it('maps server detail bags to per-field errors purely', () => {
    expect(fieldErrorsFromDetails({ locale: 'bad locale' })['locale']).toBe('bad locale');
    expect(fieldErrorsFromDetails({ evilKey: 'x' })['evilKey' as never]).toBeUndefined();
    expect(fieldErrorsFromDetails(undefined)).toEqual({});
    expect(isPreferenceConflictError({ status: 409 })).toBe(true);
    expect(isPreferenceConflictError({ code: 'CONFLICT' })).toBe(true);
    expect(isPreferenceOfflineError({ code: 'NETWORK_ERROR' })).toBe(true);
    expect(isPreferenceForbiddenError({ status: 403 })).toBe(true);
  });
});

describe('dirty guard save discard cancel', () => {
  it('offers save discard cancel on leave and preserves or resets the draft', async () => {
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-field-locale')).toBeDefined();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    expect(await screen.findByTestId('settings-dirty-bar')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-back-link'));
    expect(await screen.findByTestId('settings-dirty-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-dirty-cancel'));
    expect(screen.queryByTestId('settings-dirty-dialog')).toBeNull();
    expect((screen.getByTestId('settings-field-locale') as HTMLSelectElement).value).toBe('ar');
    fireEvent.click(screen.getByTestId('settings-back-link'));
    expect(await screen.findByTestId('settings-dirty-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-dirty-discard'));
    expect(screen.queryByTestId('settings-dirty-dialog')).toBeNull();
    expect((screen.getByTestId('settings-field-locale') as HTMLSelectElement).value).toBe('en');
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ru' } });
    fireEvent.click(screen.getByTestId('settings-back-link'));
    expect(await screen.findByTestId('settings-dirty-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-dirty-save'));
    await waitFor(() => {
      expect(world.calls.put).toBe(1);
    });
    expect(world.prefs['locale']).toBe('ru');
  });

  it('discards from the dirty bar without saving', async () => {
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-field-locale')).toBeDefined();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    expect(await screen.findByTestId('settings-dirty-bar')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-discard'));
    expect((screen.getByTestId('settings-field-locale') as HTMLSelectElement).value).toBe('en');
    expect(screen.queryByTestId('settings-dirty-bar')).toBeNull();
    expect(world.calls.put).toBe(0);
  });
});

describe('conflict keeps draft and refetches', () => {
  it('shows conflict, keeps draft, and retries or uses server', async () => {
    world.putBehavior = 'conflict';
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-field-locale')).toBeDefined();
    const getsBefore = world.calls.get;
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-conflict')).toBeDefined();
    expect(screen.getByTestId('settings-conflict-text').textContent).toMatch(/changed elsewhere/);
    expect((screen.getByTestId('settings-field-locale') as HTMLSelectElement).value).toBe('ar');
    expect(world.prefs['locale']).toBe('en');
    expect(world.calls.get).toBeGreaterThan(getsBefore);
    world.putBehavior = 'ok';
    fireEvent.click(screen.getByTestId('settings-conflict-retry'));
    await waitFor(() => {
      expect(world.prefs['locale']).toBe('ar');
    });
  });

  it('uses server values to discard the conflicted draft', async () => {
    world.putBehavior = 'conflict';
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-field-locale')).toBeDefined();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-conflict')).toBeDefined();
    fireEvent.click(screen.getByTestId('settings-conflict-use-server'));
    expect((screen.getByTestId('settings-field-locale') as HTMLSelectElement).value).toBe('en');
    expect(screen.queryByTestId('settings-conflict')).toBeNull();
  });
});

describe('offline queues draft', () => {
  it('preserves the draft with a queued notice and retries', async () => {
    world.putBehavior = 'offline';
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-field-locale')).toBeDefined();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-offline')).toBeDefined();
    expect(screen.getByTestId('settings-queued-text').textContent).toMatch(/queued/);
    expect((screen.getByTestId('settings-field-locale') as HTMLSelectElement).value).toBe('ar');
    expect(world.prefs['locale']).toBe('en');
    world.putBehavior = 'ok';
    fireEvent.click(screen.getByTestId('settings-queued-retry'));
    await waitFor(() => {
      expect(world.prefs['locale']).toBe('ar');
    });
  });

  it('explains forbidden saves without bypass', async () => {
    world.putBehavior = 'forbidden';
    renderWithProviders(<SettingsPage />);
    expect(await screen.findByTestId('settings-field-locale')).toBeDefined();
    fireEvent.change(screen.getByTestId('settings-field-locale'), { target: { value: 'ar' } });
    fireEvent.click(screen.getByTestId('settings-save'));
    expect(await screen.findByTestId('settings-forbidden')).toBeDefined();
    expect(screen.getByTestId('settings-forbidden-text').textContent).toMatch(/scoped to your account/);
    expect(world.prefs['locale']).toBe('en');
  });
});
