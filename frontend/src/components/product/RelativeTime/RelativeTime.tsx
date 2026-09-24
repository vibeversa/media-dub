import type { ReactNode } from 'react';

export interface RelativeTimeProps {
  readonly value: string;
  readonly locale?: string;
}

/** Locale-aware relative time; invalid input renders plain text, never crashes. */
export function RelativeTime({ value, locale }: RelativeTimeProps): ReactNode {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return <time dateTime={value}>{value}</time>;
  }
  const activeLocale = locale ?? (typeof navigator !== 'undefined' ? navigator.language : 'en');
  let text = date.toLocaleString(activeLocale);
  try {
    const fmt = new Intl.RelativeTimeFormat(activeLocale, { numeric: 'auto' });
    const diffMs = date.getTime() - Date.now();
    const absMin = Math.abs(diffMs) / 60_000;
    if (absMin < 60) {
      text = fmt.format(Math.round(diffMs / 60_000), 'minute');
    } else if (absMin < 24 * 60) {
      text = fmt.format(Math.round(diffMs / 3_600_000), 'hour');
    } else {
      text = fmt.format(Math.round(diffMs / 86_400_000), 'day');
    }
  } catch {
    // Fall back to absolute rendering above.
  }
  return <time dateTime={value}>{text}</time>;
}
