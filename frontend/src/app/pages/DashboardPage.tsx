import type { ReactNode } from 'react';
import { DashboardPage as FeatureDashboardPage } from '../../features/dashboard/DashboardPage.js';

/** Dashboard route: renders the Task 020 summary (one lazy chunk). */
export default function DashboardPage(): ReactNode {
  return <FeatureDashboardPage />;
}
