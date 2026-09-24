import type { ReactNode } from 'react';
import { useParams } from 'react-router-dom';

/** Project workspace shell. Segments, speakers, and output land in later tasks. */
export default function ProjectDetailsPage(): ReactNode {
  const params = useParams();
  return (
    <section data-testid="page-project-details">
      <h1 className="text-xl font-semibold">Project</h1>
      <p className="mt-2 text-sm text-slate-600">Project ID: {params['id'] ?? 'unknown'}</p>
    </section>
  );
}
