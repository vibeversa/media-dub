import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { cx } from '../_shared/cx.js';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
export type ButtonSize = 'sm' | 'md' | 'lg';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  readonly variant?: ButtonVariant;
  readonly size?: ButtonSize;
  readonly loading?: boolean;
  readonly children: ReactNode;
}

const VARIANT_CLASS: Record<ButtonVariant, string> = {
  primary: 'dp-btn-primary',
  secondary: 'dp-btn-secondary',
  ghost: 'dp-btn-ghost',
  danger: 'dp-btn-danger',
};

const SIZE_CLASS: Record<ButtonSize, string> = {
  sm: 'dp-btn-sm',
  md: 'dp-btn-md',
  lg: 'dp-btn-lg',
};

/** Primary action primitive. Token-backed; keyboard-native `<button>`. */
export function Button({
  variant = 'primary',
  size = 'md',
  loading = false,
  disabled,
  className,
  children,
  ...rest
}: ButtonProps): ReactNode {
  return (
    <>
      <style>{`.dp-btn{display:inline-flex;align-items:center;justify-content:center;gap:var(--space-2);font-weight:var(--font-weight-medium);border-radius:var(--radius-md);border:1px solid transparent;cursor:pointer;transition:opacity 120ms ease}.dp-btn:disabled{opacity:.5;cursor:not-allowed}.dp-btn-primary{background-color:var(--color-brand);color:var(--color-brand-contrast)}.dp-btn-secondary{background-color:var(--color-surface);color:var(--color-text);border-color:var(--color-border-strong)}.dp-btn-ghost{background-color:transparent;color:var(--color-text)}.dp-btn-danger{background-color:var(--color-error);color:var(--color-error-contrast)}.dp-btn-sm{font-size:var(--font-size-sm);padding:var(--space-1) var(--space-3)}.dp-btn-md{font-size:var(--font-size-md);padding:var(--space-2) var(--space-4)}.dp-btn-lg{font-size:var(--font-size-lg);padding:var(--space-3) var(--space-5)}`}</style>
      <button
        type={rest.type ?? 'button'}
        disabled={disabled ?? loading}
        aria-busy={loading}
        className={cx('dp-btn dp-focus-ring', VARIANT_CLASS[variant], SIZE_CLASS[size], className)}
        {...rest}
      >
        {loading ? 'Loading…' : children}
      </button>
    </>
  );
}
