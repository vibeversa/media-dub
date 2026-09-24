import type { ReactNode } from 'react';

export interface ProgressBarProps {
  readonly value: number;
  readonly max?: number;
  readonly label?: string;
}

/** Determinate progress; static under reduced motion. */
export function ProgressBar({ value, max = 100, label }: ProgressBarProps): ReactNode {
  const safeMax = Math.max(max, 1);
  const pct = Math.min(100, Math.max(0, (value / safeMax) * 100));
  return (
    <>
      <style>{`.dp-progress{block-size:0.5rem;border-radius:var(--radius-full);background-color:var(--color-neutral-status-bg);overflow:hidden}.dp-progress-fill{block-size:100%;background-color:var(--color-processing);border-radius:var(--radius-full)}`}</style>
      <div role="progressbar" aria-valuenow={Math.round(pct)} aria-valuemin={0} aria-valuemax={100} aria-label={label ?? 'Progress'} className="dp-progress">
        <div className="dp-progress-fill" style={{ inlineSize: `${pct}%` }} />
      </div>
    </>
  );
}
