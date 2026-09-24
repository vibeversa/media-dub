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
      <style>{`.dp-iconbtn{display:inline-flex;align-items:center;justify-content:center;inline-size:2rem;block-size:2rem;border-radius:var(--radius-md);border:1px solid var(--color-border);background-color:var(--color-surface);color:var(--color-text);cursor:pointer}.dp-iconbtn:disabled{opacity:.5;cursor:not-allowed}`}</style>
      <button type="button" aria-label={label} className={cx('dp-iconbtn dp-focus-ring', className)} {...rest}>
        {children}
      </button>
    </>
  );
}
