import type { ReactNode, SelectHTMLAttributes } from 'react';
import { useId } from 'react';
import { cx } from '../_shared/cx.js';

export interface SelectOption {
  readonly value: string;
  readonly label: string;
}

export interface SelectProps extends SelectHTMLAttributes<HTMLSelectElement> {
  readonly label: string;
  readonly options: readonly SelectOption[];
  readonly error?: string;
}

/** Native select (keyboard-operable, screen-reader friendly). */
export function Select({ label, options, error, id, className, ...rest }: SelectProps): ReactNode {
  const autoId = useId();
  const fieldId = id ?? `select-${autoId}`;
  return (
    <>
      <style>{`.dp-select{border:1px solid var(--color-border-strong);border-radius:var(--radius-md);background-color:var(--color-surface);color:var(--color-text);padding:var(--space-2) var(--space-3);font-size:var(--font-size-md)}`}</style>
      <div className={cx('dp-field', className)}>
        <label className="dp-label" htmlFor={fieldId}>
          {label}
        </label>
        <select id={fieldId} className="dp-select dp-focus-ring" aria-invalid={error ? true : undefined} {...rest}>
          {options.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
        {error ? (
          <p className="dp-error" role="alert">
            {error}
          </p>
        ) : null}
      </div>
    </>
  );
}
