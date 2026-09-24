/** localStorage key for E2E session seeding (Playwright `@shell`). Absent in production. */
export const E2E_SESSION_KEY = 'dubbing.e2e.session';

export interface E2eSessionSeed {
  readonly status: 'authenticated' | 'anonymous';
  readonly permissions: string[];
}

/**
 * Reads a seeded E2E session (`{ status, permissions }`) when present.
 * Malformed seeds are ignored (fail-closed to the loading state); Task 019's
 * /me resolution overwrites any seed with the live session.
 */
export function readE2eSessionSeed(): E2eSessionSeed | undefined {
  try {
    const raw = window.localStorage.getItem(E2E_SESSION_KEY);
    if (raw === null) {
      return undefined;
    }
    const parsed = JSON.parse(raw) as { status?: unknown; permissions?: unknown };
    if (parsed.status !== 'authenticated' && parsed.status !== 'anonymous') {
      return undefined;
    }
    const permissions = Array.isArray(parsed.permissions)
      ? parsed.permissions.filter((p): p is string => typeof p === 'string')
      : [];
    return { status: parsed.status, permissions };
  } catch {
    return undefined;
  }
}
