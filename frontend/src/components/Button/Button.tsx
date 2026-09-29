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
