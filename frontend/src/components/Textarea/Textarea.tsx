import type { ReactNode, TextareaHTMLAttributes } from 'react';
import { useId } from 'react';
import { cx } from '../_shared/cx.js';

export interface TextareaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  readonly label: string;
  readonly error?: string;
}

/** Labeled multiline input. */
export function Textarea({ label, error, id, className, ...rest }: TextareaProps): ReactNode {
  const autoId = useId();
  const fieldId = id ?? `textarea-${autoId}`;
  return (
    <>
      <style>{`.dp-textarea{border:1px solid var(--color-border-strong);border-radius:var(--radius-md);background-color:var(--color-surface);color:var(--color-text);padding:var(--space-2) var(--space-3);font-size:var(--font-size-md);min-block-size:5rem}`}</style>
      <div className={cx('dp-field', className)}>
        <label className="dp-label" htmlFor={fieldId}>
          {label}
        </label>
        <textarea id={fieldId} className="dp-textarea dp-focus-ring" aria-invalid={error ? true : undefined} {...rest} />
        {error ? (
          <p className="dp-error" role="alert">
            {error}
          </p>
        ) : null}
      </div>
    </>
  );
}
