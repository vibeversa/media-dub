import type { InputHTMLAttributes, ReactNode } from 'react';
import { cx } from '../_shared/cx.js';

export interface RadioProps extends InputHTMLAttributes<HTMLInputElement> {
  readonly label: string;
}

/** Native radio with label. */
export function Radio({ label, className, ...rest }: RadioProps): ReactNode {
  return (
    <>
      <style>{`.dp-radio{display:flex;align-items:center;gap:var(--space-2);font-size:var(--font-size-md);color:var(--color-text)}.dp-radio input{inline-size:1rem;block-size:1rem;accent-color:var(--color-brand)}`}</style>
      <label className={cx('dp-radio', className)}>
        <input type="radio" className="dp-focus-ring" {...rest} />
        {label}
      </label>
    </>
  );
}
