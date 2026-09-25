import { Navigate, createBrowserRouter } from 'react-router-dom';
import type { RouteObject } from 'react-router-dom';
import { RequireAdmin } from './guards/RequireAdmin.js';
import { RequireAuth } from './guards/RequireAuth.js';
import { AppShell } from './layouts/AppShell.js';
import { AuthLayout } from './layouts/AuthLayout.js';
import { ProjectLayout } from './layouts/ProjectLayout.js';
import {
  AdminPage,
  DashboardPage,
  ForbiddenPage,
  LoggedOutPage,
  LoginPage,
  MediaPage,
  NotFoundPage,
  NotificationsPage,
  ProjectCreatePage,
  ProjectDetailsPage,
  ProjectsPage,
  ReviewPage,
  SettingsPage,
  TranscriptPage,
  TranslationPage,
  VoicesPage,
} from './pages/lazy.js';

const PROJECT_TAB_PATHS = [
  'overview',
  'media',
  'transcript',
  'translation',
  'voices',
  'timeline',
  'quality',
  'exports',
  'activity',
] as const;

export const routes: RouteObject[] = [
  // Authenticated shell (Tasks 018/019): RequireAuth blocks until the
  // pre-shell /me resolution settles and preserves the destination as
  // `/login?next=...`; AppShell renders the IA from `getTopNavItems`, and
  // /admin nests under RequireAdmin (route-guarded, never CSS-only hiding).
  // Project tab outlets render the details placeholder until feature tasks
  // fill them; the media tab already hosts the Task 023 resumable uploader,
  // the transcript tab hosts the Task 027 versioned editor, the
  // translation tab hosts the Task 028 side-by-side workspace, and the
  // voices tab hosts the Task 029 assignment workspace.
  {
    path: '/',
    element: <RequireAuth />,
    children: [
      {
        element: <AppShell />,
        children: [
          { index: true, element: <Navigate to="/dashboard" replace /> },
          { path: 'dashboard', element: <DashboardPage /> },
          { path: 'projects', element: <ProjectsPage /> },
          { path: 'projects/new', element: <ProjectCreatePage /> },
          {
            path: 'projects/:id/*',
            element: <ProjectLayout />,
            children: [
              { index: true, element: <ProjectDetailsPage /> },
              { path: 'media', element: <MediaPage /> },
              { path: 'transcript', element: <TranscriptPage /> },
              { path: 'translation', element: <TranslationPage /> },
              { path: 'voices', element: <VoicesPage /> },
              ...PROJECT_TAB_PATHS.filter((tab) => tab !== 'media' && tab !== 'transcript' && tab !== 'translation' && tab !== 'voices').map((tab) => ({
                path: tab,
                element: <ProjectDetailsPage />,
              })),
            ],
          },
          { path: 'review', element: <ReviewPage /> },
          { path: 'notifications', element: <NotificationsPage /> },
          { path: 'settings', element: <SettingsPage /> },
          {
            element: <RequireAdmin />,
            children: [{ path: 'admin', element: <AdminPage /> }],
          },
          { path: '403', element: <ForbiddenPage /> },
        ],
      },
    ],
  },
  {
    path: '/',
    element: <AuthLayout />,
    children: [
      { path: 'login', element: <LoginPage /> },
      { path: 'logged-out', element: <LoggedOutPage /> },
    ],
  },
  { path: '*', element: <NotFoundPage /> },
];

/** Canonical path table; the router test asserts every entry renders. */
export const routePaths: readonly string[] = [
  '/',
  '/dashboard',
  '/projects',
  '/projects/new',
  '/projects/:id/*',
  '/review',
  '/notifications',
  '/settings',
  '/admin',
  '/403',
  '/login',
  '/logged-out',
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
