// Task 045, R2: locale- and timezone-correct date rendering.
//
// TWO LOCALES, TWO ZONES, ALWAYS
// -----------------------------
// R2 asks for "at least two locales/timezones". This suite pins the *same
// instant* to several locales and several zones, because the failure it exists to
// catch is not "the formatter threw": it is that the formatter quietly used the
// host's zone (UTC in CI, the developer's local zone on a laptop) or the host's
// locale, and every assertion still passed because the expectation was written
// from the same host.
//
// So the assertions below are built from `Intl` itself where a value is
// arithmetic and from *explicit literals* where a value is a contract. A literal
// like `'12:00'` is only correct if the zone is pinned; that is the point.
import { describe, expect, it } from 'vitest';
import {
  FALLBACK_LOCALE,
  FALLBACK_TIME_ZONE,
  formatDate,
  formatDateOnly,
  formatTimeOnly,
  formatTimestamp,
  isSupportedLocaleTag,
  isValidTimeZone,
  resolveExplicitTimeZone,
  resolveLocale,
  resolveTimeZone,
  toDate,
  toIsoString,
} from './dates.js';

/** 2024-01-15T12:00:00Z. Noon UTC is deliberately chosen so a zone change moves the clock. */
const INSTANT = '2024-01-15T12:00:00Z';
const INSTANT_MS = Date.parse(INSTANT);

describe('date recognition', () => {
  it('accepts every shape a caller has, and rejects nothing by throwing', () => {
    expect(toDate(INSTANT)?.toISOString()).toBe('2024-01-15T12:00:00.000Z');
    expect(toDate(INSTANT_MS)?.toISOString()).toBe('2024-01-15T12:00:00.000Z');
    expect(toDate(new Date(INSTANT_MS))?.toISOString()).toBe('2024-01-15T12:00:00.000Z');
    expect(toDate('not-a-date')).toBeUndefined();
    expect(toDate('')).toBeUndefined();
    expect(toDate('   ')).toBeUndefined();
    expect(toDate(Number.NaN)).toBeUndefined();
    expect(toDate(new Date('nope'))).toBeUndefined();
  });

  it('round-trips through the ISO form, which is what an attribute needs', () => {
    expect(toIsoString(INSTANT)).toBe('2024-01-15T12:00:00.000Z');
    expect(toIsoString('nope')).toBe('');
  });
});

describe('locale resolution (preference -> en)', () => {
  it('accepts a tag Intl can format with, including a regional variant', () => {
    expect(resolveLocale('ar')).toBe('ar');
    expect(resolveLocale('en-US')).toBe('en-US');
    expect(resolveLocale('zh-Hant-TW')).toBe('zh-Hant-TW');
    expect(isSupportedLocaleTag('en-GB')).toBe(true);
  });

  it('falls back to en for blank, unknown and non-string tags', () => {
    for (const tag of [undefined, '', '   ', '!!!', 'not a locale']) {
      expect(resolveLocale(tag), String(tag)).toBe(FALLBACK_LOCALE);
      expect(isSupportedLocaleTag(tag), String(tag)).toBe(false);
    }
  });
});

describe('timezone resolution (preference -> browser -> UTC)', () => {
  it('prefers the explicit preference', () => {
    expect(resolveTimeZone('Europe/Berlin')).toBe('Europe/Berlin');
    expect(resolveTimeZone('Asia/Tokyo')).toBe('Asia/Tokyo');
    expect(isValidTimeZone('UTC')).toBe(true);
  });

  it('never returns an unusable zone, whatever it is handed', () => {
    for (const zone of ['Mars/Olympus_Mons', '', '   ', 'Not/AZone', 'GMT+25']) {
      expect(resolveTimeZone(zone), zone).not.toBe(zone === 'GMT+25' ? 'GMT+25' : '');
      expect(isValidTimeZone(zone), zone).toBe(false);
    }
    expect(resolveTimeZone(undefined)).not.toBe('');
    expect(resolveTimeZone(undefined)).toBe(resolveTimeZone('Mars/Olympus_Mons'));
  });

  it('treats a blank preference as absent so the browser zone wins', () => {
    expect(resolveTimeZone('')).toBe(resolveTimeZone(undefined));
  });

  it('separates "authoritative" from "browser-derived" for storage-grade output', () => {
    // A render may use the browser zone; an audit row or an export manifest may
    // not, or two operators in two browsers see different strings for one event.
    expect(resolveExplicitTimeZone('Europe/Berlin')).toBe('Europe/Berlin');
    expect(resolveExplicitTimeZone('Mars/Olympus_Mons')).toBe(FALLBACK_TIME_ZONE);
    expect(resolveExplicitTimeZone(undefined)).toBe(FALLBACK_TIME_ZONE);
    expect(resolveExplicitTimeZone('')).toBe(FALLBACK_TIME_ZONE);
  });
});

describe('R2: the same instant, two locales', () => {
  it('renders the date in each locale with the zone pinned', () => {
    const utc = formatDate(INSTANT, { locale: 'en-US', timeZone: 'UTC' });
    const german = formatDate(INSTANT, { locale: 'de-DE', timeZone: 'UTC' });
    // Same instant, same zone, different rendering: that IS the requirement.
    expect(utc).not.toBe(german);
    expect(utc).toContain('2024');
    expect(german).toContain('2024');
    expect(utc).toContain('12:00');
    expect(german).toContain('12:00');
    // `en-US` leads with the month name and `de-DE` with the day number. These are
    // literal contracts, written by hand rather than derived from the same `Intl`
    // call, which would prove nothing.
    expect(utc.startsWith('Jan 15, 2024')).toBe(true);
    expect(german.startsWith('15.01.2024')).toBe(true);
  });

  it('renders Arabic numerals and an RTL date without throwing', () => {
    const arabic = formatDate(INSTANT, { locale: 'ar-EG', timeZone: 'UTC' });
    expect(arabic.length).toBeGreaterThan(0);
    expect(arabic).not.toBe(formatDate(INSTANT, { locale: 'en-US', timeZone: 'UTC' }));
  });

  it('resolves an unknown locale tag to English copy rather than a raw ISO string', () => {
    // The pre-Task-045 behaviour was to let `Intl` throw and fall through to
    // `toISOString()`, which put `2024-01-15T12:00:00.000Z` in front of a user
    // whose preference row contained a typo.
    const broken = formatDate(INSTANT, { locale: '!!!', timeZone: 'UTC' });
    expect(broken).toBe(formatDate(INSTANT, { locale: 'en-US', timeZone: 'UTC' }));
    expect(broken).not.toContain('T12:00:00');
  });
});

describe('R2: the same instant, two zones', () => {
  it('moves the clock with the zone, in the same locale', () => {
    const utc = formatDate(INSTANT, { locale: 'en-GB', timeZone: 'UTC' });
    const tokyo = formatDate(INSTANT, { locale: 'en-GB', timeZone: 'Asia/Tokyo' });
    const berlin = formatDate(INSTANT, { locale: 'en-GB', timeZone: 'Europe/Berlin' });
    expect(utc).toContain('12:00');
    // Tokyo is UTC+9 all year: 21:00, no DST edge case to reason about.
    expect(tokyo).toContain('21:00');
    // Berlin is UTC+1 in January: 13:00.
    expect(berlin).toContain('13:00');
    expect(new Set([utc, tokyo, berlin]).size).toBe(3);
  });

  it('puts the zone in a timestamp, so a row says which zone it is in', () => {
    const stamp = formatTimestamp(INSTANT, { locale: 'en-GB', timeZone: 'Asia/Tokyo' });
    expect(stamp).toContain('21:00');
    // Without the zone name two editors in two zones show the same "13:00" for
    // two different moments.
    expect(stamp).toMatch(/(GMT|UTC|[A-Z]{2,5})\s*[+-]?\d{0,2}/);
    expect(formatTimestamp(INSTANT, { locale: 'en-GB', timeZone: 'UTC' })).not.toBe(stamp);
  });

  it('splits date-only and time-only without losing the zone', () => {
    expect(formatDateOnly(INSTANT, { locale: 'en-GB', timeZone: 'UTC' })).not.toContain(':');
    expect(formatDateOnly(INSTANT, { locale: 'en-GB', timeZone: 'UTC' })).toContain('2024');
    const time = formatTimeOnly(INSTANT, { locale: 'en-GB', timeZone: 'UTC' });
    expect(time).toContain('12:00');
    expect(time).not.toContain('2024');
  });

  it('honours the requested styles', () => {
    expect(formatDate(INSTANT, { locale: 'en-GB', timeZone: 'UTC', dateStyle: 'full', timeStyle: 'full' })).toContain('2024');
    expect(formatDate(INSTANT, { locale: 'en-GB', timeZone: 'UTC', dateStyle: 'full' }).length).toBeGreaterThan(
      formatDate(INSTANT, { locale: 'en-GB', timeZone: 'UTC', dateStyle: 'short' }).length,
    );
  });
});

describe('never throws out of a render', () => {
  it('renders an unreadable value as its own raw text, so it is visible', () => {
    expect(formatDate('not-a-date')).toBe('not-a-date');
    expect(formatDate('')).toBe('');
    expect(formatDateOnly('nope')).toBe('nope');
    expect(formatTimeOnly('nope')).toBe('nope');
    expect(formatTimestamp('nope')).toBe('nope');
    expect(formatDate(undefined as unknown as string)).toBe('undefined');
  });

  it('renders with every argument absent', () => {
    // No locale, no zone, no styles: the caller's zero-argument case must not be
    // the one that throws.
    expect(formatDate(INSTANT).length).toBeGreaterThan(0);
    expect(formatDateOnly(INSTANT).length).toBeGreaterThan(0);
    expect(formatTimeOnly(INSTANT).length).toBeGreaterThan(0);
    expect(formatTimestamp(INSTANT).length).toBeGreaterThan(0);
  });
});