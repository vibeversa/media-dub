import type { ReactNode } from 'react';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { HealthRoutesPanel } from './HealthRoutesPanel.js';
import { LocalGpuPanel } from './LocalGpuPanel.js';
import { OpsDashboard } from './OpsDashboard.js';
import { RetentionAuditPanel } from './RetentionAuditPanel.js';
import { FlagsPanel } from './FlagsPanel.js';
import { TenantsPanel } from './TenantsPanel.js';
import { UsageQuotasPanel } from './UsageQuotasPanel.js';
import { UsersRolesPanel } from './UsersRolesPanel.js';
import { useAdminGuard } from './adminGuard.js';
import { EnrichmentGate } from '../enrichment/EnrichmentGate.js';

/**
 * Role-gated Admin area (Task 036).
 *
 * Renders all seven admin areas (tenants / users+roles / health+routes /
 * usage+quotas / retention / audit / flags) plus the operator diagnostics
 * dashboard. The section guard (`useAdminGuard`) renders a `ForbiddenState`
 * for non-elevated users — never a redirect loop — and logs the denial to
 * telemetry without user ids. Panels own their loading/empty/failure states
 * and lock individually on 403 (revoked mid-session) with a toast while the
 * session stays intact; the server re-authorizes every query.
 *
 * Task 044 adds the operator-only local-GPU section. It is mounted inside the
 * existing guard — never as a sibling of it — so an ordinary user cannot reach
 * it by any route, and the panel re-checks the elevated permission itself so a
 * future refactor cannot move it out from under that check. With the
 * `localGpu` flag off it renders `null`: no heading, no placeholder, nothing.
 */
export function AdminPage(): ReactNode {
  const guard = useAdminGuard();

  if (guard.isPending) {
    return (
      <section data-testid="admin-page" aria-label="Admin">
        <div data-testid="admin-loading">
          <Skeleton lines={6} />
        </div>
      </section>
    );
  }

  if (!guard.allowed) {
    return (
      <section data-testid="admin-page" aria-label="Admin">
        <div data-testid="admin-forbidden">
          <EmptyState
            title="Admin unavailable"
            description="You do not have permission to view the admin area. Contact your tenant admin for access."
          />
        </div>
      </section>
    );
  }

  return (
    <section data-testid="admin-page" aria-label="Admin">
      <h2>Admin</h2>
      <p className="dp-muted">Tenants, usage, provider health, and operator diagnostics.</p>
      <div data-testid="admin-section-tenants">
        <TenantsPanel />
      </div>
      <div data-testid="admin-section-users">
        <UsersRolesPanel />
      </div>
      <div data-testid="admin-section-health">
        <HealthRoutesPanel />
      </div>
      <div data-testid="admin-section-usage">
        <UsageQuotasPanel />
      </div>
      <div data-testid="admin-section-retention-audit">
        <RetentionAuditPanel />
      </div>
      <div data-testid="admin-section-flags">
        <FlagsPanel />
      </div>
      <div data-testid="admin-section-ops">
        <OpsDashboard />
      </div>
      {/* Task 044: operator-only. The gate wraps the WRAPPER as well as the
          panel, so with the `localGpu` flag off not even this div is emitted —
          R1 asks for zero chrome, and an empty container is chrome with
          nothing in it. `LocalGpuPanel` also re-checks the flag and the
          elevated permission itself, so moving it out from under the gate
          would not expose device detail. */}
      <EnrichmentGate flag="localGpu">
        <div data-testid="admin-section-local-gpu">
          <LocalGpuPanel />
        </div>
      </EnrichmentGate>
    </section>
  );
}
