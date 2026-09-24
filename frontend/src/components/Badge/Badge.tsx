import type { ReactNode } from 'react';
import { cx } from '../_shared/cx.js';

export type BadgeTone = 'success' | 'warning' | 'error' | 'info' | 'neutral' | 'processing' | 'review' | 'cancelled';

export interface BadgeProps {
  readonly tone?: BadgeTone;
  readonly children: ReactNode;
}

/** Small tonal pill. Status semantics belong to StatusBadge. */
export function Badge({ tone = 'neutral', children }: BadgeProps): ReactNode {
  return (
    <>
      <style>{`.dp-badge{display:inline-flex;align-items:center;border-radius:var(--radius-full);font-size:var(--font-size-xs);font-weight:var(--font-weight-medium);padding:var(--space-1) var(--space-2);border:1px solid transparent}.dp-badge-success{background-color:var(--color-success-bg);color:var(--color-success)}.dp-badge-warning{background-color:var(--color-warning-bg);color:var(--color-warning)}.dp-badge-error{background-color:var(--color-error-bg);color:var(--color-error)}.dp-badge-info{background-color:var(--color-info-bg);color:var(--color-info)}.dp-badge-neutral{background-color:var(--color-neutral-status-bg);color:var(--color-neutral-status)}.dp-badge-processing{background-color:var(--color-processing-bg);color:var(--color-processing)}.dp-badge-review{background-color:var(--color-review-bg);color:var(--color-review)}.dp-badge-cancelled{background-color:var(--color-cancelled-bg);color:var(--color-cancelled)}`}</style>
      <span className={cx('dp-badge', `dp-badge-${tone}`)}>{children}</span>
    </>
  );
}
