import { useEffect } from 'react';
import type { ReactNode } from 'react';
import { AUTH_EXPIRED_EVENT, setTokenProvider } from '../../api/client/index.js';
import { readE2eSessionSeed } from '../session/e2eSeed.js';
import { LOGOUT_BROADCAST_KEY, useAuthStore } from '../../features/auth/authStore.js';
import { ensureRestoreStarted } from '../../features/auth/useSession.js';

interface AuthProviderProps {
  readonly children: ReactNode;
}

/**
 * Auth wiring at app boot (Task 019, instruction 6).
 *
 * - Registers the in-memory token provider with the Task 017 transport (the
 *   provider reads live store state, so login/logout need no re-registration).
 * - Handles the global `auth:expired` event: a single shared re-resolve on
 *   active sessions, ignored otherwise (login-form 401s stay local). The
 *   store coalesces concurrent refreshes, so event storms cannot retry-loop.
 * - Listens for cross-tab logout broadcasts (`storage` event) and clears the
 *   local session without re-posting.
 * - Starts the pre-shell restore once — skipped when an E2E session seed is
 *   present so the hermetic `@shell` suite keeps its seeded session with no
 *   backend.
 */
export function AuthProvider({ children }: AuthProviderProps): ReactNode {
  useEffect(() => {
    setTokenProvider(() => useAuthStore.getState().accessToken);

    const onAuthExpired = (): void => {
      void useAuthStore.getState().handleUnauthorized();
    };
    const onStorage = (event: StorageEvent): void => {
      if (event.key === LOGOUT_BROADCAST_KEY) {
        useAuthStore.getState().logoutLocal();
      }
    };
    window.addEventListener(AUTH_EXPIRED_EVENT, onAuthExpired);
    window.addEventListener('storage', onStorage);
    if (readE2eSessionSeed() === undefined) {
      ensureRestoreStarted();
    }
    return () => {
      window.removeEventListener(AUTH_EXPIRED_EVENT, onAuthExpired);
      window.removeEventListener('storage', onStorage);
    };
  }, []);

  return <>{children}</>;
}
