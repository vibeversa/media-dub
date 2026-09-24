import type { ReactNode } from 'react';
import { useParams } from 'react-router-dom';
import { MediaUploader } from '../../features/uploads/MediaUploader.js';

/** Project media tab: renders the Task 023 resumable uploader (one lazy chunk). */
export default function MediaPage(): ReactNode {
  const params = useParams();
  return (
    <section data-testid="page-project-media">
      <MediaUploader projectId={params['id'] ?? ''} />
    </section>
  );
}
