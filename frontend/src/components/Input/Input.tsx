import type { InputHTMLAttributes, ReactNode } from 'react';
import { useId } from 'react';
import { cx } from '../_shared/cx.js';

export interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  readonly label: string;
  readonly error?: string;
  readonly hint?: string;
}

/** Labeled text input with hint/error slots. */
export function Input({ label, error, hint, id, className, ...rest }: InputProps): ReactNode {
  const autoId = useId();
  const fieldId = id ?? `input-${autoId}`;
  const errorId = `${fieldId}-error`;
  const hintId = `${fieldId}-hint`;
  const describedBy = error ? errorId : hint ? hintId : undefined;
  return (
    <>
      <div className={cx('dp-field', className)}>
        <label className="dp-label" htmlFor={fieldId}>
          {label}
        </label>
        <input
          id={fieldId}
          className="dp-input dp-focus-ring"
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy}
          {...rest}
        />
        {error ? (
          <p className="dp-error" id={errorId} role="alert">
            {error}
          </p>
        ) : hint ? (
          <p className="dp-hint" id={hintId}>
            {hint}
          </p>
        ) : null}
      </div>
    </>
  );
}
