import type { ReactNode } from 'react';
import { useParams } from 'react-router-dom';
import { TranscriptEditor } from '../../features/transcript/TranscriptEditor.js';

/** Project transcript tab: renders the Task 027 versioned editor (one lazy chunk). */
export default function TranscriptPage(): ReactNode {
  const params = useParams();
  return (
    <section data-testid="page-project-transcript">
      <TranscriptEditor projectId={params['id'] ?? ''} />
    </section>
  );
}
