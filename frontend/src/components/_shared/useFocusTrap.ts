import { useEffect, useRef } from 'react';

/**
 * Traps Tab focus inside `containerRef` while active and restores focus to the
 * element that held it on mount (R3). Used by Modal/Drawer/ConfirmDialog.
 */
export function useFocusTrap(active: boolean, containerRef: React.RefObject<HTMLElement>): void {
  const restoreRef = useRef<Element | null>(null);

  useEffect(() => {
    if (!active) {
      return;
    }
    restoreRef.current = document.activeElement;
    const container = containerRef.current;
    if (container) {
      const first = container.querySelector<HTMLElement>(
        'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])',
      );
      (first ?? container).focus();
    }

    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key !== 'Tab') {
        return;
      }
      const root = containerRef.current;
      if (!root) {
        return;
      }
      const items = [...root.querySelectorAll<HTMLElement>(
        'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      )].filter((el) => el.offsetParent !== null || el === document.activeElement);
      if (items.length === 0) {
        event.preventDefault();
        return;
      }
      const firstItem = items[0] as HTMLElement;
      const lastItem = items[items.length - 1] as HTMLElement;
      if (event.shiftKey && document.activeElement === firstItem) {
        event.preventDefault();
        lastItem.focus();
      } else if (!event.shiftKey && document.activeElement === lastItem) {
        event.preventDefault();
        firstItem.focus();
      }
    };

    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('keydown', onKeyDown);
      const restore = restoreRef.current as HTMLElement | null;
      if (restore && typeof restore.focus === 'function') {
        restore.focus();
      }
    };
  }, [active, containerRef]);
}
