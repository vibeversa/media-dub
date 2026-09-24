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
      <style>{`.dp-switch{display:inline-flex;align-items:center;gap:var(--space-2);cursor:pointer;background:none;border:none;color:var(--color-text);font-size:var(--font-size-md)}.dp-track{inline-size:2.25rem;block-size:1.25rem;border-radius:var(--radius-full);background-color:var(--color-neutral-status-bg);position:relative;flex:none}.dp-thumb{position:absolute;inset-block-start:2px;inset-inline-start:2px;inline-size:1rem;block-size:1rem;border-radius:var(--radius-full);background-color:var(--color-surface);box-shadow:var(--shadow-sm)}.dp-switch[data-on="true"] .dp-track{background-color:var(--color-brand)}.dp-switch[data-on="true"] .dp-thumb{transform:translateX(1rem)}[dir="rtl"] .dp-switch[data-on="true"] .dp-thumb{transform:translateX(-1rem)}`}</style>
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
