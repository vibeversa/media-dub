import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { cx } from '../_shared/cx.js';

export interface SwitchProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  readonly label: string;
  readonly checked: boolean;
  readonly onCheckedChange?: (next: boolean) => void;
}

/** Accessible switch (role=switch, arrow/space operable). */
export function Switch({ label, checked, onCheckedChange, className, ...rest }: SwitchProps): ReactNode {
  return (
    <>
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        aria-label={label}
        data-on={checked}
        className={cx('dp-switch dp-focus-ring', className)}
        onClick={() => {
          onCheckedChange?.(!checked);
        }}
        {...rest}
      >
        <span className="dp-track" aria-hidden="true">
          <span className="dp-thumb" />
        </span>
        {label}
      </button>
    </>
  );
}
