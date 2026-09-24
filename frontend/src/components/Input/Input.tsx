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
      <style>{`.dp-field{display:flex;flex-direction:column;gap:var(--space-1)}.dp-label{font-size:var(--font-size-sm);font-weight:var(--font-weight-medium);color:var(--color-text)}.dp-input{border:1px solid var(--color-border-strong);border-radius:var(--radius-md);background-color:var(--color-surface);color:var(--color-text);padding:var(--space-2) var(--space-3);font-size:var(--font-size-md)}.dp-input[aria-invalid="true"]{border-color:var(--color-error)}.dp-hint{font-size:var(--font-size-sm);color:var(--color-text-muted)}.dp-error{font-size:var(--font-size-sm);color:var(--color-error)}`}</style>
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
