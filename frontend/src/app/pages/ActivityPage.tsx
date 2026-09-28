import type { ReactNode } from 'react';
import { useParams } from 'react-router-dom';
import { AuditTimeline } from '../../features/activity/AuditTimeline.js';
import { CostSummary } from '../../features/cost/CostSummary.js';
import { QuotaBanner } from '../../features/cost/QuotaBanner.js';
import { useCostQuota } from '../../features/cost/useCostQuota.js';

/**
 * Project activity tab (Task 035A): audit timeline plus cost/quota summary
 * for the open project (one lazy chunk). The timeline owns paginated
 * `GET .../activity` on the activity factory scope; cost/quota read the
 * dashboard + workspace aggregates on the cost factory scope. Quota
 * `exceeded` blocks costly actions in this tab via the banner state.
 * Tenant-scoped keys only; SSE invalidation flows through the shared
 * registry for the open project.
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
