import { Navigate, createBrowserRouter } from 'react-router-dom';
import type { RouteObject } from 'react-router-dom';
import { AuthLayout } from './layouts/AuthLayout.js';
import { RootLayout } from './layouts/RootLayout.js';
import {
  AdminPage,
  DashboardPage,
  LoginPage,
  NotFoundPage,
  NotificationsPage,
  ProjectDetailsPage,
  ProjectsPage,
  ReviewPage,
  SettingsPage,
} from './pages/lazy.js';

export const routes: RouteObject[] = [
  // Every page element above is a React.lazy chunk (see pages/lazy.ts), so
  // `vite build` emits one chunk per route (Task 015, R2). Data loads via
  // TanStack Query inside the pages (Task 017+); no route `loader`s.
  {
    path: '/',
    element: <RootLayout />,
    children: [
      { index: true, element: <Navigate to="/dashboard" replace /> },
      { path: 'dashboard', element: <DashboardPage /> },
      { path: 'projects', element: <ProjectsPage /> },
      { path: 'projects/:id/*', element: <ProjectDetailsPage /> },
      { path: 'review', element: <ReviewPage /> },
      { path: 'notifications', element: <NotificationsPage /> },
      { path: 'settings', element: <SettingsPage /> },
      { path: 'admin', element: <AdminPage /> },
    ],
  },
  {
    path: '/',
    element: <AuthLayout />,
    children: [{ path: 'login', element: <LoginPage /> }],
  },
  { path: '*', element: <NotFoundPage /> },
];

/** Canonical path table; the router test asserts every entry renders. */
export const routePaths: readonly string[] = [
  '/',
  '/dashboard',
  '/projects',
  '/projects/:id/*',
  '/review',
  '/notifications',
  '/settings',
  '/admin',
  '/login',
];

/** Browser router for production. Tests build memory routers from `routes`. */
export function createAppRouter(): ReturnType<typeof createBrowserRouter> {
  return createBrowserRouter(routes, { future: { ...ROUTER_FUTURE_FLAGS } });
}

/** Opt into v7 data-router behaviors early so upgrades stay warning-free. */
export const ROUTER_FUTURE_FLAGS = {
  v7_fetcherPersist: true,
  v7_normalizeFormMethod: true,
  v7_partialHydration: true,
  v7_relativeSplatPath: true,
  v7_skipActionErrorRevalidation: true,
} as const;

/** v7_startTransition lives on <RouterProvider>, not on the router init. */
export const ROUTER_PROVIDER_FUTURE_FLAGS = {
  v7_startTransition: true,
} as const;
