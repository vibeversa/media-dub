import { create } from 'zustand';

export type SessionStatus = 'loading' | 'authenticated' | 'anonymous';
export type ThemeName = 'light' | 'dark';

const THEME_KEY = 'dubbing.theme';
const LOCALE_KEY = 'dubbing.locale';
const TELEMETRY_OPT_OUT_KEY = 'dubbing.telemetry.optOut';

function readStoredTheme(): ThemeName {
  try {
    return window.localStorage.getItem(THEME_KEY) === 'dark' ? 'dark' : 'light';
  } catch {
    return 'light';
  }
}

function readStoredLocale(): string {
  try {
    const stored = window.localStorage.getItem(LOCALE_KEY);
    return stored !== null && stored !== '' ? stored : 'en';
  } catch {
    return 'en';
  }
}

function readStoredOptOut(): boolean {
  try {
    return window.localStorage.getItem(TELEMETRY_OPT_OUT_KEY) === '1';
  } catch {
    return false;
  }
}

function persist(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // Storage full or unavailable: preferences stay in memory only.
  }
}

interface AppState {
  readonly ready: boolean;
  /** Pre-shell identity resolution (Task 019 fills this from GET /me). Starts loading. */
  readonly sessionStatus: SessionStatus;
  /** UX-hint permission strings from /me (never a security boundary). */
  readonly permissions: readonly string[];
  readonly theme: ThemeName;
  readonly locale: string;
  /** Tenant timezone from preferences (Task 035 writes it); undefined = browser tz. */
  readonly tenantTimezone: string | undefined;
  readonly telemetryOptOut: boolean;
  readonly markReady: () => void;
  readonly setSession: (status: SessionStatus, permissions: readonly string[]) => void;
  readonly setTheme: (theme: ThemeName) => void;
  readonly toggleTheme: () => void;
  readonly setLocale: (locale: string) => void;
  readonly setTenantTimezone: (timeZone: string | undefined) => void;
  readonly setTelemetryOptOut: (optOut: boolean) => void;
  /** Test-only reset to Hermes defaults. Never used in production code. */
  readonly resetForTests: () => void;
}

/**
 * Zustand store root (Task 018): session placeholder Task 019 resolves via
 * GET /me, UI slices (theme/locale/telemetry opt-out) persisted locally,
 * tenant timezone defaulting to undefined (browser tz) until Task 035.
 */
export const useAppStore = create<AppState>()((set) => ({
  ready: false,
  sessionStatus: 'loading',
  permissions: [],
  theme: readStoredTheme(),
  locale: readStoredLocale(),
  tenantTimezone: undefined,
  telemetryOptOut: readStoredOptOut(),
  markReady: () => {
    set({ ready: true });
  },
  setSession: (status, permissions) => {
    set({ sessionStatus: status, permissions: [...permissions] });
  },
  setTheme: (theme) => {
    persist(THEME_KEY, theme);
    set({ theme });
  },
  toggleTheme: () => {
    set((state) => {
      const next: ThemeName = state.theme === 'dark' ? 'light' : 'dark';
      persist(THEME_KEY, next);
      return { theme: next };
    });
  },
  setLocale: (locale) => {
    persist(LOCALE_KEY, locale);
    set({ locale });
  },
  setTenantTimezone: (timeZone) => {
    set({ tenantTimezone: timeZone });
  },
  setTelemetryOptOut: (optOut) => {
    persist(TELEMETRY_OPT_OUT_KEY, optOut ? '1' : '0');
    set({ telemetryOptOut: optOut });
  },
  resetForTests: () => {
    set({
      ready: false,
      sessionStatus: 'loading',
      permissions: [],
      theme: 'light',
      locale: 'en',
      tenantTimezone: undefined,
      telemetryOptOut: false,
    });
  },
}));
