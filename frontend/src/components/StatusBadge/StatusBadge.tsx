import type { ReactNode } from 'react';
import { Badge } from '../Badge/Badge.js';
import { isKnownStatus, statusToVariant, warnUnknownStatus } from './statusMap.js';
import type { BackendStatus } from './statusMap.js';

export interface StatusBadgeProps {
  readonly status: BackendStatus | (string & Record<never, never>);
  readonly label?: string;
}

/** Status visualization. Unknown runtime strings render neutral, never crash. */
export function StatusBadge({ status, label }: StatusBadgeProps): ReactNode {
  if (!isKnownStatus(status)) {
    warnUnknownStatus(status);
    return <Badge tone="neutral">{label ?? status}</Badge>;
  }
  return <Badge tone={statusToVariant(status as BackendStatus)}>{label ?? status}</Badge>;
}
