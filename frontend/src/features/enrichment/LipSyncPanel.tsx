import { useState } from 'react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Alert } from '../../components/Alert/Alert.js';
import { Badge } from '../../components/Badge/Badge.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { fetchLipSyncAssetUrl, isEnrichmentForbiddenError, isNotProvisionedError, normalizeDownloadError, useLipSync } from './useEnrichmentQueries.js';
import { EnrichmentPrivacyNote, NotAvailableState, UnavailableState } from './EnrichmentStates.js';
import { formatEnrichmentFileSize, formatLipSyncScore, resolveSegmentLink } from './types.js';
import type { AppError } from '../../api/errors/index.js';
import type { LipSyncAssetView, LipSyncSegmentView } from './types.js';
import { useTranslation } from 'react-i18next';

/**
 * Lip-sync scores and the separate transformed asset (Task 044, §19.2, R3).
 *
 * R3 HAS TWO HALVES AND BOTH ARE VISIBLE
 * --------------------------------------
 * A score alone is a claim without provenance; an asset alone is a file with no
 * explanation of what it is. So:
 *
 * 1. **Every score carries its method note** (`heuristic v1` by default, or the
 *    backend's own value when it supplies one). The note renders on the same
 *    row as the number, never in a tooltip and never in a legend the reader has
 *    to go and find.
 * 2. **The transformed asset is a separate download**, distinct from the core
 *    outputs of Task 033: a different route, a different query key, its own
 *    click-time fetch, and no appearance in the outputs or exports lists. It
 *    uses the shared `fetchSignedDownloadUrl` mechanism rather than a second
 *    implementation, so the 15-minute / tenant-bound / never-logged rules of
 *    Task 037 hold by construction rather than by discipline.
 *
 * SCORE WITHOUT ASSET
 * -------------------
 * A payload can score a segment and publish no transformed file. The score
 * stays — it is real data — and the download is hidden WITH A REASON. Removing
 * the whole row would discard a valid number; rendering a dead button would
 * look like a bug.
 */

export interface LipSyncPanelProps {
  readonly projectId: string;
  readonly enabled: boolean;
  readonly knownSegmentIds?: ReadonlySet<string>;
}

export function LipSyncPanel({ projectId, enabled, knownSegmentIds }: LipSyncPanelProps): ReactNode {
    const { t } = useTranslation();
const [dismissed, setDismissed] = useState(false);
  const query = useLipSync(projectId, enabled);

  if (query.isPending && query.data === undefined) {
    return (
      <section data-testid="enrichment-lip-sync" aria-label={t('enrichment:lipSyncPanel.lip-sync')}>
        <h3 data-testid="enrichment-lip-sync-title">{t('enrichment:lipSyncPanel.lip-sync2')}</h3>
        <div data-testid="enrichment-lip-sync-loading">
          <Skeleton lines={3} />
        </div>
      </section>
    );
  }

  if (query.data === undefined) {
    const error = query.error;
    if (isEnrichmentForbiddenError(error)) {
      return (
        <section data-testid="enrichment-lip-sync" aria-label={t('enrichment:lipSyncPanel.lip-sync3')}>
          <h3 data-testid="enrichment-lip-sync-title">{t('enrichment:lipSyncPanel.lip-sync4')}</h3>
          <div data-testid="enrichment-lip-sync-forbidden">
            <EmptyState
              title={t('enrichment:lipSyncPanel.lip-sync-unavailable')}
              description={t('enrichment:lipSyncPanel.you-do-not-have-permission-to')}
            />
          </div>
        </section>
      );
    }
    if (isNotProvisionedError(error)) {
      return (
        <section data-testid="enrichment-lip-sync" aria-label={t('enrichment:lipSyncPanel.lip-sync5')}>
          <h3 data-testid="enrichment-lip-sync-title">{t('enrichment:lipSyncPanel.lip-sync6')}</h3>
          <div data-testid="enrichment-lip-sync-not-available">
            <NotAvailableState
              title={t('enrichment:lipSyncPanel.lip-sync-not-available')}
              description={t('enrichment:lipSyncPanel.lip-sync-scoring-is-an-operator')}
            />
          </div>
        </section>
      );
    }
    if (dismissed) {
      return null;
    }
    return (
      <section data-testid="enrichment-lip-sync" aria-label={t('enrichment:lipSyncPanel.lip-sync7')}>
        <h3 data-testid="enrichment-lip-sync-title">{t('enrichment:lipSyncPanel.lip-sync8')}</h3>
        <div data-testid="enrichment-lip-sync-unavailable">
          <UnavailableState
            title={t('enrichment:lipSyncPanel.lip-sync-unavailable2')}
            message={error?.message ?? t('enrichment:lipSyncPanel.lip-sync-scores-could-not-be')}
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
            data-testid="enrichment-lip-sync-dismiss"
            onClick={() => {
              setDismissed(true);
            }}
          >
            {t('enrichment:lipSyncPanel.dismiss')}
          </button>
        </div>
      </section>
    );
  }

  const view = query.data;
  return (
    <section data-testid="enrichment-lip-sync" aria-label={t('enrichment:lipSyncPanel.lip-sync9')}>
      <h3 data-testid="enrichment-lip-sync-title">{t('enrichment:lipSyncPanel.lip-sync10')}</h3>
      <p data-testid="enrichment-lip-sync-separate-note" className="dp-muted">
        {t('enrichment:lipSyncPanel.optional-enrichment-the-transformed-asset-below')}
      </p>
      {view.overallScore !== undefined ? (
        <p data-testid="enrichment-lip-sync-overall">
          {`Overall: ${formatLipSyncScore(view.overallScore)} (${view.overallMethod})`}
        </p>
      ) : null}
      {view.empty ? (
        <div data-testid="enrichment-lip-sync-empty">
          <EmptyState
            title={t('enrichment:lipSyncPanel.no-lip-sync-scores-yet')}
            description={t('enrichment:lipSyncPanel.nothing-has-been-scored-for-this')}
          />
        </div>
      ) : (
        <ul data-testid="enrichment-lip-sync-list">
          {view.segments.map((segment) => (
            <SegmentRow
              key={segment.segmentId}
              projectId={projectId}
              segment={segment}
              knownSegmentIds={knownSegmentIds}
            />
          ))}
        </ul>
      )}
      {view.scoreWithoutAsset ? (
        <div data-testid="enrichment-lip-sync-no-asset">
          <Alert tone="info" title={t('enrichment:lipSyncPanel.scores-available-transformed-asset-not-published')}>
            <p>{t('enrichment:lipSyncPanel.scores-were-computed-but-no-transformed')}</p>
          </Alert>
        </div>
      ) : null}
      <LipSyncAssetDownload projectId={projectId} asset={view.asset} />
      <EnrichmentPrivacyNote boundary="operator-hosted local inference" />
    </section>
  );
}

interface SegmentRowProps {
  readonly projectId: string;
  readonly segment: LipSyncSegmentView;
  readonly knownSegmentIds: ReadonlySet<string> | undefined;
}

function SegmentRow({ projectId, segment, knownSegmentIds }: SegmentRowProps): ReactNode {
    const { t } = useTranslation();
const link = resolveSegmentLink(segment.segmentId, knownSegmentIds);
  const hasScore = segment.score !== undefined;
  return (
    <li
      data-testid={`enrichment-lip-sync-segment-${segment.segmentId}`}
      data-score-present={hasScore ? 'true' : 'false'}
      data-asset-available={segment.assetAvailable ? 'true' : 'false'}
    >
      <span data-testid={`enrichment-lip-sync-score-${segment.segmentId}`}>
        <Badge tone={hasScore ? 'success' : 'neutral'}>{formatLipSyncScore(segment.score)}</Badge>
      </span>{' '}
      {/* The method note is on the same row as the number, always. */}
      <span data-testid={`enrichment-lip-sync-method-${segment.segmentId}`} className="dp-muted">
        {segment.method}
      </span>
      {link === 'linked' ? (
        <Link
          data-testid={`enrichment-lip-sync-link-${segment.segmentId}`}
          to={`/projects/${encodeURIComponent(projectId)}/transcript?segment=${encodeURIComponent(segment.segmentId)}`}
        >
          {t('enrichment:lipSyncPanel.open-segment')}
        </Link>
      ) : null}
      {link === 'gone' ? (
        <span data-testid={`enrichment-lip-sync-gone-${segment.segmentId}`} className="dp-muted">
          {t('enrichment:lipSyncPanel.the-segment-this-score-refers-to')}
        </span>
      ) : null}
      {hasScore && !segment.assetAvailable ? (
        <span data-testid={`enrichment-lip-sync-asset-missing-${segment.segmentId}`} className="dp-muted">
          {` ${segment.assetUnavailableReason}`}
        </span>
      ) : null}
    </li>
  );
}

/**
 * The separate transformed-asset download.
 *
 * Signed URLs are fetched here, in the click handler, and nowhere else: no
 * query, no cache entry, no `href` in the markup, no state. The `<a>` carries
 * `href="#download"` and `preventDefault` so the element is a real link
 * (middle-click and keyboard activation both work) while the URL only ever
 * exists inside this handler for the length of one click.
 */
function LipSyncAssetDownload({
  projectId,
  asset,
}: {
  readonly projectId: string;
  readonly asset: LipSyncAssetView | undefined;
}): ReactNode {
    const { t } = useTranslation();
const [pending, setPending] = useState(false);
  const [error, setError] = useState<AppError | undefined>(undefined);
  const size = formatEnrichmentFileSize(asset?.sizeBytes);

  async function handleDownload(event: React.MouseEvent<HTMLAnchorElement>): Promise<void> {
    event.preventDefault();
    if (pending) {
      return;
    }
    setPending(true);
    setError(undefined);
    try {
      const { url } = await fetchLipSyncAssetUrl(projectId);
      const { triggerBrowserDownload } = await import('../../api/signedDownload/index.js');
      triggerBrowserDownload(url, `lipsync-${projectId}.wav`);
    } catch (caught) {
      setError(normalizeDownloadError(caught));
    } finally {
      setPending(false);
    }
  }

  return (
    <div data-testid="enrichment-lip-sync-asset">
      <a
        data-testid="enrichment-lip-sync-asset-download"
        download
        href="#download"
        onClick={(event) => {
          void handleDownload(event);
        }}
      >
        {pending ? 'Preparing download…' : 'Download transformed lip-sync asset'}
      </a>
      {asset !== undefined ? (
        <p data-testid="enrichment-lip-sync-asset-meta" className="dp-muted">
          {asset.format === '' ? 'Transformed asset' : `Transformed asset (.${asset.format})`}
          {size === undefined ? '' : ` — ${size}`}
        </p>
      ) : null}
      <p data-testid="enrichment-lip-sync-asset-note" className="dp-muted">
        {t('enrichment:lipSyncPanel.a-separate-file-from-your-project')}
      </p>
      {error !== undefined ? (
        <div data-testid="enrichment-lip-sync-asset-error">
          <Alert
            tone="error"
            title={t('enrichment:lipSyncPanel.lip-sync-download-failed')}
            details={error.correlationId === '' ? undefined : `Ref: ${error.correlationId}`}
          >
            <p data-testid="enrichment-lip-sync-asset-error-message">{error.message}</p>
            <button
              type="button"
              className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
              data-testid="enrichment-lip-sync-asset-retry"
              onClick={(event) => {
                event.preventDefault();
                setError(undefined);
              }}
            >
              {t('enrichment:lipSyncPanel.retry-download')}
            </button>
          </Alert>
        </div>
      ) : null}
    </div>
  );
}