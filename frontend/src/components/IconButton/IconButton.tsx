import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { cx } from '../_shared/cx.js';

export interface IconButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  readonly label: string;
  readonly children: ReactNode;
}

/** Icon-only button with accessible label. */
export function IconButton({ label, className, children, ...rest }: IconButtonProps): ReactNode {
  return (
    <>
      <button type="button" aria-label={label} className={cx('dp-iconbtn dp-focus-ring', className)} {...rest}>
        {children}
      </button>
    </>
  );
}
