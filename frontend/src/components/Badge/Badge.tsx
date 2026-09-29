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
      <span className={cx('dp-badge', `dp-badge-${tone}`)}>{children}</span>
    </>
  );
}
