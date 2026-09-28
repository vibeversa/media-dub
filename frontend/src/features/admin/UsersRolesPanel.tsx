import { useState } from 'react';
import type { ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Alert } from '../../components/Alert/Alert.js';
import { EmptyState } from '../../components/EmptyState/EmptyState.js';
import { ErrorState } from '../../components/ErrorState/ErrorState.js';
import { Skeleton } from '../../components/Skeleton/Skeleton.js';
import { useToast } from '../../components/Toast/useToast.js';
import { useAppStore } from '../../stores/index.js';
import { ASSIGNABLE_ROLES, MIN_AUDIT_REASON_LENGTH, assignerRank, canAssignRole, isAdminForbiddenError, isValidAuditReason, sanitizeReasonText } from './types.js';
import { assignUserRole, invalidateAdminQueries, useAdminUsers } from './useAdminQueries.js';

/**
 * User-role assignment (Task 036, R6).
 *
 * Reads `GET /admin/users` on `queryKeys.adminUsers.list`. Role changes
 * require the assigner to hold a strictly higher grant than the target role
 * (`canAssignRole`) and a mandatory audit reason (min 10 chars, sanitized).
 * Denied grants and short reasons block the submit with an inline
 * explanation — never a silent request. On success the admin scopes are
 * invalidated; on 403 a `ForbiddenState` renders without leaking role names.
 */
export function UsersRolesPanel(): ReactNode {
  const queryClient = useQueryClient();
  const { push } = useToast();
  const permissions = useAppStore((s) => s.permissions);
  const usersQuery = useAdminUsers();
  const [selectedId, setSelectedId] = useState<string | undefined>(undefined);
  const [targetRole, setTargetRole] = useState<string>(ASSIGNABLE_ROLES[ASSIGNABLE_ROLES.length - 1] ?? 'ProjectViewer');
  const [reason, setReason] = useState('');
  const [pending, setPending] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  if (usersQuery.isPending && usersQuery.data === undefined) {
    return (
      <section data-testid="admin-users" aria-label="Users and roles">
        <div data-testid="admin-users-loading">
          <Skeleton lines={4} />
        </div>
      </section>
    );
  }

  if (usersQuery.isError && usersQuery.data === undefined) {
    if (isAdminForbiddenError(usersQuery.error)) {
      return (
        <section data-testid="admin-users" aria-label="Users and roles">
          <div data-testid="admin-users-forbidden">
            <EmptyState
              title="Users unavailable"
              description="You do not have permission to view users. Contact your tenant admin for access."
            />
          </div>
        </section>
      );
    }
    return (
      <section data-testid="admin-users" aria-label="Users and roles">
        <div data-testid="admin-users-error">
          <ErrorState
            title="Users unavailable"
            message={usersQuery.error?.message ?? 'Users could not be loaded. No data was changed.'}
            correlationId={usersQuery.error?.correlationId}
            onRetry={() => {
              void usersQuery.refetch();
            }}
          />
        </div>
      </section>
    );
  }

  const items = usersQuery.data?.items ?? [];
  const notProvisioned = usersQuery.data?.notProvisioned === true;
  if (items.length === 0) {
    return (
      <section data-testid="admin-users" aria-label="Users and roles">
        <div data-testid="admin-users-empty">
          <EmptyState
            title="No users"
            description={notProvisioned ? 'User reads are not provisioned on this backend yet.' : 'No users are visible in this scope.'}
          />
        </div>
      </section>
    );
  }

  const selected = items.find((user) => user.id === selectedId) ?? items[0];
  const grantable = canAssignRole(permissions, targetRole);
  const reasonValid = isValidAuditReason(reason);
  const canSubmit = selected !== undefined && grantable && reasonValid && !pending;

  async function submit(): Promise<void> {
    if (!canSubmit || selected === undefined) {
      return;
    }
    setPending(true);
    setFailure(null);
    try {
      const receipt = await assignUserRole(selected.id, targetRole, sanitizeReasonText(reason), permissions);
      await invalidateAdminQueries(queryClient);
      push('success', `Role ${targetRole} assigned. Receipt ${receipt.actionId}.`);
      setReason('');
      await usersQuery.refetch();
    } catch (error) {
      if (isAdminForbiddenError(error)) {
        setFailure('FORBIDDEN');
      } else {
        setFailure(error instanceof Error && error.message !== '' ? error.message : 'Role assignment failed. No data was changed.');
      }
    } finally {
      setPending(false);
    }
  }

  return (
    <section data-testid="admin-users" aria-label="Users and roles">
      <h3>User roles</h3>
      <p className="dp-muted" data-testid="admin-users-grant-note">
        Assigners must hold a strictly higher grant than the assigned role (your rank: {String(assignerRank(permissions))}).
      </p>
      <ul data-testid="admin-users-list">
        {items.map((user) => (
          <li key={user.id} data-testid={`admin-user-row-${user.id}`}>
            <button
              type="button"
              data-testid={`admin-user-select-${user.id}`}
              className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
              aria-pressed={selected !== undefined && user.id === selected.id}
              onClick={() => {
                setSelectedId(user.id);
                setFailure(null);
              }}
            >
              <span data-testid={`admin-user-name-${user.id}`}>{user.displayName}</span>{' '}
              <span data-testid={`admin-user-roles-${user.id}`}>{user.roles.join(', ') === '' ? 'No roles' : user.roles.join(', ')}</span>
            </button>
          </li>
        ))}
      </ul>
      {selected !== undefined ? (
        <div data-testid="admin-role-assign">
          <h4>Assign role</h4>
          <label htmlFor="admin-role-target">Target role</label>
          <select
            id="admin-role-target"
            data-testid="admin-role-target"
            value={targetRole}
            onChange={(event) => {
              setTargetRole(event.target.value);
              setFailure(null);
            }}
          >
            {ASSIGNABLE_ROLES.map((role) => (
              <option key={role} value={role} data-testid={`admin-role-option-${role}`}>
                {role}
              </option>
            ))}
          </select>
          {!grantable ? (
            <div data-testid="admin-role-grant-denied">
              <Alert tone="error" title="Higher grant required">
                <p data-testid="admin-role-grant-denied-text">
                  Your grant must strictly exceed the assigned role. Ask a higher-grant admin to assign {targetRole}.
                </p>
              </Alert>
            </div>
          ) : null}
          <label htmlFor="admin-role-reason">Audit reason (minimum {String(MIN_AUDIT_REASON_LENGTH)} characters)</label>
          <textarea
            id="admin-role-reason"
            data-testid="admin-role-reason"
            rows={3}
            value={reason}
            onChange={(event) => {
              setReason(event.target.value);
            }}
          />
          {!reasonValid && reason !== '' ? (
            <p data-testid="admin-role-reason-error" role="alert">
              Enter at least {String(MIN_AUDIT_REASON_LENGTH)} characters explaining this assignment.
            </p>
          ) : null}
          {failure !== null ? (
            failure === 'FORBIDDEN' ? (
              <div data-testid="admin-role-forbidden">
                <EmptyState
                  title="Assignment not permitted"
                  description="You do not have permission for this action. Contact your tenant admin for access."
                />
              </div>
            ) : (
              <div data-testid="admin-role-error">
                <Alert tone="error" title="Assignment failed">
                  <p>{failure}</p>
                </Alert>
              </div>
            )
          ) : null}
          <button
            type="button"
            data-testid="admin-role-submit"
            className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
            disabled={!canSubmit}
            onClick={() => {
              void submit();
            }}
          >
            {pending ? 'Assigning…' : 'Assign role'}
          </button>
        </div>
      ) : null}
    </section>
  );
}
