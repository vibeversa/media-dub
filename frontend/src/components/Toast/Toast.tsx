import type { ReactNode } from 'react';
import { useCallback, useMemo, useState } from 'react';
import { ToastContext } from './toastContext.js';
import type { ToastItem, ToastTone } from './toastContext.js';

let nextId = 1;

/** Toast queue: caps at 3 visible, dedupes identical messages. */
export function ToastProvider({ children }: { readonly children: ReactNode }): ReactNode {
  const [toasts, setToasts] = useState<readonly ToastItem[]>([]);

  const dismiss = useCallback((id: number): void => {
    setToasts((prev) => prev.filter((t) => t.id !== id));
  }, []);

  const push = useCallback((tone: ToastTone, message: string): void => {
    setToasts((prev) => {
      if (prev.some((t) => t.tone === tone && t.message === message)) {
        return prev;
      }
      const item: ToastItem = { id: nextId++, tone, message };
      return [...prev, item].slice(-3);
    });
  }, []);

  const value = useMemo(() => ({ toasts, push, dismiss }), [toasts, push, dismiss]);

  return (
      <ToastContext.Provider value={value}>
        {children}
        <div className="dp-toasts" role="region" aria-live="polite" aria-label="Notifications">
          {toasts.map((t) => (
            <div key={t.id} className={`dp-toast dp-toast-${t.tone}`}>
              {t.message}
              <button
                type="button"
                aria-label={`Dismiss: ${t.message}`}
                className="dp-focus-ring"
                onClick={() => {
                  dismiss(t.id);
                }}
              >
                Dismiss
              </button>
            </div>
          ))}
        </div>
      </ToastContext.Provider>
  );
}
