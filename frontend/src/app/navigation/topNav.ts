import { hasAdminPermission } from '../session/permissions.js';

/**
 * Top-level information architecture (Task 018). The single source of truth
 * for product navigation: `AppShell` renders from it, `router.tsx` guards
 * from it, and the R1 test asserts Admin absence from it — never CSS-only
 * hiding, never per-page nav copies.
 */
export type TopNavId = 'dashboard' | 'projects' | 'review' | 'notifications' | 'settings' | 'admin';

export interface TopNavItem {
  readonly id: TopNavId;
  readonly labelKey: string;
  readonly to: string;
  /** True only for Admin: rendered + routed solely with admin permission. */
  readonly requiresAdmin: boolean;
}

const TOP_NAV: readonly TopNavItem[] = [
  { id: 'dashboard', labelKey: 'nav:dashboard', to: '/dashboard', requiresAdmin: false },
  { id: 'projects', labelKey: 'nav:projects', to: '/projects', requiresAdmin: false },
  { id: 'review', labelKey: 'nav:review', to: '/review', requiresAdmin: false },
  { id: 'notifications', labelKey: 'nav:notifications', to: '/notifications', requiresAdmin: false },
  { id: 'settings', labelKey: 'nav:settings', to: '/settings', requiresAdmin: false },
  { id: 'admin', labelKey: 'nav:admin', to: '/admin', requiresAdmin: true },
];

/**
 * Resolves visible top-level items for a permission set. Admin appears only
 * with an admin permission; every other item is unconditional. Fail-closed:
 * empty/unknown permissions yield the non-admin set.
 */
export function getTopNavItems(permissions: readonly string[]): readonly TopNavItem[] {
  const admin = hasAdminPermission(permissions);
  return TOP_NAV.filter((item) => (item.requiresAdmin ? admin : true));
}
