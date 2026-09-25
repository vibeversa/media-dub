import type { ReactNode } from 'react';
import { useParams } from 'react-router-dom';
import { AuditTimeline } from '../../features/activity/AuditTimeline.js';
import { CostSummary } from '../../features/settings/CostSummary.js';
import { QuotaBanner } from '../../features/settings/QuotaBanner.js';
import { useCostQuota } from '../../features/settings/useCostQuota.js';

/**
 * Project activity tab (Task 035): audit timeline plus cost/quota summary
 * for the open project (one lazy chunk). The timeline owns paginated
 * `GET .../activity`; cost/quota read the dashboard + workspace aggregates.
 * Quota `exceeded` blocks costly actions in this tab via the banner state.
 */
export default function ActivityPage(): ReactNode {
  const params = useParams();
  const projectId = params['id'] ?? '';
  const costQuery = useCostQuota(projectId);
  const quota = costQuery.data?.quota;
  return (
    <section data-testid="page-project-activity">
      <AuditTimeline projectId={projectId} />
      <div className="mt-4">
        <CostSummary projectId={projectId} />
      </div>
      {quota !== undefined ? (
        <div className="mt-4">
          <QuotaBanner state={quota.state} resetsAt={quota.resetsAt} remaining={quota.remaining} />
        </div>
      ) : null}
    </section>
  );
}
