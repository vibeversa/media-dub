import type { ReactNode } from 'react';
import type { ProviderHealth } from '../../../api/client/index.js';
import { StatusBadge } from '../../StatusBadge/StatusBadge.js';

export type ProviderHealthStatus = NonNullable<ProviderHealth['status']>;

export interface ProviderBadgeProps {
  readonly status: ProviderHealthStatus | (string & Record<never, never>);
  readonly provider?: string;
}

/** Provider health badge (healthy/degraded/down + unknown fallback). */
export function ProviderBadge({ status, provider }: ProviderBadgeProps): ReactNode {
  const label = provider ? `${provider}: ${status}` : status;
  return <StatusBadge status={status} label={label} />;
}
