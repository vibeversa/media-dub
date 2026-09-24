import type { ReactNode } from 'react';

export interface QuotaMeterProps {
  readonly used: number;
  readonly quota: number;
  readonly label?: string;
  readonly warningThreshold?: number;
}

/** Usage bar with warning threshold (default 80%). */
export function QuotaMeter({ used, quota, label, warningThreshold = 0.8 }: QuotaMeterProps): ReactNode {
  const safeQuota = Math.max(quota, 1);
  const pct = Math.min(100, Math.max(0, (used / safeQuota) * 100));
  const over = used / safeQuota >= warningThreshold;
  return (
    <>
      <style>{`.dp-quota{display:flex;flex-direction:column;gap:var(--space-1);font-size:var(--font-size-sm)}.dp-quota-bar{block-size:0.5rem;border-radius:var(--radius-full);background-color:var(--color-neutral-status-bg);overflow:hidden}.dp-quota-fill{block-size:100%}.dp-quota-fill[data-over="true"]{background-color:var(--color-warning)}.dp-quota-fill[data-over="false"]{background-color:var(--color-success)}`}</style>
      <div className="dp-quota" role="meter" aria-valuenow={Math.round(pct)} aria-valuemin={0} aria-valuemax={100} aria-label={label ?? 'Quota'}>
        {label ? <span>{label}</span> : null}
        <div className="dp-quota-bar">
          <div className="dp-quota-fill" data-over={over} style={{ inlineSize: `${pct}%` }} />
        </div>
        <span className="dp-muted">
          {used} / {quota}
        </span>
      </div>
    </>
  );
}
