import { Suspense } from 'react';
import { cleanup, render, screen } from '@testing-library/react';
import { RouterProvider, createMemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it } from 'vitest';
import { RouteFallback } from '../RouteFallback.js';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS, routePaths, routes } from '../router.js';

afterEach(() => {
  cleanup();
});

const EXPECTED_PATHS: readonly string[] = [
  '/dashboard',
  '/projects',
  '/projects/:id/*',
  '/review',
  '/notifications',
  '/settings',
  '/admin',
  '/login',
];

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
    const router = createMemoryRouter(routes, {
      initialEntries: ['/dashboard'],
      future: { ...ROUTER_FUTURE_FLAGS },
    });
    render(
      <Suspense fallback={<RouteFallback />}>
        <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
      </Suspense>,
    );
    expect(await screen.findByTestId('page-dashboard')).toBeDefined();
  });

  it('renders unknown paths as NotFound', async () => {
    const router = createMemoryRouter(routes, {
      initialEntries: ['/no-such-page'],
      future: { ...ROUTER_FUTURE_FLAGS },
    });
    render(
      <Suspense fallback={<RouteFallback />}>
        <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
      </Suspense>,
    );
    expect(await screen.findByTestId('page-not-found')).toBeDefined();
  });
});
