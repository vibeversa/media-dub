import { useContext } from 'react';
import { ToastContext } from './toastContext.js';
import type { ToastContextValue } from './toastContext.js';

/** Push/dismiss toasts. Must be used inside ToastProvider. */
export function useToast(): ToastContextValue {
  const ctx = useContext(ToastContext);
  if (ctx === null) {
    throw new Error('useToast must be used inside ToastProvider.');
  }
  return ctx;
}
