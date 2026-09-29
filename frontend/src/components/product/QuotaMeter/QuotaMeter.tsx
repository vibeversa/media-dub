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
      <div className="dp-quota" role="meter" aria-valuenow={Math.round(pct)} aria-valuemin={0} aria-valuemax={100} aria-label={label ?? 'Quota'}>
        {label ? <span>{label}</span> : null}
        <div className="dp-quota-bar">
          <div className="dp-quota-fill" data-over={over} style={{ inlineSize: `${pct}%` }} />
        </div>
        <span className="dp-muted">
          {used} / {quota}
        </span>
      </div>
  );
}
