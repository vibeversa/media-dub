import type { InputHTMLAttributes, ReactNode } from 'react';
import { cx } from '../_shared/cx.js';

export interface RadioProps extends InputHTMLAttributes<HTMLInputElement> {
  readonly label: string;
}

/** Native radio with label. */
export function Radio({ label, className, ...rest }: RadioProps): ReactNode {
  return (
    <>
      <label className={cx('dp-radio', className)}>
        <input type="radio" className="dp-focus-ring" {...rest} />
        {label}
      </label>
    </>
  );
}
