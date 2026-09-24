import type { ReactNode } from 'react';

export interface RingProps {
  readonly value: number;
  readonly label?: string;
  readonly size?: number;
}

/** Determinate ring (SVG, token stroke). Static under reduced motion. */
export function Ring({ value, label, size = 48 }: RingProps): ReactNode {
  const pct = Math.min(100, Math.max(0, value));
  const r = 20;
  const c = 2 * Math.PI * r;
  return (
    <div role="progressbar" aria-valuenow={Math.round(pct)} aria-valuemin={0} aria-valuemax={100} aria-label={label ?? 'Progress'}>
      <svg width={size} height={size} viewBox="0 0 48 48" aria-hidden="true">
        <circle cx="24" cy="24" r={r} fill="none" strokeWidth="6" style={{ stroke: 'var(--color-neutral-status-bg)' }} />
        <circle
          cx="24"
          cy="24"
          r={r}
          fill="none"
          strokeWidth="6"
          strokeLinecap="round"
          strokeDasharray={c}
          strokeDashoffset={c - (pct / 100) * c}
          style={{ stroke: 'var(--color-processing)' }}
        />
      </svg>
    </div>
  );
}
