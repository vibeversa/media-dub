import type { ReactNode } from 'react';
import { cx } from '../_shared/cx.js';

export interface SkeletonProps {
  readonly lines?: number;
  readonly className?: string;
}

/** Loading placeholder (aria-hidden, reduced-motion safe). */
export function Skeleton({ lines = 3, className }: SkeletonProps): ReactNode {
  return (
    <>
      <style>{`.dp-skel{display:flex;flex-direction:column;gap:var(--space-2)}.dp-skel-line{block-size:0.875rem;border-radius:var(--radius-sm);background-color:var(--color-neutral-status-bg)}`}</style>
      <div aria-hidden="true" className={cx('dp-skel', className)}>
        {Array.from({ length: Math.max(lines, 1) }, (_, i) => (
          <div key={i} className="dp-skel-line" />
        ))}
      </div>
    </>
  );
}
