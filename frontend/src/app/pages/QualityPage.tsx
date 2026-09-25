import type { ReactNode } from 'react';
import { useParams } from 'react-router-dom';
import { QualityWorkspace } from '../../features/quality/QualityWorkspace.js';

/** Project quality tab: renders the Task 032 QC summary + evidence-backed issues (one lazy chunk). */
export default function QualityPage(): ReactNode {
  const params = useParams();
  return (
    <section data-testid="page-project-quality">
      <QualityWorkspace projectId={params['id'] ?? ''} />
    </section>
  );
}
