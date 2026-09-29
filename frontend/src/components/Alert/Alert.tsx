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
      <div role="alert" className={cx('dp-alert', `dp-alert-${tone}`)}>
        <p className="dp-alert-title">{title}</p>
        {children ? <div>{children}</div> : null}
        {details ? <pre className="dp-alert-details">{details}</pre> : null}
      </div>
    </>
  );
}
