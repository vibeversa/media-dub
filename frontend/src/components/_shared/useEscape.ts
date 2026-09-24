import { useEffect } from 'react';

/** Calls `onEscape` on Escape keydown while active (Tooltip/Popover/dialogs). */
export function useEscape(active: boolean, onEscape: () => void): void {
  useEffect(() => {
    if (!active) {
      return;
    }
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') {
        onEscape();
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [active, onEscape]);
}
