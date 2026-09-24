import type { ReactNode } from 'react';

/** Project list. Feature content lands in later tasks. */
export default function ProjectsPage(): ReactNode {
  return (
    <section data-testid="page-projects">
      <h1 className="text-xl font-semibold">Projects</h1>
      <p className="mt-2 text-sm text-slate-600">All dubbing projects in this tenant.</p>
    </section>
  );
}
