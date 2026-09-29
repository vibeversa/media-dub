import type { InputHTMLAttributes, ReactNode } from 'react';
import { cx } from '../_shared/cx.js';

export interface CheckboxProps extends InputHTMLAttributes<HTMLInputElement> {
  readonly label: string;
}

/** Native checkbox with label. */
export function Checkbox({ label, className, ...rest }: CheckboxProps): ReactNode {
  return (
    <>
      <label className={cx('dp-check', className)}>
        <input type="checkbox" className="dp-focus-ring" {...rest} />
        {label}
      </label>
    </>
  );
}
