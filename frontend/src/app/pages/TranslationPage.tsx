import type { ReactNode } from 'react';
import { useParams } from 'react-router-dom';
import { TranslationWorkspace } from '../../features/translation/TranslationWorkspace.js';

/** Project translation tab: renders the Task 028 side-by-side workspace (one lazy chunk). */
export default function TranslationPage(): ReactNode {
  const params = useParams();
  return (
    <section data-testid="page-project-translation">
      <TranslationWorkspace projectId={params['id'] ?? ''} />
    </section>
  );
}
