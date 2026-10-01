import type { ReactNode } from 'react';
import { useParams } from 'react-router-dom';
import { ProjectEnrichment } from '../../features/enrichment/EnrichmentGate.js';
import { MediaUploader } from '../../features/uploads/MediaUploader.js';

/**
 * Project media tab: renders the Task 023 resumable uploader (one lazy chunk)
 * plus the Task 044 optional enrichment surfaces.
 *
 * WHY THE MEDIA TAB HOSTS ENRICHMENT
 * ----------------------------------
 * Enrichment is derived from the uploaded media, so the media tab is where a
 * reader looks for it. It is also, critically, NOT the transcript or the
 * timeline: R2 requires video-intel results to be separate artifacts that
 * link to core data, and mounting them on those tabs would put them one
 * refactor away from being merged into the core read models. Here they are a
 * sibling section of the uploader, with no shared query key and no shared
 * DOM subtree.
 *
 * The whole `<ProjectEnrichment>` region renders `null` with the flags off, so
 * the media tab of an installation without enrichment is byte-identical to
 * what it was before this task.
 */
export default function MediaPage(): ReactNode {
  const params = useParams();
  const projectId = params['id'] ?? '';
  return (
    <section data-testid="page-project-media">
      <MediaUploader projectId={projectId} />
      <ProjectEnrichment projectId={projectId} />
    </section>
  );
}