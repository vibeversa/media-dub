import { lazy } from 'react';

// Lazy page components, one chunk per route (Task 015, R2). Kept in this
// module so router.tsx only exports route data (see react-refresh rule).
export const DashboardPage = lazy(() => import('./DashboardPage.js'));
export const ProjectsPage = lazy(() => import('./ProjectsPage.js'));
export const ProjectDetailsPage = lazy(() => import('./ProjectDetailsPage.js'));
export const ReviewPage = lazy(() => import('./ReviewPage.js'));
export const NotificationsPage = lazy(() => import('./NotificationsPage.js'));
export const SettingsPage = lazy(() => import('./SettingsPage.js'));
export const AdminPage = lazy(() => import('./AdminPage.js'));
export const ForbiddenPage = lazy(() => import('./ForbiddenPage.js'));
export const LoginPage = lazy(() => import('./LoginPage.js'));
export const NotFoundPage = lazy(() => import('./NotFoundPage.js'));
