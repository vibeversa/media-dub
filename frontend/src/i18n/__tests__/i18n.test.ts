import { afterEach, describe, expect, it } from 'vitest';
import i18n from '../i18n.js';
import { applyDirection, formatDate, formatNumber, getPluralCategory, isRtlLocale, resolveTimeZone } from '../format.js';

afterEach(() => {
  applyDirection('en');
  document.documentElement.setAttribute('lang', 'en');
});

describe('plural rules (ICU categories)', () => {
  it('handles English one/other', () => {
    expect(i18n.t('common:items', { count: 1, lng: 'en' })).toBe('1 item');
    expect(i18n.t('common:items', { count: 5, lng: 'en' })).toBe('5 items');
  });

  it('handles Arabic six-way plurals', () => {
    expect(i18n.t('common:items', { count: 0, lng: 'ar' })).toBe('لا عناصر');
    expect(i18n.t('common:items', { count: 1, lng: 'ar' })).toBe('عنصر واحد');
    expect(i18n.t('common:items', { count: 2, lng: 'ar' })).toBe('عنصران');
    expect(i18n.t('common:items', { count: 3, lng: 'ar' })).toContain('3');
    expect(i18n.t('common:items', { count: 11, lng: 'ar' })).toContain('11');
    expect(getPluralCategory(0, 'ar')).toBe('zero');
    expect(getPluralCategory(2, 'ar')).toBe('two');
  });

  it('handles Russian one/few/many', () => {
    expect(i18n.t('common:items', { count: 1, lng: 'ru' })).toBe('1 элемент');
    expect(i18n.t('common:items', { count: 2, lng: 'ru' })).toBe('2 элемента');
    expect(i18n.t('common:items', { count: 5, lng: 'ru' })).toBe('5 элементов');
  });
});

describe('RTL switch', () => {
  it('detects RTL locales and flips document.dir', () => {
    expect(isRtlLocale('ar')).toBe(true);
    expect(isRtlLocale('ar-EG')).toBe(true);
    expect(isRtlLocale('en')).toBe(false);
    expect(isRtlLocale('ru')).toBe(false);
    applyDirection('ar');
    expect(document.documentElement.getAttribute('dir')).toBe('rtl');
    expect(document.documentElement.getAttribute('lang')).toBe('ar');
    applyDirection('en');
    expect(document.documentElement.getAttribute('dir')).toBe('ltr');
  });
});

describe('en fallback (never blank)', () => {
  it('falls back to en for missing keys and unsupported locales', () => {
    expect(i18n.t('nav:admin', { lng: 'ar' })).toBe('Admin');
    expect(i18n.t('nav:dashboard', { lng: 'fr' })).toBe('Dashboard');
    expect(i18n.t('common:loading', { lng: 'ru' })).toBe('Loading…');
  });
});

describe('locale-aware formatting (R4)', () => {
  it('formats numbers per locale', () => {
    expect(formatNumber(1234.5, { locale: 'en' })).toBe('1,234.5');
    expect(formatNumber(3, { locale: 'ar' })).not.toBe('');
  });

  it('formats dates with the tenant timezone', () => {
    const text = formatDate('2024-01-15T12:00:00Z', { locale: 'en', timeZone: 'UTC' });
    expect(text).toContain('2024');
    expect(formatDate('not-a-date', { locale: 'en' })).toBe('not-a-date');
  });

  it('falls back to UTC for unknown timezones', () => {
    expect(resolveTimeZone('Mars/Olympus_Mons')).toBe('UTC');
    expect(resolveTimeZone('Europe/Berlin')).toBe('Europe/Berlin');
    expect(resolveTimeZone(undefined)).not.toBe('');
  });
});
