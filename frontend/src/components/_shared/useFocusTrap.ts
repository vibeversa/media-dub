import { useEffect, useRef } from 'react';

/**
 * True when an element is actually rendered.
 *
 * <p>
 * `HTMLElement.offsetParent` is the obvious check and it is <strong>wrong
 * inside a dialog</strong>. The spec says `offsetParent` is `null` when the
 * element or any ancestor has `position: fixed`, and `Modal`/`Drawer` render
 * their overlay as `position: fixed`. So every control inside a dialog reported
 * `offsetParent === null`, the trap's item list came back empty, and the trap
 * silently did nothing: Tab walked straight out of the dialog into the page
 * behind it.
 * </p>
 *
 * <p>
 * Task 041C found this by measuring it - the dialog looked perfect in a
 * screenshot and in `useFocusTrap`'s own unit test (which renders without the
 * fixed-position overlay), and trapped nothing in the browser.
 * </p>
 *
 * <p>
 * `getClientRects()` is the right primitive: an element with no layout boxes is
 * not rendered, whatever its ancestors' positioning.
 * </p>
 */
function isVisible(element: HTMLElement): boolean {
  if (element === document.activeElement) {
    return true;
  }
  const style = window.getComputedStyle(element);
  if (style.display === 'none' || style.visibility === 'hidden') {
    return false;
  }
  return element.getClientRects().length > 0;
}

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
      )].filter(isVisible);
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
