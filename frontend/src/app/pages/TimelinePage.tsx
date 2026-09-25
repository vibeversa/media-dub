import type { ReactNode } from 'react';
import { useParams } from 'react-router-dom';
import { TimelineWorkspace } from '../../features/timeline/TimelineWorkspace.js';

/** Project timeline tab: renders the Task 030 player + waveform + timeline (one lazy chunk). */
export default function TimelinePage(): ReactNode {
  const params = useParams();
  return (
    <section data-testid="page-project-timeline">
      <TimelineWorkspace projectId={params['id'] ?? ''} />
    </section>
  );
}
