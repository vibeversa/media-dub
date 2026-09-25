import type { ReactNode } from 'react';
import { useParams } from 'react-router-dom';
import { VoicesWorkspace } from '../../features/voices/VoicesWorkspace.js';

/** Project voices tab: speaker-to-voice assignment with preview playback (one lazy chunk). */
export default function VoicesPage(): ReactNode {
  const params = useParams();
  return (
    <section data-testid="page-project-voices">
      <VoicesWorkspace projectId={params['id'] ?? ''} />
    </section>
  );
}
