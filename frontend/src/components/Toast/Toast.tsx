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
    <>
      <style>{`.dp-toasts{position:fixed;inset-block-end:var(--space-4);inset-inline-end:var(--space-4);z-index:60;display:flex;flex-direction:column;gap:var(--space-2);max-inline-size:min(24rem,90vw)}.dp-toast{background-color:var(--color-surface-overlay);border:1px solid var(--color-border);border-radius:var(--radius-md);box-shadow:var(--shadow-md);padding:var(--space-3) var(--space-4);font-size:var(--font-size-sm);color:var(--color-text)}.dp-toast-success{border-inline-start:4px solid var(--color-success)}.dp-toast-error{border-inline-start:4px solid var(--color-error)}.dp-toast-info{border-inline-start:4px solid var(--color-info)}`}</style>
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
    </>
  );
}
