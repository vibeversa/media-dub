// Task 045, R2: locale-aware numbers, currency, sizes and pluralization.
//
// THE SAME ARGUMENT AS `dates.spec.ts`
// -----------------------------------
// Every assertion here is written against a locale's *contract* (German puts a
// dot where English puts a comma; Arabic-Indic digits are not ASCII digits), not
// against whatever `Intl` happened to return on the host. An expectation built
// from the same `Intl` call as the code under test is a tautology, and a
// tautology in a locale test is worse than no test because it looks like
// coverage.
import { describe, expect, it } from 'vitest';
import {
  formatBytes,
  formatCompactNumber,
  formatCurrency,
  formatNumber,
  formatPercent,
  formatSignedPercent,
  getPluralCategory,
  isCurrencyCode,
  pluralKey,
  pluralKeyCandidates,
  pluralSuffix,
  requiredPluralCategories,
} from './formatting.js';

describe('numbers are locale-aware (R2)', () => {
  it('separates thousands the way each locale does', () => {
    expect(formatNumber(1234.5, { locale: 'en-US' })).toBe('1,234.5');
    expect(formatNumber(1234.5, { locale: 'de-DE' })).toBe('1.234,5');
    // French uses U+202F (narrow no-break space) as the group separator; the
    // class spells it out rather than matching `\s`, which also matches a plain
    // space and would pass on the wrong separator.
    expect(formatNumber(1234.5, { locale: 'fr-FR' })).toBe('1\u202f234,5');
    // Arabic locales use Arabic-Indic digits by default. A UI that hard-codes
    // ASCII digits is wrong there, and this is the assertion that says so.
    expect(formatNumber(5, { locale: 'ar-EG' })).not.toBe('5');
  });

  it('honours compact notation and fraction bounds', () => {
    expect(formatCompactNumber(12_300, 'en-US')).toMatch(/12(\.3)?K/);
    expect(formatNumber(3, { locale: 'en-US', maximumFractionDigits: 0 })).toBe('3');
    // `minimumFractionDigits` alone floors the *existing* precision, so it does
    // not round. Rounding is `maximumFractionDigits`' job; asserting both
    // together is the only way the contract is pinned.
    expect(formatNumber(3.14159, { locale: 'en-US', maximumFractionDigits: 2 })).toBe('3.14');
    expect(formatNumber(3.1, { locale: 'en-US', minimumFractionDigits: 2 })).toBe('3.10');
  });

  it('formats currency with a validated code', () => {
    expect(formatCurrency(12.5, 'en-US', 'USD')).toContain('12.50');
    expect(formatCurrency(12.5, 'de-DE', 'EUR')).toMatch(/12,50/);
    // An unknown currency is not a reason to render nothing: the format
    // downgrades rather than throwing out of a render.
    expect(formatCurrency(12.5, 'en-US', 'NOTACODE')).toBe(formatCurrency(12.5, 'en-US', 'USD'));
    expect(isCurrencyCode('USD')).toBe(true);
    expect(isCurrencyCode('usd')).toBe(true);
    expect(isCurrencyCode('US')).toBe(false);
    expect(isCurrencyCode(undefined)).toBe(false);
  });

  it('formats a ratio as a percent, so no caller has to remember the multiply', () => {
    expect(formatPercent(0.155, 'en-US')).toBe('15.5%');
    expect(formatPercent(1, 'en-US')).toBe('100%');
    expect(formatNumber(12.5, { locale: 'en-US', style: 'percent' })).toContain('1,250%');
    expect(formatSignedPercent(0.12, 'en-US')).toBe('+12%');
    expect(formatSignedPercent(-0.12, 'en-US')).toBe('-12%');
    expect(formatSignedPercent(0, 'en-US')).toBe('0%');
  });

  it('never throws out of a render', () => {
    expect(formatNumber(12.5, { locale: '!!!' })).toBe('12.5');
    expect(formatNumber(Number.NaN)).toBe('NaN');
    expect(formatNumber(Number.POSITIVE_INFINITY)).not.toBe('');
    expect(formatPercent(1, '!!!')).not.toBe('');
    expect(formatBytes(Number.NaN)).toBe('NaN');
    // No locale at all: the caller's zero-argument case must not be the one
    // that throws.
    expect(formatNumber(1)).toBe('1');
    expect(formatCurrency(1)).toContain('1');
    expect(formatPercent(1)).toBe('100%');
    expect(formatBytes(1024)).toBe('1.0 KB');
  });
});

describe('byte counts', () => {
  it('uses binary units, because every size here is a file-system byte count', () => {
    // A storage screen that says 1 MB for 1 048 576 bytes disagrees with every
    // other storage screen in the product.
    expect(formatBytes(1024)).toBe('1.0 KB');
    expect(formatBytes(1024 * 1024)).toBe('1.0 MB');
    expect(formatBytes(1024 ** 3)).toBe('1.0 GB');
    expect(formatBytes(1024 ** 4)).toBe('1.0 TB');
    expect(formatBytes(1024 ** 5)).toBe('1.0 PB');
    expect(formatBytes(512)).toBe('512 B');
    // Clamped at the largest unit rather than inventing a PB/EB suffix.
    expect(formatBytes(1024 ** 6)).toContain('PB');
  });

  it('offers decimal units when the caller has them (network transfer sizes)', () => {
    expect(formatBytes(1000, 'en-US', false)).toBe('1.0 KB');
    expect(formatBytes(1024, 'en-US', false)).toBe('1.0 KB');
  });

  it('keeps the sign and localises the number', () => {
    expect(formatBytes(-2048)).toBe('-2.0 KB');
    expect(formatBytes(1536, 'de-DE')).toBe('1,5 KB');
  });
});

describe('pluralization per locale', () => {
  it('selects the ICU category each locale actually uses', () => {
    expect(getPluralCategory(1, 'en')).toBe('one');
    expect(getPluralCategory(5, 'en')).toBe('other');
    // Arabic is the six-category case: 0, 1, 2, 3-10, 11-99, 100+.
    expect(getPluralCategory(0, 'ar')).toBe('zero');
    expect(getPluralCategory(1, 'ar')).toBe('one');
    expect(getPluralCategory(2, 'ar')).toBe('two');
    expect(getPluralCategory(3, 'ar')).toBe('few');
    expect(getPluralCategory(11, 'ar')).toBe('many');
    expect(getPluralCategory(100, 'ar')).toBe('other');
    // Russian is the one/few/many case that English's one/other gets wrong.
    expect(getPluralCategory(1, 'ru')).toBe('one');
    expect(getPluralCategory(2, 'ru')).toBe('few');
    expect(getPluralCategory(5, 'ru')).toBe('many');
  });

  it('reports every category a locale needs, from the runtime', () => {
    expect(requiredPluralCategories('en')).toEqual(['one', 'other']);
    expect(requiredPluralCategories('ru')).toEqual(expect.arrayContaining(['one', 'few', 'many', 'other']));
    expect(requiredPluralCategories('ar')).toHaveLength(6);
    // Never a throw: an unknown locale falls back rather than losing the key.
    expect(requiredPluralCategories('!!!')).toEqual(['one', 'other']);
    expect(getPluralCategory(1, '!!!')).toBe('other');
    expect(getPluralCategory(1)).toBe('one');
  });

  it('builds the i18next suffix keys', () => {
    expect(pluralSuffix('few')).toBe('_few');
    expect(pluralKey('items', 'other')).toBe('items_other');
    const candidates = pluralKeyCandidates('items', 5, 'ru');
    expect(candidates).toContain('items_many');
    // The selected category is last, so a "first present key wins" scan
    // resolves the same category i18next would.
    expect(candidates.at(-1)).toBe('items_many');
    expect(candidates.at(-1)).toBe(`items_${getPluralCategory(5, 'ru')}`);
    expect(pluralKeyCandidates('items', 1, 'en').at(-1)).toBe('items_one');
  });
});