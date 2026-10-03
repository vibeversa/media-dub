import { useState } from 'react';
import type { ReactNode } from 'react';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { isAdminForbiddenError } from './types.js';
import { useAdminTenants } from './useAdminQueries.js';
import { useTranslation } from 'react-i18next';

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
    const { t } = useTranslation();
const tenantsQuery = useAdminTenants();
  const [selectedId, setSelectedId] = useState<string | undefined>(undefined);

  if (tenantsQuery.isPending && tenantsQuery.data === undefined) {
    return (
      <section data-testid="admin-tenants" aria-label={t('admin:tenantsPanel.tenants')}>
        <div data-testid="admin-tenants-loading">
          <Skeleton lines={4} />
        </div>
      </section>
    );
  }

  if (tenantsQuery.isError && tenantsQuery.data === undefined) {
    if (isAdminForbiddenError(tenantsQuery.error)) {
      return (
        <section data-testid="admin-tenants" aria-label={t('admin:tenantsPanel.tenants2')}>
          <div data-testid="admin-tenants-forbidden">
            <EmptyState
              title={t('admin:tenantsPanel.tenants-unavailable')}
              description={t('admin:tenantsPanel.you-do-not-have-permission-to')}
            />
          </div>
        </section>
      );
    }
    return (
      <section data-testid="admin-tenants" aria-label={t('admin:tenantsPanel.tenants3')}>
        <div data-testid="admin-tenants-error">
          <ErrorState
            title={t('admin:tenantsPanel.tenants-unavailable2')}
            message={tenantsQuery.error?.message ?? t('admin:tenantsPanel.tenants-could-not-be-loaded-no')}
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
      <section data-testid="admin-tenants" aria-label={t('admin:tenantsPanel.tenants4')}>
        <div data-testid="admin-tenants-empty">
          <EmptyState
            title={t('admin:tenantsPanel.no-tenants')}
            description={notProvisioned ? 'Tenant reads are not provisioned on this backend yet.' : 'No tenants are visible in this scope.'}
          />
        </div>
      </section>
    );
  }

  const selected = items.find((tenant) => tenant.id === selectedId) ?? items[0];
  return (
    <section data-testid="admin-tenants" aria-label={t('admin:tenantsPanel.tenants5')}>
      <h3>{t('admin:tenantsPanel.tenant-list')}</h3>
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
            <dt>{t('admin:tenantsPanel.tenant-id')}</dt>
            <dd data-testid="admin-tenant-detail-id">{selected.id}</dd>
          </div>
          <div>
            <dt>{t('admin:tenantsPanel.name')}</dt>
            <dd data-testid="admin-tenant-detail-name">{selected.name}</dd>
          </div>
          <div>
            <dt>{t('admin:tenantsPanel.slug')}</dt>
            <dd data-testid="admin-tenant-detail-slug">{selected.slug === '' ? '—' : selected.slug}</dd>
          </div>
        </dl>
      ) : null}
    </section>
  );
}
