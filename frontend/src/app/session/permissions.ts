/**
 * Session permission helpers (Task 018 seam for Task 019).
 *
 * The task spec gates Admin on `admin:read`; the backend catalog (Task 006,
 * `Permissions.All`, frozen at 12 names) issues `admin.manage` (and
 * `diagnostics.view` for diagnostics readers). All three are accepted here
 * so the shell matches both the spec and the server; endpoints stay
 * authoritative per Tasks 013/036. Task 019 fills the store's permission set
 * from GET /me and calls `useAppStore.getState().setSession(...)`.
 */

/** Permission strings that unlock the Admin IA (any one suffices). */
export const ADMIN_PERMISSION_ALIASES: readonly string[] = ['admin.manage', 'diagnostics.view', 'admin:read'];

/** True when the /me permission set unlocks Admin. Fail-closed on empty input. */
export function hasAdminPermission(permissions: readonly string[]): boolean {
  return permissions.some((p) => ADMIN_PERMISSION_ALIASES.includes(p));
}

/** True for Admin-routed pathnames (used by the route guard). */
export function isAdminPath(pathname: string): boolean {
  return pathname === '/admin' || pathname.startsWith('/admin/');
}
