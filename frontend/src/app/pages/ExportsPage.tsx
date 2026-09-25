import type { ReactNode } from 'react';
import { useParams } from 'react-router-dom';
import { OutputsPage } from '../../features/exports/OutputsPage.js';

/** Project exports tab: renders the Task 033 output readiness + export jobs (one lazy chunk). */
export default function ExportsPage(): ReactNode {
  const params = useParams();
  return (
    <section data-testid="page-project-exports">
      <OutputsPage projectId={params['id'] ?? ''} />
    </section>
  );
}
