import type { ReactNode } from 'react';
import { cx } from '../_shared/cx.js';

export type AlertTone = 'success' | 'warning' | 'error' | 'info';

export interface AlertProps {
  readonly tone: AlertTone;
  readonly title: string;
  readonly children?: ReactNode;
  readonly details?: string;
}

/** Non-dismissible callout. `details` renders as pre-formatted text only (never HTML). */
export function Alert({ tone, title, children, details }: AlertProps): ReactNode {
  return (
    <>
      <style>{`.dp-alert{border:1px solid var(--color-border);border-inline-start-width:4px;border-radius:var(--radius-md);padding:var(--space-3) var(--space-4);background-color:var(--color-surface)}.dp-alert-success{border-inline-start-color:var(--color-success)}.dp-alert-warning{border-inline-start-color:var(--color-warning)}.dp-alert-error{border-inline-start-color:var(--color-error)}.dp-alert-info{border-inline-start-color:var(--color-info)}.dp-alert-title{font-weight:var(--font-weight-semibold);margin:0}.dp-alert-details{white-space:pre-wrap;font-family:var(--font-family-mono);font-size:var(--font-size-sm);color:var(--color-text-muted);margin:var(--space-2) 0 0}`}</style>
      <div role="alert" className={cx('dp-alert', `dp-alert-${tone}`)}>
        <p className="dp-alert-title">{title}</p>
        {children ? <div>{children}</div> : null}
        {details ? <pre className="dp-alert-details">{details}</pre> : null}
      </div>
    </>
  );
}
