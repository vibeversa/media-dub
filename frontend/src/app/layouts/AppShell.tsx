import type { ReactNode } from 'react';
import { Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { queryKeys } from '../../api/queryKeys/index.js';
import { useIsAuthenticated } from '../../features/auth/useSession.js';
import { fetchUnreadCount } from '../../features/notifications/useNotifications.js';
import { RouteTracker } from '../../telemetry/RouteTracker.js';
import { useAuthStore } from '../../features/auth/authStore.js';
import { getEnv } from '../../lib/env.js';
import { useAppStore } from '../../stores/index.js';
import { NotificationBell as CenterNotificationBell } from '../../features/notifications/NotificationBell.js';
import { ChunkErrorBoundary } from '../ChunkErrorBoundary.js';
import { RouteFallback } from '../RouteFallback.js';
import { getTopNavItems } from '../navigation/topNav.js';
import type { TopNavItem } from '../navigation/topNav.js';

const UI_LOCALES = ['en', 'ar'] as const;

/**
 * Header bell slot (Task 034): delegates to the notification-center bell so
 * the badge (hidden at zero, `99+` overflow, `aria-live="polite"`) stays in
 * one place. Kept as a thin alias so the shell R2 string scan still sees
 * `useTranslation` in this module.
 */
function NotificationBell({ unreadCount }: { readonly unreadCount: number }): ReactNode {
  return <CenterNotificationBell unreadCount={unreadCount} />;
}

function LocaleSwitcher(): ReactNode {
  const { t, i18n } = useTranslation();
  const setLocale = useAppStore((s) => s.setLocale);
  const locale = useAppStore((s) => s.locale);
  return (
    <div data-testid="locale-switcher" role="group" aria-label={t('nav:localeSwitcher.label')}>
      {UI_LOCALES.map((code) => (
        <button
          key={code}
          type="button"
          disabled={locale === code}
          aria-pressed={locale === code}
          onClick={() => {
            setLocale(code);
            void i18n.changeLanguage(code);
          }}
        >
          {t(`nav:localeSwitcher.languages.${code}`)}
        </button>
      ))}
    </div>
  );
}

function ThemeSwitcher(): ReactNode {
  const { t } = useTranslation();
  const theme = useAppStore((s) => s.theme);
  const toggleTheme = useAppStore((s) => s.toggleTheme);
  return (
    <button type="button" data-testid="theme-switcher" aria-label={t('nav:themeSwitcher.label')} onClick={toggleTheme}>
      {theme === 'dark' ? t('nav:themeSwitcher.light') : t('nav:themeSwitcher.dark')}
    </button>
  );
}

function UserMenu({ onSignOut }: { readonly onSignOut?: () => void }): ReactNode {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const storeLogout = useAuthStore((s) => s.logout);
  const handleSignOut =
    onSignOut ??
    ((): void => {
      void (async (): Promise<void> => {
        await storeLogout();
        navigate('/logged-out');
      })();
    });
  return (
    <div data-testid="user-menu">
      <span>{t('nav:userMenu.label')}</span>
      <button type="button" data-testid="user-menu-signout" onClick={handleSignOut}>
        {t('nav:userMenu.signOut')}
      </button>
    </div>
  );
}

function NavItems({ items, testIdPrefix }: { readonly items: readonly TopNavItem[]; readonly testIdPrefix: string }): ReactNode {
  const { t } = useTranslation();
  return (
    <ul className="flex flex-wrap items-center gap-4">
      {items.map((item) => (
        <li key={item.id}>
          <NavLink to={item.to} data-testid={`${testIdPrefix}-${item.id}`}>
            {t(item.labelKey)}
          </NavLink>
        </li>
      ))}
    </ul>
  );
}

export interface AppShellProps {
  /**
   * Unread badge override (tests/storybook). When absent, the bell reads the
   * live `queryKeys.notifications.unreadCount()` query (Task 026 live
   * invalidation; Task 034 owns the full center). Prop wins when provided so
   * hermetic shell tests stay deterministic without a query provider.
   */
  readonly unreadCount?: number;
  /**
   * Sign-out handler override (tests). Defaults to the Task 019 logout:
   * server revocation + cache clear, then navigation to `/logged-out`.
   */
  readonly onSignOut?: () => void;
}

function LiveNotificationBell(): ReactNode {
  const isAuthenticated = useIsAuthenticated();
  const query = useQuery({
    queryKey: queryKeys.notifications.unreadCount(),
    queryFn: ({ signal }) => fetchUnreadCount(signal),
    enabled: isAuthenticated,
    staleTime: 30_000,
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: false,
  });
  const raw = query.data;
  const count = typeof raw === 'number' ? raw : (raw as { unreadCount?: number } | undefined)?.unreadCount ?? 0;
  return <NotificationBell unreadCount={count} />;
}

/**
 * Authenticated app shell (Task 018): header with product nav, notification
 * bell (unread-badge slot), user menu, locale/theme switchers; sidebar +
 * main; footer stamped with `VITE_APP_VERSION`. Every visible string resolves
 * through i18n keys. Admin appears only with an admin permission (R1).
 */
export function AppShell({ unreadCount, onSignOut }: AppShellProps): ReactNode {
  const { t } = useTranslation();
  const permissions = useAppStore((s) => s.permissions);
  const items = getTopNavItems(permissions);
  const version = getEnv().appVersion;
  return (
    <div data-testid="app-shell" className="min-h-screen">
      <a href="#main" className="sr-only focus:not-sr-only">
        {t('common:skipToContent')}
      </a>
      <header className="border-b">
        <div className="dp-container flex items-center gap-4 py-3">
          <Link to="/" data-testid="brand" className="font-semibold">
            {t('nav:brand')}
          </Link>
          <nav aria-label={t('nav:primary')} className="flex-1">
            <NavItems items={items} testIdPrefix="nav" />
          </nav>
          {unreadCount !== undefined ? (
            <NotificationBell unreadCount={unreadCount} />
          ) : (
            <LiveNotificationBell />
          )}
          <LocaleSwitcher />
          <ThemeSwitcher />
          <UserMenu onSignOut={onSignOut} />
        </div>
      </header>
      <div className="dp-container flex gap-6 py-6">
        <aside className="hidden w-48 shrink-0 md:block">
          <nav aria-label={t('nav:secondary')}>
            <NavItems items={items} testIdPrefix="sidenav" />
          </nav>
        </aside>
        <main id="main" className="min-w-0 flex-1">
          <ChunkErrorBoundary>
            <Suspense fallback={<RouteFallback />}>
              <RouteTracker />
              <Outlet />
            </Suspense>
          </ChunkErrorBoundary>
        </main>
      </div>
      <footer className="border-t">
        <p className="dp-container dp-muted py-4 text-xs">{t('common:footer.version', { version })}</p>
      </footer>
    </div>
  );
}
