// Task 039B: store state-matrix gap closure.
//
// Covers the persisted-slice branches in `src/stores/index.ts`: theme toggle
// in both directions, locale/opt-out persistence, stored-value hydration,
// and storage-failure fallbacks (fail-closed to in-memory defaults).
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useAppStore } from '../index.js';

afterEach(() => {
  useAppStore.getState().resetForTests();
  window.localStorage.clear();
  vi.restoreAllMocks();
});

describe('store slices', () => {
  it('toggles the theme in both directions and persists the choice', () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    useAppStore.getState().toggleTheme();
    expect(useAppStore.getState().theme).toBe('dark');
    useAppStore.getState().toggleTheme();
    expect(useAppStore.getState().theme).toBe('light');
    expect(setItem).toHaveBeenCalledWith('dubbing.theme', 'dark');
    useAppStore.getState().setTheme('dark');
    expect(useAppStore.getState().theme).toBe('dark');
  });

  it('persists locale, timezone, opt-out, session, and readiness', () => {
    const store = useAppStore.getState();
    store.setLocale('ar');
    expect(useAppStore.getState().locale).toBe('ar');
    expect(window.localStorage.getItem('dubbing.locale')).toBe('ar');
    store.setTenantTimezone('Europe/Berlin');
    expect(useAppStore.getState().tenantTimezone).toBe('Europe/Berlin');
    store.setTenantTimezone(undefined);
    expect(useAppStore.getState().tenantTimezone).toBeUndefined();
    store.setTelemetryOptOut(true);
    expect(useAppStore.getState().telemetryOptOut).toBe(true);
    expect(window.localStorage.getItem('dubbing.telemetry.optOut')).toBe('1');
    store.setTelemetryOptOut(false);
    expect(useAppStore.getState().telemetryOptOut).toBe(false);
    expect(window.localStorage.getItem('dubbing.telemetry.optOut')).toBe('0');
    store.setSession('authenticated', ['a']);
    expect(useAppStore.getState().sessionStatus).toBe('authenticated');
    expect(useAppStore.getState().permissions).toEqual(['a']);
    store.markReady();
    expect(useAppStore.getState().ready).toBe(true);
  });

  it('keeps preferences in memory when storage writes fail', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('quota exceeded');
    });
    useAppStore.getState().setTheme('dark');
    expect(useAppStore.getState().theme).toBe('dark');
    useAppStore.getState().setLocale('ar');
    expect(useAppStore.getState().locale).toBe('ar');
  });

  it('hydrates persisted values on fresh import', async () => {
    window.localStorage.setItem('dubbing.theme', 'dark');
    window.localStorage.setItem('dubbing.locale', 'ar');
    window.localStorage.setItem('dubbing.telemetry.optOut', '1');
    vi.resetModules();
    const fresh = await import('../index.js');
    expect(fresh.useAppStore.getState().theme).toBe('dark');
    expect(fresh.useAppStore.getState().locale).toBe('ar');
    expect(fresh.useAppStore.getState().telemetryOptOut).toBe(true);
  });

  it('falls back to defaults when storage reads fail', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    vi.resetModules();
    const fresh = await import('../index.js');
    expect(fresh.useAppStore.getState().theme).toBe('light');
    expect(fresh.useAppStore.getState().locale).toBe('en');
    expect(fresh.useAppStore.getState().telemetryOptOut).toBe(false);
  });

  it('treats blank stored values as absent', async () => {
    window.localStorage.setItem('dubbing.theme', 'sepia');
    window.localStorage.setItem('dubbing.locale', '');
    vi.resetModules();
    const fresh = await import('../index.js');
    expect(fresh.useAppStore.getState().theme).toBe('light');
    expect(fresh.useAppStore.getState().locale).toBe('en');
  });
});
