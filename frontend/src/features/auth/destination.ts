/**
 * Destination preservation (Task 019, R2).
 *
 * Expiry and anonymous guards redirect to `/login?next=<destination>`; after
 * a successful login the app navigates back. Only same-origin absolute paths
 * are accepted — anything else (absolute URLs, protocol-relative, blank)
 * falls back to `/dashboard` so a crafted `?next=` can never become an open
 * redirector. Query and hash are preserved; the login path itself is never a
 * valid destination (would loop).
 */

export const DEFAULT_POST_LOGIN_PATH = '/dashboard';

export const LOGIN_PATH = '/login';

/** True for safe same-origin destinations (`/projects/...`, never `//evil`). */
export function isSafeNext(value: string): boolean {
  if (value === '' || !value.startsWith('/')) {
    return false;
  }
  if (value.startsWith('//')) {
    return false;
  }
  if (value === LOGIN_PATH || value.startsWith(`${LOGIN_PATH}/`) || value.startsWith(`${LOGIN_PATH}?`)) {
    return false;
  }
  return true;
}

/**
 * Resolves the post-login destination from a location search string.
 * Returns `/dashboard` for missing, malformed, or unsafe values.
 */
export function getNextPath(search: string): string {
  const params = new URLSearchParams(search.startsWith('?') ? search.slice(1) : search);
  const next = params.get('next');
  if (next === null || next === '') {
    return DEFAULT_POST_LOGIN_PATH;
  }
  let decoded = next;
  try {
    decoded = decodeURIComponent(next);
  } catch {
    return DEFAULT_POST_LOGIN_PATH;
  }
  return isSafeNext(decoded) ? decoded : DEFAULT_POST_LOGIN_PATH;
}

/** Builds `/login?next=<destination>` for guards (destination is encoded). */
export function buildLoginPath(destination: string): string {
  const safe = isSafeNext(destination) ? destination : DEFAULT_POST_LOGIN_PATH;
  return `${LOGIN_PATH}?next=${encodeURIComponent(safe)}`;
}
