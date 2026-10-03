import { useState } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Badge } from '../../components/Badge/Badge.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import {
  EnrichmentPrivacyNote,
  GoneState,
  NotAvailableState,
  UnavailableState,
} from './EnrichmentStates.js';
import { isEnrichmentForbiddenError, isNotProvisionedError, useVideoIntel } from './useEnrichmentQueries.js';
import { resolveSegmentLink } from './types.js';
import type { VideoIntelArtifactView, VideoIntelKind } from './types.js';
import { useTranslation } from 'react-i18next';

/**
 * Video-intelligence artifacts (Task 044, §19.1, R2).
 *
 * THE SEPARATE-ARTIFACT RULE
 * --------------------------
 * Everything this panel renders is a SEPARATE artifact that happens to *link*
 * to core data. It never merges into the transcript or the timeline, and the
 * separation is structural in three places rather than a styling convention:
 *
 * 1. **Data.** `useVideoIntel` reads `queryKeys.enrichment.videoIntel(id)` — a
 *    sibling scope under `['enrichment', …]`, not a child of
 *    `['projects','detail',id,'transcript']`. Neither prefix invalidation can
 *    reach the other, so an enrichment refetch cannot rewrite transcript or
 *    timeline state and a transcript invalidation cannot blank enrichment.
 * 2. **Rendering.** This panel renders its own `<section>`. No enrichment
 *    element is ever a descendant of the transcript or timeline subtrees; the
 *    link below is a route change, not a splice.
 * 3. **Failure.** Every state below returns inside `data-testid="enrichment-*"`
 *    and nothing outside. A 500 here cannot raise an error boundary that wraps
 *    the workspace, and cannot cancel the queries the workspace depends on.
 *
 * Failure behaviour (R5): a 5xx or timeout renders a dismissible
 * `UnavailableState`. The transcript, translation, voices, review and export
 * surfaces are not re-rendered, re-fetched, disabled or reordered as a
 * consequence — this panel simply shows its own failure.
 */

const KIND_LABEL: Readonly<Record<VideoIntelKind | 'other', string>> = Object.freeze({
  'scene-cut': 'Scene cut',
  overlay: 'Detected overlay',
  face: 'Face track',
  'active-speaker': 'Active speaker',
  other: 'Signal',
});

export interface VideoIntelPanelProps {
  readonly projectId: string;
  /** True only once the enrichment flag has resolved to ON. */
  readonly enabled: boolean;
  /**
   * Core segment ids, for link resolution only.
   *
   * Optional and deliberately not fetched here: reading the segment list from
   * inside an enrichment panel would be the merge R2 forbids. When absent,
   * links render as unlinked rather than as gone — a panel that cannot see
   * segments must not declare them deleted.
   */
  readonly knownSegmentIds?: ReadonlySet<string>;
}

export function VideoIntelPanel({ projectId, enabled, knownSegmentIds }: VideoIntelPanelProps): ReactNode {
    const { t } = useTranslation();
const [dismissed, setDismissed] = useState(false);
  const query = useVideoIntel(projectId, enabled);

  // While the flag is resolving the panel is not mounted at all, so this
  // branch is only reachable when the flag resolved ON and the query has not
  // yet produced data.
  if (query.isPending && query.data === undefined) {
    return (
      <section data-testid="enrichment-video-intel" aria-label={t('enrichment:videoIntelPanel.video-intelligence')}>
        <h3 data-testid="enrichment-video-intel-title">{t('enrichment:videoIntelPanel.video-intelligence2')}</h3>
        <div data-testid="enrichment-video-intel-loading">
          <Skeleton lines={3} />
        </div>
      </section>
    );
  }

  if (query.data === undefined) {
    const error = query.error;
    if (isEnrichmentForbiddenError(error)) {
      // No detail whatsoever: a 403 must not leak whether the artifacts exist.
      return (
        <section data-testid="enrichment-video-intel" aria-label={t('enrichment:videoIntelPanel.video-intelligence3')}>
          <h3 data-testid="enrichment-video-intel-title">{t('enrichment:videoIntelPanel.video-intelligence4')}</h3>
          <div data-testid="enrichment-video-intel-forbidden">
            <EmptyState
              title={t('enrichment:videoIntelPanel.video-intelligence-unavailable')}
              description={t('enrichment:videoIntelPanel.you-do-not-have-permission-to')}
            />
          </div>
        </section>
      );
    }
    if (isNotProvisionedError(error)) {
      return (
        <section data-testid="enrichment-video-intel" aria-label={t('enrichment:videoIntelPanel.video-intelligence5')}>
          <h3 data-testid="enrichment-video-intel-title">{t('enrichment:videoIntelPanel.video-intelligence6')}</h3>
          <div data-testid="enrichment-video-intel-not-available">
            <NotAvailableState
              title={t('enrichment:videoIntelPanel.video-intelligence-not-available')}
              description={t('enrichment:videoIntelPanel.scene-cut-and-overlay-detection-is')}
            />
          </div>
        </section>
      );
    }
    if (dismissed) {
      return null;
    }
    return (
      <section data-testid="enrichment-video-intel" aria-label={t('enrichment:videoIntelPanel.video-intelligence7')}>
        <h3 data-testid="enrichment-video-intel-title">{t('enrichment:videoIntelPanel.video-intelligence8')}</h3>
        <div data-testid="enrichment-video-intel-unavailable">
          <UnavailableState
            title={t('enrichment:videoIntelPanel.video-intelligence-unavailable2')}
            message={error?.message ?? t('enrichment:videoIntelPanel.video-intelligence-could-not-be-loaded')}
            correlationId={error?.correlationId}
            onRetry={() => {
              setDismissed(false);
              void query.refetch();
            }}
            retryPending={query.isFetching}
          />
          <button
            type="button"
            className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
            data-testid="enrichment-video-intel-dismiss"
            onClick={() => {
              setDismissed(true);
            }}
          >
            {t('enrichment:videoIntelPanel.dismiss')}
          </button>
        </div>
      </section>
    );
  }

  const view = query.data;
  return (
    <section data-testid="enrichment-video-intel" aria-label={t('enrichment:videoIntelPanel.video-intelligence9')}>
      <h3 data-testid="enrichment-video-intel-title">{t('enrichment:videoIntelPanel.video-intelligence10')}</h3>
      <p data-testid="enrichment-video-intel-separate-note" className="dp-muted">
        {t('enrichment:videoIntelPanel.optional-enrichment-these-are-separate-analysis')}
      </p>
      {view.model === '' ? null : (
        <p data-testid="enrichment-video-intel-model" className="dp-muted">
          {`Model: ${view.model}`}
        </p>
      )}
      {view.empty ? (
        <div data-testid="enrichment-video-intel-empty">
          <EmptyState
            title={t('enrichment:videoIntelPanel.no-video-intelligence-yet')}
            description={t('enrichment:videoIntelPanel.nothing-has-been-analysed-for-this')}
          />
        </div>
      ) : (
        <ul data-testid="enrichment-video-intel-list">
          {view.artifacts.map((artifact) => (
            <ArtifactRow
              key={artifact.id}
              projectId={projectId}
              artifact={artifact}
              knownSegmentIds={knownSegmentIds}
            />
          ))}
        </ul>
      )}
      <EnrichmentPrivacyNote boundary="operator-hosted local inference" />
    </section>
  );
}

interface ArtifactRowProps {
  readonly projectId: string;
  readonly artifact: VideoIntelArtifactView;
  readonly knownSegmentIds: ReadonlySet<string> | undefined;
}

/**
 * One artifact row. Status is carried by text and a badge rather than by
 * colour alone (Task 041C's rule), so a reader who cannot distinguish the
 * badge tone still reads "linked" / "no longer exists".
 */
function ArtifactRow({ projectId, artifact, knownSegmentIds }: ArtifactRowProps): ReactNode {
    const { t } = useTranslation();
const link = resolveSegmentLink(artifact.segmentId, knownSegmentIds);
  const label = artifact.label === '' ? artifact.id : artifact.label;
  return (
    <li data-testid={`enrichment-video-intel-artifact-${artifact.id}`} data-kind={artifact.kind} data-link={link}>
      <span data-testid={`enrichment-video-intel-kind-${artifact.id}`}>
        <Badge tone="neutral">{KIND_LABEL[artifact.kind]}</Badge>
      </span>{' '}
      <span data-testid={`enrichment-video-intel-label-${artifact.id}`}>{label}</span>
      {artifact.detail === '' ? null : (
        <span data-testid={`enrichment-video-intel-detail-${artifact.id}`} className="dp-muted">
          {` — ${artifact.detail}`}
        </span>
      )}
      {artifact.atMs === '' ? null : (
        <span data-testid={`enrichment-video-intel-at-${artifact.id}`} className="dp-muted">
          {` (${artifact.atMs})`}
        </span>
      )}
      {link === 'linked' ? (
        <Link
          data-testid={`enrichment-video-intel-link-${artifact.id}`}
          to={`/projects/${encodeURIComponent(projectId)}/transcript?segment=${encodeURIComponent(artifact.segmentId)}`}
        >
          {t('enrichment:videoIntelPanel.open-segment')}
        </Link>
      ) : null}
      {link === 'gone' ? (
        <span data-testid={`enrichment-video-intel-gone-${artifact.id}`}>
          <GoneState detail="The segment this refers to no longer exists." />
        </span>
      ) : null}
    </li>
  );
}