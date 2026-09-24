import { createContext } from 'react';

export type ToastTone = 'success' | 'error' | 'info';

export interface ToastItem {
  readonly id: number;
  readonly tone: ToastTone;
  readonly message: string;
}

export interface ToastContextValue {
  readonly toasts: readonly ToastItem[];
  readonly push: (tone: ToastTone, message: string) => void;
  readonly dismiss: (id: number) => void;
}

export const ToastContext = createContext<ToastContextValue | null>(null);
