import { create } from 'zustand';
import { setTokenProvider } from '../../api/client/index.js';
import { NETWORK_ERROR, normalizeError } from '../../api/errors/index.js';
import { queryClient } from '../../app/providers/queryClient.js';
import { useAppStore } from '../../stores/index.js';
import { fetchMe, login as apiLogin, logout as apiLogout, refreshSession } from './api.js';
import type { LoginCredentials } from './api.js';

/**
 * Authentication session store (Task 019).
 *
 * - Tokens live in memory only (zustand state). Nothing here touches
 *   `localStorage`/`sessionStorage` with token material: the sole storage
 *   write is a timestamp logout broadcast for other tabs (R1; the no-persist
 *   test asserts no stored value ever contains token material).
 * - `status` drives the pre-shell gate: `unknown` blocks the shell (skeleton),
 *   `authenticated` renders it, `expired`/`error`/`anonymous` redirect to
 *   `/login?next=<destination>` (R5, R2).
 * - Silent refresh is scheduled at 80% of the access-token lifetime
 *   (`computeRefreshDelayMs`); concurrent callers share one in-flight refresh
 *   promise, so expiry storms and double-submits collapse into a single
 *   rotation (R3).
 * - The store syncs every transition into `useAppStore` (`setSession`) so the
 *   Task 018 shell, IA, and guards keep working unchanged.
 * - Logout clears the query cache and session slices *before* the caller
 *   navigates (R4): `queryClient.clear()` runs prior to the status flip, and
 *   callers `await logout()` before `navigate(...)`, so back-button never
 *   reveals cached data.
 */

export type AuthStatus = 'unknown' | 'authenticated' | 'expired' | 'error' | 'anonymous';

export interface AuthState {
  readonly status: AuthStatus;
  /** In-memory access JWT. Never persisted, never logged, never telemetered. */
  readonly accessToken: string | undefined;
  /** In-memory refresh secret. Never persisted, never logged, never telemetered. */
  readonly refreshToken: string | undefined;
  readonly expiresAtMs: number | undefined;
  readonly userId: string | undefined;
  readonly tenantId: string | undefined;
  /** UX-hint permission strings from `/me` (never a security boundary). */
  readonly permissions: readonly string[];
  /** Last failure code for support triage (a catalog code, never a secret). */
  readonly lastErrorCode: string | undefined;
  /** True while a login request is in flight (double-submit guard). */
  readonly loginPending: boolean;
  readonly login: (credentials: LoginCredentials) => Promise<void>;
  /** Pre-shell resolution: validates memory tokens via refresh → me. */
  readonly restore: () => Promise<void>;
  /**
   * Single-flight refresh. Returns true when the session is usable
   * afterwards. Shared by silent renewal, `auth:expired` handling, and
   * explicit retries — concurrent callers await the same promise (R3).
   */
  readonly refreshNow: (options?: RefreshNowOptions) => Promise<boolean>;
  /**
   * Best-effort server revocation + local clear + cache clear + cross-tab
   * broadcast. Resolves only after clearing, so callers navigate afterwards
   * (R4 ordering).
   */
  readonly logout: () => Promise<void>;
  /** Local-only clear for cross-tab logout broadcasts (no POST, no echo). */
  readonly logoutLocal: () => void;
  /**
   * 401 on an active session: one shared re-resolve, then the caller routes
   * to login on failure. No-op unless currently authenticated (login-form 401s
   * stay local, generic, and never flip global state).
   */
  readonly handleUnauthorized: () => Promise<boolean>;
  /** Test-only reset: cancels timers/promises and restores initial state. */
  readonly resetForTests: () => void;
}

export interface RefreshNowOptions {
  /** How long to wait for reconnect when offline before giving up (ms). */
  readonly offlineTimeoutMs?: number;
}

/** `localStorage` key carrying only a logout timestamp (never token material). */
export const LOGOUT_BROADCAST_KEY = 'dubbing.auth.logout';

const DEFAULT_OFFLINE_TIMEOUT_MS = 8000;

const INITIAL_STATE = {
  status: 'unknown',
  accessToken: undefined,
  refreshToken: undefined,
  expiresAtMs: undefined,
  userId: undefined,
  tenantId: undefined,
  permissions: [],
  lastErrorCode: undefined,
  loginPending: false,
} as const;

/** 80% of the access-token lifetime — the silent-renewal point. */
export function computeRefreshDelayMs(expiresInSeconds: number): number {
  if (!Number.isFinite(expiresInSeconds) || expiresInSeconds <= 0) {
    return 0;
  }
  return Math.floor(expiresInSeconds * 1000 * 0.8);
}

function syncShell(status: AuthStatus, permissions: readonly string[]): void {
  useAppStore.getState().setSession(status, permissions);
}

function isOffline(): boolean {
  return (
    typeof navigator !== 'undefined' && 'onLine' in navigator && navigator.onLine === false
  );
}

/** Resolves true on reconnect, false on timeout. Never throws. */
function waitForOnlineOnce(timeoutMs: number): Promise<boolean> {
  if (!isOffline()) {
    return Promise.resolve(true);
  }
  if (typeof window === 'undefined') {
    return Promise.resolve(false);
  }
  return new Promise<boolean>((resolve) => {
    const timer = window.setTimeout(() => {
      window.removeEventListener('online', onOnline);
      resolve(false);
    }, timeoutMs);
    const onOnline = (): void => {
      window.clearTimeout(timer);
      resolve(true);
    };
    window.addEventListener('online', onOnline, { once: true });
  });
}

let loginPromise: Promise<void> | null = null;
let refreshPromise: Promise<boolean> | null = null;
let restorePromise: Promise<void> | null = null;
let refreshTimer: ReturnType<typeof setTimeout> | undefined;

function cancelRefreshTimer(): void {
  if (refreshTimer !== undefined) {
    clearTimeout(refreshTimer);
    refreshTimer = undefined;
  }
}

function scheduleRefreshTimer(expiresInSeconds: number): void {
  cancelRefreshTimer();
  const delay = computeRefreshDelayMs(expiresInSeconds);
  refreshTimer = setTimeout(() => {
    refreshTimer = undefined;
    void useAuthStore.getState().refreshNow();
  }, delay);
}

/** Broadcasts logout to other tabs (timestamp only — never token material). */
function broadcastLogout(): void {
  try {
    window.localStorage.setItem(LOGOUT_BROADCAST_KEY, String(Date.now()));
  } catch {
    // Storage unavailable: other tabs keep their own in-memory session.
  }
}

function clearLocalState(status: AuthStatus): void {
  cancelRefreshTimer();
  useAuthStore.setState({
    status,
    accessToken: undefined,
    refreshToken: undefined,
    expiresAtMs: undefined,
    userId: undefined,
    tenantId: undefined,
    permissions: [],
    lastErrorCode: undefined,
    loginPending: false,
  });
  syncShell(status, []);
}

export const useAuthStore = create<AuthState>()((set, get) => {
  function markExpired(code: string | undefined): void {
    cancelRefreshTimer();
    const permissions = get().permissions;
    set({ status: 'expired', lastErrorCode: code, loginPending: false });
    syncShell('expired', permissions);
  }

  function markError(code: string | undefined): void {
    cancelRefreshTimer();
    const permissions = get().permissions;
    set({ status: 'error', lastErrorCode: code, loginPending: false });
    syncShell('error', permissions);
  }

  function applyPair(pair: { accessToken: string; refreshToken: string; expiresInSeconds: number; userId: string; tenantId: string }): void {
    set({
      accessToken: pair.accessToken,
      refreshToken: pair.refreshToken,
      expiresAtMs: Date.now() + pair.expiresInSeconds * 1000,
      userId: pair.userId,
      tenantId: pair.tenantId,
    });
    setTokenProvider(() => useAuthStore.getState().accessToken);
    scheduleRefreshTimer(pair.expiresInSeconds);
  }

  function applyAuthenticated(permissions: readonly string[]): void {
    set({ status: 'authenticated', permissions: [...permissions], lastErrorCode: undefined, loginPending: false });
    syncShell('authenticated', permissions);
  }

  async function attemptRefresh(refreshToken: string): Promise<boolean> {
    const pair = await refreshSession(refreshToken);
    applyPair(pair);
    return true;
  }

  async function doRefresh(options?: RefreshNowOptions): Promise<boolean> {
    const state = get();
    const refreshToken = state.refreshToken;
    if (refreshToken === undefined || refreshToken === '') {
      if (state.status === 'authenticated') {
        markExpired(undefined);
      }
      return false;
    }
    try {
      await attemptRefresh(refreshToken);
    } catch (error) {
      const normalized = normalizeError(error, { method: 'POST' });
      if (normalized.code === NETWORK_ERROR && isOffline()) {
        // Offline: one retry on reconnect, else expired (Task 019 edge case).
        const reconnected = await waitForOnlineOnce(options?.offlineTimeoutMs ?? DEFAULT_OFFLINE_TIMEOUT_MS);
        if (reconnected) {
          try {
            await attemptRefresh(get().refreshToken ?? '');
            return refreshNowConfirm();
          } catch {
            markExpired(NETWORK_ERROR);
            return false;
          }
        }
        markExpired(NETWORK_ERROR);
        return false;
      }
      return classifyRefreshFailure(error);
    }
    return refreshNowConfirm();
  }

  /** Re-validates identity after a rotation; failure here expires the session. */
  async function refreshNowConfirm(): Promise<boolean> {
    try {
      const me = await fetchMe();
      applyAuthenticated(me.permissions ?? []);
      return true;
    } catch (error) {
      return classifyRefreshFailure(error);
    }
  }

  function classifyRefreshFailure(error: unknown): boolean {
    const normalized = normalizeError(error, { method: 'POST' });
    if (normalized.status === 401) {
      markExpired(normalized.code);
    } else {
      markError(normalized.code);
    }
    return false;
  }

  async function doLogin(credentials: LoginCredentials): Promise<void> {
    set({ loginPending: true });
    try {
      const pair = await apiLogin(credentials);
      applyPair(pair);
      const me = await fetchMe();
      applyAuthenticated(me.permissions ?? []);
    } catch (error) {
      const normalized = normalizeError(error, { method: 'POST' });
      if (get().accessToken !== undefined && get().accessToken !== '') {
        // Tokens were issued but identity resolution failed (e.g. account
        // disabled mid-login): surface a global error state, not a silent
        // half-session. Pure credential rejections (no tokens) keep the
        // previous status and surface locally in the form.
        markError(normalized.code);
      } else {
        set({ loginPending: false, lastErrorCode: normalized.code });
      }
      throw error;
    }
  }

  async function doRestore(): Promise<void> {
    const state = get();
    if (state.accessToken !== undefined && state.accessToken !== '') {
      try {
        const me = await fetchMe();
        applyAuthenticated(me.permissions ?? []);
        return;
      } catch (error) {
        const normalized = normalizeError(error, { method: 'GET' });
        if (normalized.status === 401 && state.refreshToken) {
          const ok = await get().refreshNow();
          if (ok) {
            return;
          }
          return;
        }
        if (normalized.status === 401) {
          markExpired(normalized.code);
          return;
        }
        markError(normalized.code);
        return;
      }
    }
    if (state.refreshToken !== undefined && state.refreshToken !== '') {
      await get().refreshNow();
      return;
    }
    set({ status: 'anonymous', loginPending: false });
    syncShell('anonymous', []);
  }

  return {
    ...INITIAL_STATE,
    permissions: [...INITIAL_STATE.permissions],

    login: (credentials) => {
      if (loginPromise !== null) {
        return loginPromise;
      }
      const task = doLogin(credentials);
      loginPromise = task;
      task.then(
        () => {
          if (loginPromise === task) {
            loginPromise = null;
          }
        },
        () => {
          if (loginPromise === task) {
            loginPromise = null;
          }
        },
      );
      return task;
    },

    restore: () => {
      if (restorePromise !== null) {
        return restorePromise;
      }
      const task = doRestore();
      restorePromise = task;
      task.then(
        () => {
          if (restorePromise === task) {
            restorePromise = null;
          }
        },
        () => {
          if (restorePromise === task) {
            restorePromise = null;
          }
        },
      );
      return task;
    },

    refreshNow: (options) => {
      if (refreshPromise !== null) {
        return refreshPromise;
      }
      const task = doRefresh(options);
      refreshPromise = task;
      task.then(
        () => {
          if (refreshPromise === task) {
            refreshPromise = null;
          }
        },
        () => {
          if (refreshPromise === task) {
            refreshPromise = null;
          }
        },
      );
      return task;
    },

    logout: async () => {
      const refreshToken = get().refreshToken;
      cancelRefreshTimer();
      await apiLogout(refreshToken);
      queryClient.clear();
      useAppStore.getState().setTenantTimezone(undefined);
      clearLocalState('anonymous');
      broadcastLogout();
    },

    logoutLocal: () => {
      cancelRefreshTimer();
      queryClient.clear();
      useAppStore.getState().setTenantTimezone(undefined);
      clearLocalState('anonymous');
    },

    handleUnauthorized: () => {
      if (get().status !== 'authenticated') {
        return Promise.resolve(false);
      }
      return get().refreshNow();
    },

    resetForTests: () => {
      cancelRefreshTimer();
      loginPromise = null;
      refreshPromise = null;
      restorePromise = null;
      useAuthStore.setState({
        ...INITIAL_STATE,
        permissions: [],
      });
    },
  };
});
