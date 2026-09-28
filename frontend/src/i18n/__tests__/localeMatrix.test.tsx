// Task 039B: locale state-matrix gap closure.
//
// Covers `useLocale` (never rendered before: locale/RTL/timezone derivation,
// locale switching with `''` fallback, date/number passthrough) plus the
// uncovered `format.ts` branches (empty-locale base language, blank timezone
// preference, currency + failure fallbacks) and the i18n missing-key handler
// (telemetry-tracked fallback, never blank, never throwing).
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../i18n.js';
import i18n, { FALLBACK_LOCALE, SUPPORTED_LOCALES, missingKeyLocale } from '../i18n.js';
import { formatDate, formatNumber, getPluralCategory, isRtlLocale, resolveTimeZone } from '../format.js';
import { useLocale } from '../useLocale.js';
import { resetEnvCache } from '../../lib/env.js';
import { useAppStore } from '../../stores/index.js';
import { clearBufferedEvents, getBufferedEvents } from '../../telemetry/telemetry.js';

beforeEach(() => {
  // The missing-key handler records `missing_translation` telemetry, which is
  // gated on the build flag plus the user opt-out; enable both so the
  // handler's effect is observable (synthetic key names only).
  resetEnvCache();
  vi.stubEnv('VITE_API_BASE_URL', 'http://localhost:5000');
  vi.stubEnv('VITE_TELEMETRY_ENABLED', 'true');
  useAppStore.getState().setTelemetryOptOut(false);
  clearBufferedEvents();
});

afterEach(() => {
  cleanup();
  vi.unstubAllEnvs();
  resetEnvCache();
  clearBufferedEvents();
  useAppStore.getState().resetForTests();
});

function Probe(): React.JSX.Element {
  const api = useLocale();
  return (
    <div data-testid="locale-probe" data-rtl={String(api.isRtl)} data-tz={api.timeZone}>
      {`${api.locale}|${api.formatNumber(1234.5)}|${api.formatDate('2024-01-15T12:00:00Z')}`}
    </div>
  );
}

describe('useLocale matrix', () => {
  it('exposes the default locale with browser timezone and formatted values', () => {
    render(<Probe />);
    const probe = screen.getByTestId('locale-probe');
    expect(probe.textContent).toContain('en|');
    expect(probe.textContent).toContain('1,234.5');
    expect(probe.textContent).toContain('2024');
    expect(probe.getAttribute('data-rtl')).toBe('false');
    expect(probe.getAttribute('data-tz')).not.toBe('');
  });

  it('derives RTL and Arabic formatting from the stored locale', () => {
    useAppStore.getState().setLocale('ar');
    render(<Probe />);
    const probe = screen.getByTestId('locale-probe');
    expect(probe.textContent).toContain('ar|');
    expect(probe.getAttribute('data-rtl')).toBe('true');
  });

  it('prefers the tenant timezone over the browser zone', () => {
    useAppStore.getState().setTenantTimezone('Europe/Berlin');
    render(<Probe />);
    expect(screen.getByTestId('locale-probe').getAttribute('data-tz')).toBe('Europe/Berlin');
  });

  it('switches locales with side effects and falls back for blank tags', () => {
    function Switcher(): React.JSX.Element {
      const api = useLocale();
      return (
        <div>
          <button type="button" data-testid="to-ar" onClick={() => api.setLocale('ar')}>
            ar
          </button>
          <button type="button" data-testid="to-blank" onClick={() => api.setLocale('')}>
            blank
          </button>
          <span data-testid="current">{api.locale}</span>
        </div>
      );
    }
    render(<Switcher />);
    fireEvent.click(screen.getByTestId('to-ar'));
    expect(screen.getByTestId('current').textContent).toBe('ar');
    expect(document.documentElement.getAttribute('dir')).toBe('rtl');
    fireEvent.click(screen.getByTestId('to-blank'));
    expect(screen.getByTestId('current').textContent).toBe('en');
    expect(document.documentElement.getAttribute('dir')).toBe('ltr');
    expect(useAppStore.getState().locale).toBe('en');
  });
});

describe('format edge matrix', () => {
  it('maps empty base languages to English (never crashes on odd tags)', () => {
    expect(isRtlLocale('')).toBe(false);
    expect(isRtlLocale('-x')).toBe(false);
    expect(isRtlLocale('HE')).toBe(true);
  });

  it('treats a blank timezone preference as absent (browser zone wins)', () => {
    expect(resolveTimeZone('')).not.toBe('');
    expect(resolveTimeZone('')).toBe(resolveTimeZone(undefined));
  });

  it('formats currency and falls back to plain strings on failure', () => {
    expect(formatNumber(12.5, { locale: 'en', style: 'currency', currency: 'USD' })).toContain('12.50');
    expect(formatNumber(12.5, { locale: 'en', style: 'percent' })).toContain('1,250%');
    expect(formatNumber(12.5, { locale: '!!!' })).toBe('12.5');
    expect(formatDate('2024-01-15T12:00:00Z', { locale: '!!!' })).toContain('2024-01-15');
  });

  it('falls back to other for unformattable plural locales', () => {
    expect(getPluralCategory(1, 'en')).toBe('one');
    expect(getPluralCategory(1, '!!!')).toBe('other');
  });
});

describe('missing-key handler', () => {
  it('tracks the fallback and never renders blank (never throws)', () => {
    clearBufferedEvents();
    const text = i18n.t('zzz.definitely.missing.key');
    expect(typeof text).toBe('string');
    expect(text.length).toBeGreaterThan(0);
    // §11.6 recovery for a missing translation is the `en` fallback, never a
    // blank label: the key text itself is the non-color signal.
    const tracked = getBufferedEvents().filter((event) => event.type === 'missing_translation');
    expect(tracked.length).toBeGreaterThan(0);
    const last = tracked[tracked.length - 1];
    if (last?.type === 'missing_translation') {
      expect(last.key).toBe('zzz.definitely.missing.key');
      expect(last.locale).not.toBe('');
    }
  });

  it('emits a missing-translation event when falling back to a default value', () => {
    clearBufferedEvents();
    const text = i18n.t('zzz.another.missing.key', { defaultValue: 'Fallback text' });
    expect(text).toBe('Fallback text');
    expect(
      getBufferedEvents().some((event) => event.type === 'missing_translation'),
    ).toBe(true);
  });

  it('resolves the reported locale with an English fallback', () => {
    expect(missingKeyLocale(['ar', 'en'])).toBe('ar');
    expect(missingKeyLocale([])).toBe('en');
    expect(missingKeyLocale('ru')).toBe('en');
    expect(missingKeyLocale(undefined)).toBe('en');
    expect(FALLBACK_LOCALE).toBe('en');
    expect([...SUPPORTED_LOCALES]).toEqual(['en', 'ar', 'ru']);
  });

  it('stays silent for keys that resolve in the active bundle', () => {
    clearBufferedEvents();
    expect(i18n.t('common:retry')).toBe('Retry');
    expect(getBufferedEvents().some((event) => event.type === 'missing_translation')).toBe(false);
  });
});
