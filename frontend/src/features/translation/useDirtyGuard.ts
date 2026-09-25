import { useCallback, useState } from 'react';

export interface UseDirtyGuardOptions {
  /** True while the draft holds unsaved changes. */
  readonly isDirty: boolean;
  /**
   * Persists the draft (manual-version flow). Return `false` to stay on the
   * current segment/route (e.g. 409 conflict keeps the draft); any other
   * result proceeds with the pending navigation.
   */
  readonly onSave: () => Promise<boolean | void> | boolean | void;
  /** Discards the draft and resets to the selected version. */
  readonly onDiscard: () => void;
}

export interface DirtyGuard {
  readonly isDirty: boolean;
  readonly dialogOpen: boolean;
  readonly pendingLabel: string | undefined;
  /**
   * Requests navigation (segment, route, or tab change). When clean the
   * callback runs immediately; when dirty the save/discard/cancel dialog
   * opens and the callback is held until the user resolves it.
   */
  readonly requestLeave: (next: () => void, label?: string) => void;
  /** Save → manual-version flow, then follow the pending navigation on success. */
  readonly confirmSave: () => Promise<void>;
  /** Discard → reset the draft, then follow the pending navigation. */
  readonly confirmDiscard: () => void;
  /** Cancel → stay (drop the pending navigation, keep the draft). */
  readonly confirmCancel: () => void;
}

/**
 * Navigate-away guard for dirty translation drafts (Task 028, R3).
 *
 * Dirty drafts always trigger save/discard/cancel on segment, route, or tab
 * change — never silent loss, never silent overwrite. `requestLeave` is the
 * single gate: every segment select and every workspace leave link routes
 * through it. Save delegates to the manual-version flow; a `false` save
 * result (conflict/validation) keeps the user in place with the draft
 * preserved.
 */
export function useDirtyGuard(options: UseDirtyGuardOptions): DirtyGuard {
  const [pending, setPending] = useState<(() => void) | undefined>(undefined);
  const [pendingLabel, setPendingLabel] = useState<string | undefined>(undefined);
  const [saving, setSaving] = useState(false);

  const requestLeave = useCallback(
    (next: () => void, label?: string) => {
      if (!options.isDirty) {
        next();
        return;
      }
      setPending(() => next);
      setPendingLabel(label);
    },
    [options.isDirty],
  );

  const confirmSave = useCallback(async (): Promise<void> => {
    if (saving) {
      return;
    }
    setSaving(true);
    try {
      const result = await options.onSave();
      if (result === false) {
        return;
      }
      const next = pending;
      setPending(undefined);
      setPendingLabel(undefined);
      if (next !== undefined) {
        next();
      }
    } finally {
      setSaving(false);
    }
  }, [options, pending, saving]);

  const confirmDiscard = useCallback((): void => {
    options.onDiscard();
    const next = pending;
    setPending(undefined);
    setPendingLabel(undefined);
    if (next !== undefined) {
      next();
    }
  }, [options, pending]);

  const confirmCancel = useCallback((): void => {
    setPending(undefined);
    setPendingLabel(undefined);
  }, []);

  return {
    isDirty: options.isDirty,
    dialogOpen: pending !== undefined,
    pendingLabel,
    requestLeave,
    confirmSave,
    confirmDiscard,
    confirmCancel,
  };
}
