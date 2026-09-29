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
