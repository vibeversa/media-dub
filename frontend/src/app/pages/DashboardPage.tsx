import type { ReactNode } from 'react';

/** Dashboard overview. Feature content lands in later tasks. */
export default function DashboardPage(): ReactNode {
  return (
    <section data-testid="page-dashboard">
      <h1 className="text-xl font-semibold">Dashboard</h1>
      <p className="mt-2 text-sm text-slate-600">Project overview and recent activity.</p>
    </section>
  );
}
