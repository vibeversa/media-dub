const DISMISS_KEY = 'dubbing.quotaBanner.dismissed';

/** Reads the per-session dismissal flag. */
export function isQuotaBannerDismissed(): boolean {
  try {
    return window.sessionStorage.getItem(DISMISS_KEY) === '1';
  } catch {
    return false;
  }
}

/** Persists the per-session dismissal flag. */
export function markQuotaBannerDismissed(): void {
  try {
    window.sessionStorage.setItem(DISMISS_KEY, '1');
  } catch {
    // Storage unavailable: in-memory dismissal only.
  }
}

/** Test-only reset for the per-session dismissal. */
export function resetQuotaBannerDismissalForTests(): void {
  try {
    window.sessionStorage.removeItem(DISMISS_KEY);
  } catch {
    // Ignore.
  }
}
