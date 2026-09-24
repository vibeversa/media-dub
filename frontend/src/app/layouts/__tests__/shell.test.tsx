import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { cleanup, render, screen } from '@testing-library/react';
import { RouterProvider, createMemoryRouter } from 'react-router-dom';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS } from '../../router.js';
import { LocaleProvider } from '../../providers/LocaleProvider.js';
import { AppShell } from '../AppShell.js';
import { ProjectLayout } from '../ProjectLayout.js';
import { RequireAdmin } from '../../guards/RequireAdmin.js';
import { getTopNavItems } from '../../navigation/topNav.js';
import { EMPTY_WORKSPACE_STATE, getProjectTabs } from '../../navigation/projectTabs.js';
import { useAppStore } from '../../../stores/index.js';

beforeEach(() => {
  useAppStore.getState().resetForTests();
  useAppStore.getState().setSession('authenticated', []);
});

afterEach(() => {
  cleanup();
  useAppStore.getState().resetForTests();
});

function renderAt(path: string, element: ReactNode) {
  const router = createMemoryRouter([{ path: '/', element }], {
    initialEntries: [path],
    future: { ...ROUTER_FUTURE_FLAGS },
  });
  render(
    <LocaleProvider>
      <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
    </LocaleProvider>,
  );
}

describe('AppShell navigation', () => {
  it('renders every top-level item with translated labels', () => {
    renderAt('/', <AppShell />);
    for (const [testId, label] of [
      ['nav-dashboard', 'Dashboard'],
      ['nav-projects', 'Projects'],
      ['nav-review', 'Review'],
      ['nav-notifications', 'Notifications'],
      ['nav-settings', 'Settings'],
    ] as const) {
      expect(screen.getByTestId(testId).textContent).toBe(label);
    }
    expect(screen.getByTestId('brand').textContent).toBe('Dubbing Platform');
  });

  it('omits Admin without an admin permission (R1: absence, not hiding)', () => {
    renderAt('/', <AppShell />);
    expect(screen.queryByTestId('nav-admin')).toBeNull();
    expect(screen.queryByTestId('sidenav-admin')).toBeNull();
    expect(document.documentElement.innerHTML).not.toContain('/admin');
  });

  it('renders Admin for each accepted admin permission alias', () => {
    for (const permission of ['admin.manage', 'diagnostics.view', 'admin:read']) {
      cleanup();
      useAppStore.getState().setSession('authenticated', [permission]);
      renderAt('/', <AppShell />);
      expect(screen.getByTestId('nav-admin').textContent).toBe('Admin');
      cleanup();
    }
  });

  it('stamps the footer with the app version', () => {
    renderAt('/', <AppShell />);
    expect(screen.getByText(/Version 0\.1\.0-dev/).tagName).toBe('P');
  });

  it('shows the unread badge only with a positive count', () => {
    renderAt('/', <AppShell unreadCount={3} />);
    expect(screen.getByTestId('nav-bell-badge').textContent).toBe('3');
    cleanup();
    renderAt('/', <AppShell />);
    expect(screen.queryByTestId('nav-bell-badge')).toBeNull();
  });
});

describe('RequireAdmin guard', () => {
  function renderAdminRoute() {
    const router = createMemoryRouter(
      [
        {
          element: <RequireAdmin />,
          children: [{ path: '/admin', element: <div data-testid="admin-content" /> }],
        },
        { path: '/403', element: <div data-testid="forbidden-page" /> },
      ],
      { initialEntries: ['/admin'], future: { ...ROUTER_FUTURE_FLAGS } },
    );
    render(<RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />);
  }

  it('redirects non-admin sessions to /403', async () => {
    renderAdminRoute();
    expect(await screen.findByTestId('forbidden-page')).toBeDefined();
    expect(screen.queryByTestId('admin-content')).toBeNull();
  });

  it('renders the outlet for admin sessions', async () => {
    useAppStore.getState().setSession('authenticated', ['admin.manage']);
    renderAdminRoute();
    expect(await screen.findByTestId('admin-content')).toBeDefined();
  });
});

describe('project tab model (R5)', () => {
  it('disables state-gated tabs with reason keys on empty state', () => {
    const tabs = getProjectTabs(EMPTY_WORKSPACE_STATE);
    expect(tabs.map((t) => t.id)).toEqual([
      'overview',
      'media',
      'transcript',
      'translation',
      'voices',
      'timeline',
      'quality',
      'exports',
      'activity',
    ]);
    const byId = new Map(tabs.map((t) => [t.id, t]));
    expect(byId.get('translation')?.disabled).toBe(true);
    expect(byId.get('translation')?.disabledReasonKey).toBe('nav:projectTabs.disabledReason.needsTranscript');
    expect(byId.get('exports')?.disabled).toBe(true);
    expect(byId.get('exports')?.disabledReasonKey).toBe('nav:projectTabs.disabledReason.nothingToExport');
    expect(byId.get('overview')?.disabled).toBe(false);
  });

  it('enables gated tabs and badges counts on live state', () => {
    const tabs = getProjectTabs({ hasMedia: true, hasTranscript: true, exportReadyCount: 2, openReviewCount: 5 });
    const byId = new Map(tabs.map((t) => [t.id, t]));
    expect(byId.get('translation')?.disabled).toBe(false);
    expect(byId.get('exports')?.disabled).toBe(false);
    expect(byId.get('exports')?.badge).toBe('2');
    expect(byId.get('quality')?.badge).toBe('5');
  });

  it('renders disabled tabs as text with tooltips, never dead links', () => {
    renderAt('/', <ProjectLayout />);
    expect(screen.getByTestId('project-layout')).toBeDefined();
    // Disabled on empty state: no link target rendered for translation.
    expect(screen.queryByTestId('project-tab-translation')).toBeNull();
  });
});

describe('topNav registry (R1)', () => {
  it('excludes admin for empty permissions and includes it for admin aliases', () => {
    expect(getTopNavItems([]).some((i) => i.id === 'admin')).toBe(false);
    expect(getTopNavItems(['admin.manage']).some((i) => i.id === 'admin')).toBe(true);
    expect(getTopNavItems(['admin:read']).some((i) => i.id === 'admin')).toBe(true);
  });
});

describe('R2 shell strings via i18n keys', () => {
  const SCANNED = [join('src', 'app', 'layouts', 'AppShell.tsx'), join('src', 'app', 'layouts', 'ProjectLayout.tsx')];
  const GUARD_DIR = join('src', 'app', 'guards');

  function allFiles(): string[] {
    const files = [...SCANNED, join('src', 'app', 'pages', 'ForbiddenPage.tsx')];
    for (const entry of readdirSync(join(process.cwd(), GUARD_DIR))) {
      const full = join(process.cwd(), GUARD_DIR, entry);
      if (statSync(full).isFile() && /\.tsx$/.test(entry)) {
        files.push(join('src', 'app', 'guards', entry));
      }
    }
    return files.map((f) => join(process.cwd(), f));
  }

  it('contains no hardcoded shell/nav string literals', () => {
    const literals = [
      'Dubbing Platform',
      'Dashboard',
      'Projects',
      'Review',
      'Notifications',
      'Settings',
      'Admin',
      'Sign out',
      'Account',
      'Language',
      'Project',
      'Not allowed',
      'Skip to main content',
    ];
    const hits: string[] = [];
    for (const file of allFiles()) {
      const text = readFileSync(file, 'utf8');
      for (const literal of literals) {
        if (text.includes(`>${literal}<`)) {
          hits.push(`${file}: >${literal}<`);
        }
      }
    }
    expect(hits).toEqual([]);
  });

  it('renders shell text through react-i18next', () => {
    for (const file of allFiles()) {
      if (file.endsWith('RequireAuth.tsx') || file.endsWith('RequireAdmin.tsx')) {
        continue;
      }
      expect(readFileSync(file, 'utf8')).toContain('useTranslation');
    }
  });
});
