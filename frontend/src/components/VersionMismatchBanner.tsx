import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useVersionWatch } from '../hooks/useVersionWatch.js';
import type { UseVersionWatchOptions } from '../hooks/useVersionWatch.js';

export interface VersionMismatchBannerProps extends UseVersionWatchOptions {
  /** Test/storybook override for the whole watch state. */
  readonly state?: {
    readonly match: 'MATCH' | 'MISMATCH' | 'UNKNOWN';
    readonly bakedTag: string;
    readonly servedTag: string | null;
    readonly reload: () => void;
  };
}

/**
 * The stale-bundle prompt (Task 043).
 *
 * Renders nothing unless the CDN is demonstrably serving a different release
 * than the running bundle. `UNKNOWN` renders nothing too, and that is the
 * load-bearing decision: a user who is offline, behind a proxy that strips
 * `/version.json`, or on a deploy that predates the file must not be told their
 * app is out of date when there is no evidence for it. A banner that appears on
 * an inconclusive check trains people to dismiss banners, and the one time it
 * matters is the time it is dismissed.
 *
 * `role="status"` rather than `role="alert"`: this is not urgent, and an alert
 * interrupts whatever the user is doing, which is precisely the thing they would
 * have to redo. The reload is offered, not forced - a forced reload mid-edit
 * loses work, and a banner that could lose a user's draft would not be shipped
 * to fix a white screen.
 */
export function VersionMismatchBanner({ state, ...options }: VersionMismatchBannerProps): ReactNode {
  const { t } = useTranslation();
  const watched = useVersionWatch(options);
  const resolved = state ?? watched;

  if (resolved.match !== 'MISMATCH') {
    return null;
  }

  return (
    <div className="dp-container py-3">
      <div
        role="status"
        aria-live="polite"
        data-testid="version-mismatch-banner"
        className="dp-alert dp-alert-warning flex flex-wrap items-center gap-3"
      >
        <span>
          {t('common:versionMismatch.description', {
            served: resolved.servedTag ?? t('common:versionMismatch.unknownRelease'),
          })}
        </span>
        <button
          type="button"
          data-testid="version-mismatch-reload"
          onClick={resolved.reload}
          className="dp-btn dp-btn-secondary dp-btn-sm dp-focus-ring"
        >
          {t('common:versionMismatch.reload')}
        </button>
      </div>
    </div>
  );
}
