import { useState } from 'react';
import type { ReactNode } from 'react';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { isAdminForbiddenError } from './types.js';
import { useAdminTenants } from './useAdminQueries.js';

/**
 * Tenant list/detail (Task 036).
 *
 * Reads `GET /admin/tenants` on `queryKeys.adminTenants.list`. When the
 * backend has not provisioned the read, the panel renders an `EmptyState`
 * (never a failure); 403 renders `ForbiddenState` without leaking role
 * names; other failures render `ErrorState` with retry. Secret-free: only
 * id/name/slug survive parsing.
 */
export function TenantsPanel(): ReactNode {
  const tenantsQuery = useAdminTenants();
  const [selectedId, setSelectedId] = useState<string | undefined>(undefined);

  if (tenantsQuery.isPending && tenantsQuery.data === undefined) {
    return (
      <section data-testid="admin-tenants" aria-label="Tenants">
        <div data-testid="admin-tenants-loading">
          <Skeleton lines={4} />
        </div>
      </section>
    );
  }

  if (tenantsQuery.isError && tenantsQuery.data === undefined) {
    if (isAdminForbiddenError(tenantsQuery.error)) {
      return (
        <section data-testid="admin-tenants" aria-label="Tenants">
          <div data-testid="admin-tenants-forbidden">
            <EmptyState
              title="Tenants unavailable"
              description="You do not have permission to view tenants. Contact your tenant admin for access."
            />
          </div>
        </section>
      );
    }
    return (
      <section data-testid="admin-tenants" aria-label="Tenants">
        <div data-testid="admin-tenants-error">
          <ErrorState
            title="Tenants unavailable"
            message={tenantsQuery.error?.message ?? 'Tenants could not be loaded. No data was changed.'}
            correlationId={tenantsQuery.error?.correlationId}
            onRetry={() => {
              void tenantsQuery.refetch();
            }}
          />
        </div>
      </section>
    );
  }

  const items = tenantsQuery.data?.items ?? [];
  const notProvisioned = tenantsQuery.data?.notProvisioned === true;
  if (items.length === 0) {
    return (
      <section data-testid="admin-tenants" aria-label="Tenants">
        <div data-testid="admin-tenants-empty">
          <EmptyState
            title="No tenants"
            description={notProvisioned ? 'Tenant reads are not provisioned on this backend yet.' : 'No tenants are visible in this scope.'}
          />
        </div>
      </section>
    );
  }

  const selected = items.find((tenant) => tenant.id === selectedId) ?? items[0];
  return (
    <section data-testid="admin-tenants" aria-label="Tenants">
      <h3>Tenant list</h3>
      <ul data-testid="admin-tenants-list">
        {items.map((tenant) => (
          <li key={tenant.id} data-testid={`admin-tenant-row-${tenant.id}`}>
            <button
              type="button"
              data-testid={`admin-tenant-select-${tenant.id}`}
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              aria-pressed={selected !== undefined && tenant.id === selected.id}
              onClick={() => {
                setSelectedId(tenant.id);
              }}
            >
              <span data-testid={`admin-tenant-name-${tenant.id}`}>{tenant.name}</span>
            </button>
          </li>
        ))}
      </ul>
      {selected !== undefined ? (
        <dl data-testid="admin-tenant-detail">
          <div>
            <dt>Tenant id</dt>
            <dd data-testid="admin-tenant-detail-id">{selected.id}</dd>
          </div>
          <div>
            <dt>Name</dt>
            <dd data-testid="admin-tenant-detail-name">{selected.name}</dd>
          </div>
          <div>
            <dt>Slug</dt>
            <dd data-testid="admin-tenant-detail-slug">{selected.slug === '' ? '—' : selected.slug}</dd>
          </div>
        </dl>
      ) : null}
    </section>
  );
}
