import type { InputHTMLAttributes, ReactNode } from 'react';
import { cx } from '../_shared/cx.js';

export interface CheckboxProps extends InputHTMLAttributes<HTMLInputElement> {
  readonly label: string;
}

/** Native checkbox with label. */
export function Checkbox({ label, className, ...rest }: CheckboxProps): ReactNode {
  return (
    <>
      <style>{`.dp-check{display:flex;align-items:center;gap:var(--space-2);font-size:var(--font-size-md);color:var(--color-text)}.dp-check input{inline-size:1rem;block-size:1rem;accent-color:var(--color-brand)}`}</style>
      <label className={cx('dp-check', className)}>
        <input type="checkbox" className="dp-focus-ring" {...rest} />
        {label}
      </label>
    </>
  );
}
