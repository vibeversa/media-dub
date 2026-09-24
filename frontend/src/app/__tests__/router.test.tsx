import { Suspense } from 'react';
import { cleanup, render, screen } from '@testing-library/react';
import { RouterProvider, createMemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { RouteFallback } from '../RouteFallback.js';
import { LocaleProvider } from '../providers/LocaleProvider.js';
import { QueryProvider } from '../providers/QueryProvider.js';
import { queryClient } from '../providers/queryClient.js';
import { ToastProvider } from '../../components/Toast/Toast.js';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS, routePaths, routes } from '../router.js';
import { useAppStore } from '../../stores/index.js';

beforeEach(() => {
  // Authenticated shell for route renders; guard-specific states are set
  // per test (Task 018 guards wrap every authenticated route).
  queryClient.clear();
  useAppStore.getState().resetForTests();
  useAppStore.getState().setSession('authenticated', []);
});

afterEach(() => {
  cleanup();
  queryClient.clear();
  useAppStore.getState().resetForTests();
});

const EXPECTED_PATHS: readonly string[] = [
  '/dashboard',
  '/projects',
  '/projects/new',
  '/projects/:id/*',
  '/review',
  '/notifications',
  '/settings',
  '/admin',
  '/login',
];

function renderPath(path: string): void {
  const router = createMemoryRouter(routes, {
    initialEntries: [path],
    future: { ...ROUTER_FUTURE_FLAGS },
  });
  render(
    <QueryProvider>
      <LocaleProvider>
        <ToastProvider>
          <Suspense fallback={<RouteFallback />}>
            <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
          </Suspense>
        </ToastProvider>
      </LocaleProvider>
    </QueryProvider>,
  );
}

describe('route table', () => {
  it('exposes every top-level path', () => {
    for (const path of EXPECTED_PATHS) {
      expect(routePaths).toContain(path);
    }
  });

  it('renders the lazy fallback', () => {
    render(<RouteFallback />);
    expect(screen.getByTestId('route-fallback')).toBeDefined();
  });

  it('renders the dashboard route', async () => {
    renderPath('/dashboard');
    expect(await screen.findByTestId('page-dashboard')).toBeDefined();
  });

  it('renders the project creation wizard route', async () => {
    renderPath('/projects/new');
    expect(await screen.findByTestId('page-project-create')).toBeDefined();
  });

  it('renders the project media tab with the uploader', async () => {
    renderPath('/projects/prj_1/media');
    expect(await screen.findByTestId('page-project-media')).toBeDefined();
    expect(await screen.findByTestId('upload-uploader')).toBeDefined();
  });

  it('renders unknown paths as NotFound', async () => {
    renderPath('/no-such-page');
    expect(await screen.findByTestId('page-not-found')).toBeDefined();
  });

  it('shows the shell skeleton while the session resolves', async () => {
    useAppStore.getState().setSession('loading', []);
    renderPath('/dashboard');
    expect(await screen.findByTestId('session-skeleton')).toBeDefined();
  });

  it('redirects anonymous users to login', async () => {
    useAppStore.getState().setSession('anonymous', []);
    renderPath('/dashboard');
    expect(await screen.findByTestId('page-login')).toBeDefined();
  });

  it('redirects non-admin users away from /admin', async () => {
    renderPath('/admin');
    expect(await screen.findByTestId('page-forbidden')).toBeDefined();
  });
});
