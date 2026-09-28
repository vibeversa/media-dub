import type { ReactNode } from 'react';
import { iconForQuotaState, toneForQuotaState } from './types.js';
import type { QuotaState } from './types.js';

export interface QuotaBadgeProps {
  readonly state: QuotaState;
  readonly remaining?: number;
}

/**
 * Quota badge (Task 035A).
 *
 * Compact quota-state treatment for inline surfaces: distinct tone plus a
 * text glyph (never color alone), state text, and optional remaining count.
 * `exceeded` is the only blocking state (parents read `isQuotaBlocking`).
 * Never renders reservation ids or provider internals.
 */
export function QuotaBadge({ state, remaining }: QuotaBadgeProps): ReactNode {
  const tone = toneForQuotaState(state);
  const icon = iconForQuotaState(state);
  return (
    <span data-testid={`quota-badge-${state}`} data-tone={tone} data-state={state}>
      <span data-testid="quota-badge-icon" aria-hidden="true">
        {icon}
      </span>{' '}
      <span data-testid="quota-badge-state">{state}</span>
      {remaining !== undefined ? (
        <span data-testid="quota-badge-remaining"> · {String(remaining)} remaining</span>
      ) : null}
    </span>
  );
}
