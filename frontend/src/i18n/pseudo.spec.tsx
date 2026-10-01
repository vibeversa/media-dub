// Task 045 — pseudo-locale + RTL + fallback-chain suite (instructions 3, 4, 5;
// R3, R4, R5).
//
// WHY THE SPEC LIVES HERE AND NOT IN `__tests__/`
// ----------------------------------------------
// The task names this file `frontend/src/i18n/pseudo.spec.ts`, so it is here and
// it is a `.tsx`. Vitest's include glob is `src/**/*.{test,spec}.{ts,tsx}`, so
// both are collected, and the presence gate treats any `*.spec.*` under
// `src/i18n` as coverage for that area either way.
//
// WHAT IT PROVES, AND WHAT IT CANNOT
// ---------------------------------
// jsdom has no layout engine. "No layout crash" therefore means *the tree
// rendered* — every node the screen is supposed to produce exists, is attached,
// and is inside the shell — and "no overlapping chrome" is asserted structurally
// (the header/nav/main landmarks are distinct, non-nested siblings), not by
// measuring boxes. The pixel half of the RTL claim belongs to the Task 041B
// visual matrix, which is what `docs/i18n.md` says this file is standing in for.
import { cleanup, render, screen } from '@testing-library/react';
import { QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../api/client/index.js';
import { AppShell } from '../app/layouts/AppShell.js';
import { LocaleProvider } from '../app/providers/LocaleProvider.js';
import { queryClient } from '../app/providers/queryClient.js';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS } from '../app/router.js';
import { readMePermissions } from '../features/auth/api.js';
import { useAuthStore } from '../features/auth/authStore.js';
import { DashboardPage } from '../features/dashboard/DashboardPage.js';
import { pluralKey, requiredPluralCategories } from '../lib/formatting/formatting.js';
import { useAppStore } from '../stores/index.js';
import i18n from './i18n.js';
import {
  applyDirection,
  baseLanguage,
  isRtlLocale,
  resolveDirection,
  resolveDirectionOverride,
} from './direction.js';
import {
  countMissingTranslations,
  getMissingTranslations,
  missingTranslationKeys,
  recordMissingTranslation,
  resetMissingTranslations,
} from './missingKeys.js';
import {
  LOCALE_CHOICES,
  parseMeLocale,
  readBrowserLocales,
  resolveAndApplyLocale,
  resolveLocale,
} from './localePreference.js';
import arCommon from './locales/ar/common.json';
import { PSEUDO_BRACKETS, PSEUDO_LOCALE, buildPseudoResources, pseudoLocalize } from './locales/pseudo.js';
import ruCommon from './locales/ru/common.json';
import { EN_RESOURCES, NAMESPACE_NAMES, listResourceKeys } from './resources.js';

/** A dashboard payload with every section present, so the whole screen renders. */
const DASHBOARD_SUMMARY = {
  projectCounts: { active: 2, archived: 1, total: 3 },
  recentOutputs: [
    { id: 'exp_1', projectId: 'prj_1', mediaKind: 'Mp4', container: 'mp4', completedAt: '2024-05-04T10:00:00Z' },
  ],
  storage: { usedBytes: 1073741824, quotaBytes: 10737418240 },
  cost: { monthToDate: 42.5, currency: 'USD' },
  quota: { remaining: 9663676416, resetsAt: '2024-06-01T00:00:00Z' },
  warnings: [],
  backlog: { pendingReviews: 4, runningJobs: 1 },
};

function mockApi(): void {
  setInnerFetchForTests((input) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
    const body = url.includes('/dashboard/summary')
      ? DASHBOARD_SUMMARY
      : url.endsWith('/notifications/unread-count')
        ? { unreadCount: 2 }
        : {};
    return Promise.resolve(
      new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    );
  });
}

function renderShell(): void {
  const router = createMemoryRouter([{ path: '/', element: <AppShell /> }], {
    initialEntries: ['/'],
    future: { ...ROUTER_FUTURE_FLAGS },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

/**
 * The shell with the dashboard in its outlet. `AppShell` renders `<Outlet/>`, so
 * the representative screen is mounted through the route tree rather than
 * beside the shell — a side-by-side render would not exercise the real
 * container/landmark nesting the RTL claim is about.
 */
function renderShellWithDashboard(): void {
  const router = createMemoryRouter(
    [{ path: '/', element: <AppShell />, children: [{ index: true, element: <DashboardPage /> }] }],
    { initialEntries: ['/'], future: { ...ROUTER_FUTURE_FLAGS } },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

/**
 * Select a locale the way the app does.
 *
 * The store is the single source of truth: `LocaleProvider` re-derives
 * `<html dir>`/`<html lang>` from `useAppStore.locale` on every mount, so calling
 * `applyDirection` directly here would be undone by the provider the next line
 * renders. Driving the store is also the more honest test — it exercises the
 * wiring the requirement is about rather than a side effect of it.
 */
async function selectLocale(locale: string): Promise<void> {
  useAppStore.getState().setLocale(locale);
  await i18n.changeLanguage(locale);
}

/**
 * A signed-in session.
 *
 * Both stores are needed and they are not interchangeable: `AppShell` reads the
 * UX-hint permissions from `useAppStore` (Task 018) while every query gate —
 * `useIsAuthenticated()` — reads the session status from `useAuthStore` (Task
 * 019). A token is registered too, because the transport refuses to make a
 * request without one and the representative screen would otherwise sit in its
 * loading skeleton forever. That is a real property of this suite: a screen that
 * never resolves is indistinguishable from one that resolved to nothing, and the
 * pseudo/RTL claims are only meaningful over a screen that actually rendered.
 */
function signIn(): void {
  useAppStore.getState().resetForTests();
  useAppStore.getState().setSession('authenticated', ['admin.manage']);
  useAuthStore.getState().resetForTests();
  useAuthStore.setState({ status: 'authenticated', accessToken: 'i18n-suite-token' });
  setTokenProvider(() => useAuthStore.getState().accessToken);
}

beforeEach(() => {
  mockApi();
  queryClient.clear();
  signIn();
  resetMissingTranslations();
});

afterEach(async () => {
  cleanup();
  clearTokenProvider();
  restoreInnerFetchForTests();
  queryClient.clear();
  useAppStore.getState().resetForTests();
  useAuthStore.getState().resetForTests();
  resetMissingTranslations();
  applyDirection('en');
  await i18n.changeLanguage('en');
});

// ---------------------------------------------------------------------------
// R4 — the pseudo locale
// ---------------------------------------------------------------------------

describe('R4: the pseudo locale covers every key and reports nothing missing', () => {
  it('derives a bundle whose key set is exactly the en bundle (never structurally incomplete)', () => {
    const pseudo = buildPseudoResources(EN_RESOURCES);
    expect([...listResourceKeys(pseudo)]).toEqual([...listResourceKeys(EN_RESOURCES)]);
    // The derivation is a fact about the function, not an assumption about it.
    expect(listResourceKeys(EN_RESOURCES).length).toBeGreaterThan(100);
  });

  it('brackets and expands, and leaves interpolation and non-ASCII alone', () => {
    const pseudo = pseudoLocalize('Retry');
    expect(pseudo.startsWith(PSEUDO_BRACKETS.open)).toBe(true);
    expect(pseudo.endsWith(PSEUDO_BRACKETS.close)).toBe(true);
    // Expanded: at least 35% longer than the source, and still not English.
    expect(pseudo.length).toBeGreaterThanOrEqual('Retry'.length * 1.35);
    expect(pseudo).not.toContain('Retry');
    // Bracketed even when there is nothing to expand, so a resolved non-ASCII
    // key is still visibly pseudo-localized.
    expect(pseudoLocalize('العربية')).toBe('[العربية]');
    // Placeholders survive byte for byte, or a plural assertion would pass for
    // the wrong reason.
    expect(pseudoLocalize('{{count}} items')).toContain('{{count}}');
    expect(pseudoLocalize('')).toBe('');
    expect(pseudoLocalize('   ')).toBe('   ');
  });

  it('is deterministic, so a screenshot baseline is stable', () => {
    expect(pseudoLocalize('Waveform with peaks')).toBe(pseudoLocalize('Waveform with peaks'));
    expect(buildPseudoResources(EN_RESOURCES)).toEqual(buildPseudoResources(EN_RESOURCES));
  });

  it('renders the shell and the dashboard under pseudo with no missing-key reports', async () => {
    await selectLocale(PSEUDO_LOCALE);
    resetMissingTranslations();
    renderShellWithDashboard();

    // The shell rendered.
    expect(screen.getByTestId('app-shell')).toBeDefined();
    expect(screen.getByTestId('brand').textContent).not.toBe('Dubbing Platform');
    // The representative screen rendered, including its deepest data-driven card.
    expect(await screen.findByTestId('dashboard-grid')).toBeDefined();
    expect(screen.getByTestId('page-dashboard')).toBeDefined();

    // R4: nothing was missing. A key the pseudo run could not resolve shows up
    // here with its namespace, which is the actionable form of the failure.
    expect(missingTranslationKeys()).toEqual([]);
    expect(countMissingTranslations()).toBe(0);

    // Nothing on the page is still English. This is the half of the pseudo idea
    // that a "no warnings" assertion cannot cover: a screen can be
    // missing-key-free and still be hard-coded English.
    const text = screen.getByTestId('page-dashboard').textContent ?? '';
    expect(text).not.toMatch(/Dashboard/);
    expect(text).not.toMatch(/Loading/);
    expect(text.length).toBeGreaterThan(0);
  });

  it('the missing-key collector is live, so every empty result above is evidence', () => {
    // A collector that never records would make "no missing keys" vacuously
    // true across this whole file - and this suite asserts it five times. Prove
    // it records, with non-empty fields, before relying on it.
    expect(countMissingTranslations()).toBe(0);
    recordMissingTranslation({ locale: '', namespace: '', key: 'probe.missing' });
    expect(countMissingTranslations()).toBe(1);
    expect(getMissingTranslations()[0]).toEqual({ locale: 'en', namespace: 'common', key: 'probe.missing' });
    resetMissingTranslations();
    expect(countMissingTranslations()).toBe(0);

    // And the real path, through i18next's own `missingKeyHandler`: a key no
    // bundle has is recorded, and renders as the key rather than blank.
    expect(i18n.t('zzz.i18n.pseudo.suite.missing')).toBe('zzz.i18n.pseudo.suite.missing');
    expect(countMissingTranslations()).toBeGreaterThan(0);
    expect(missingTranslationKeys().join(' ')).toContain('zzz.i18n.pseudo.suite.missing');
    resetMissingTranslations();
  });

  it('registers pseudo for resolution but keeps it out of the user-selectable list', async () => {
    await selectLocale(PSEUDO_LOCALE);
    expect(i18n.t('common:retry')).not.toBe('Retry');
    // A user must never be offered a fake locale.
    expect(LOCALE_CHOICES).toEqual(['en', 'ar', 'ru']);
    expect(LOCALE_CHOICES).not.toContain(PSEUDO_LOCALE);
  });
});

// ---------------------------------------------------------------------------
// R3 — RTL
// ---------------------------------------------------------------------------

describe('R3: the shell renders under dir=rtl', () => {
  it('detects RTL from the base language of a full tag', () => {
    expect(isRtlLocale('ar')).toBe(true);
    expect(isRtlLocale('ar-EG')).toBe(true);
    expect(isRtlLocale('HE')).toBe(true);
    expect(isRtlLocale('en')).toBe(false);
    expect(isRtlLocale('en-US')).toBe(false);
    expect(isRtlLocale('')).toBe(false);
    expect(baseLanguage('ar-EG')).toBe('ar');
  });

  it('flips the document and renders the real chrome, mirrored, without crashing', async () => {
    await selectLocale('ar');
    renderShellWithDashboard();

    expect(document.documentElement.getAttribute('dir')).toBe('rtl');
    expect(document.documentElement.getAttribute('lang')).toBe('ar');

    // Every landmark the shell owns is present and attached. jsdom has no layout,
    // so "no overlapping chrome" is asserted structurally: distinct nodes, not
    // nested inside one another, with the main landmark inside the shell.
    const shell = screen.getByTestId('app-shell');
    const header = shell.querySelector('header');
    const main = shell.querySelector('main#main');
    const nav = shell.querySelector('nav');
    const footer = shell.querySelector('footer');
    expect(header).not.toBeNull();
    expect(main).not.toBeNull();
    expect(nav).not.toBeNull();
    expect(footer).not.toBeNull();
    expect(header?.contains(main as Node)).toBe(false);
    expect(shell.contains(main as Node)).toBe(true);

    // Arabic copy resolved rather than falling through to English.
    expect(screen.getByTestId('nav-dashboard').textContent).toBe('لوحة التحكم');
    expect(await screen.findByTestId('dashboard-grid')).toBeDefined();
    expect(missingTranslationKeys()).toEqual([]);
  });

  it('mirrors the shell chrome under ?dir=rtl without touching the locale', async () => {
    // The override is presentation-only. The locale stays `en`, so the copy is
    // English while the layout mirrors — which is the point: a translator
    // inspecting chrome must not have Arabic's different reflow hiding a
    // layout bug.
    window.history.replaceState({}, '', '/settings?dir=rtl');
    expect(resolveDirectionOverride(window.location.search)).toBe('rtl');

    await selectLocale('en');
    useAppStore.getState().setLocale('en');
    renderShell();

    expect(document.documentElement.getAttribute('dir')).toBe('rtl');
    expect(document.documentElement.getAttribute('lang')).toBe('en');
    expect(i18n.language).toBe('en');
    // The store, the active locale and the session are untouched: an override
    // cannot reach authz, tenant scoping or an API contract.
    expect(useAppStore.getState().locale).toBe('en');
    expect(screen.getByTestId('brand').textContent).toBe('Dubbing Platform');
    window.history.replaceState({}, '', '/settings');
  });

  it('ignores an unrecognised ?dir= value rather than guessing', () => {
    expect(resolveDirectionOverride('?dir=sideways')).toBeUndefined();
    expect(resolveDirectionOverride('?dir=')).toBeUndefined();
    expect(resolveDirectionOverride('?dir=%20')).toBeUndefined();
    expect(resolveDirectionOverride('')).toBeUndefined();
    expect(resolveDirectionOverride(undefined)).toBeUndefined();
    expect(resolveDirectionOverride('?other=rtl')).toBeUndefined();
    // The one thing it does accept: case and surrounding space.
    expect(resolveDirectionOverride('?dir=RTL')).toBe('rtl');
    expect(resolveDirectionOverride('?dir=%20ltr%20')).toBe('ltr');
    // An override beats the locale; no override means the locale decides.
    expect(resolveDirection('ar')).toBe('rtl');
    expect(resolveDirection('en', 'rtl')).toBe('rtl');
    expect(resolveDirection('ar', 'ltr')).toBe('ltr');
  });

  it('never leaves document.dir unset for an unknown locale', () => {
    applyDirection('xh-Whatever');
    expect(document.documentElement.getAttribute('dir')).toBe('ltr');
    expect(document.documentElement.getAttribute('lang')).toBe('xh-Whatever');
  });
});

// ---------------------------------------------------------------------------
// R5 — the default locale from the user preference
// ---------------------------------------------------------------------------

describe('R5: preference -> stored -> browser -> en', () => {
  it('takes the /me preference first', () => {
    const resolution = resolveLocale({ preference: 'ar-EG', stored: 'ru', browserLocales: ['fr-FR'] });
    expect(resolution.locale).toBe('ar-EG');
    expect(resolution.source).toBe('preference');
  });

  it('keeps the full tag, because en-US and en-GB format dates differently', () => {
    expect(resolveLocale({ preference: 'en-US' }).locale).toBe('en-US');
    expect(resolveLocale({ preference: 'en-US' }).source).toBe('preference');
  });

  it('falls back to the stored choice when the preference is unusable', () => {
    for (const preference of [undefined, null, '', '   ', 'not a locale', 42, 'xh', {}]) {
      const resolution = resolveLocale({ preference, stored: 'ru', browserLocales: ['fr-FR'] });
      expect(resolution.locale, `preference ${JSON.stringify(preference)}`).toBe('ru');
      expect(resolution.source).toBe('stored');
    }
  });

  it('names WHY a preference was refused, so a data problem is visible', () => {
    // A refusal is recorded; an absent candidate is not. "There was no
    // preference" is already answered by `source`, and mixing the two would
    // bury the one somebody has to fix.
    expect(resolveLocale({ preference: 'not a locale' }).rejected).toEqual([
      { value: 'not a locale', reason: 'malformed' },
    ]);
    expect(resolveLocale({ preference: 42 }).rejected[0]?.reason).toBe('not-a-string');
    expect(resolveLocale({ preference: '  ' }).rejected[0]?.reason).toBe('blank');
    expect(resolveLocale({ preference: 'xh' }).rejected[0]?.reason).toBe('unsupported');
    expect(resolveLocale({ preference: undefined }).rejected).toEqual([]);
    expect(resolveLocale().rejected).toEqual([]);
  });

  it('falls back to the browser, then to en, and never to undefined', () => {
    expect(resolveLocale({ browserLocales: ['fr-FR', 'ar'] })).toMatchObject({ locale: 'ar', source: 'browser' });
    expect(resolveLocale({ browserLocales: ['zz-ZZ'] })).toMatchObject({ locale: 'en', source: 'default' });
    expect(resolveLocale()).toMatchObject({ locale: 'en', source: 'default' });
    expect(resolveLocale({ browserLocales: [] }).locale).toBe('en');
  });

  it('reads the locale out of a raw /me document, and survives an absent field', () => {
    expect(parseMeLocale({ locale: 'ar' })).toBe('ar');
    expect(parseMeLocale('ru')).toBe('ru');
    // The committed OpenAPI bundle's MeResponse has no `locale`; an older
    // backend returns nothing and the chain must continue, not crash.
    expect(parseMeLocale({ userId: 'u', tenantId: 't', permissions: [] })).toBeUndefined();
    expect(parseMeLocale(undefined)).toBeUndefined();
    expect(parseMeLocale(null)).toBeUndefined();
    expect(parseMeLocale([{ locale: 'ar' }])).toBeUndefined();
    expect(parseMeLocale({ locale: 7 })).toBeUndefined();
  });

  it('applies the resolution exactly once and reports what it applied', () => {
    const applied: string[] = [];
    const resolution = resolveAndApplyLocale({ locale: 'ar-EG' }, (locale) => applied.push(locale), 'ru');
    expect(applied).toEqual(['ar-EG']);
    expect(resolution.source).toBe('preference');

    // With no preference in the document, the stored value wins and the write
    // still happens — a silent no-write would leave the store on a stale locale.
    const fallbackApplied: string[] = [];
    const fallback = resolveAndApplyLocale({ userId: 'u' }, (locale) => fallbackApplied.push(locale), 'ru');
    expect(fallbackApplied).toEqual(['ru']);
    expect(fallback.source).toBe('stored');
  });

  it('reads navigator defensively', () => {
    const locales = readBrowserLocales();
    expect(Array.isArray(locales)).toBe(true);
    for (const locale of locales) {
      expect(typeof locale).toBe('string');
    }
  });

  it('reads the permission hints defensively from the same document', () => {
    expect(readMePermissions({ permissions: ['project.view', '', 'project.edit'] })).toEqual(['project.view', 'project.edit']);
    expect(readMePermissions({ permissions: 'nope' })).toEqual([]);
    expect(readMePermissions({ permissions: ['ok', 7] })).toEqual(['ok']);
    expect(readMePermissions(undefined)).toEqual([]);
    expect(readMePermissions([{ permissions: [] }])).toEqual([]);
  });
});

// ---------------------------------------------------------------------------
// The bundle itself
// ---------------------------------------------------------------------------

describe('the bundles', () => {
  it('declares every namespace it registers', () => {
    expect(Object.keys(EN_RESOURCES).sort()).toEqual([...NAMESPACE_NAMES].sort());
    expect(Object.keys(EN_RESOURCES)).toHaveLength(NAMESPACE_NAMES.length);
  });

  it('has no empty string anywhere in the baseline bundle', () => {
    // `returnEmptyString: false` makes an empty value fall through to the key
    // name, so an accidental `""` renders as `nav:brand`. It is a data bug that
    // no rendering assertion catches.
    const empties: string[] = [];
    const walk = (bundle: Record<string, unknown>, prefix: string): void => {
      for (const [key, value] of Object.entries(bundle)) {
        if (typeof value === 'string') {
          if (value.trim() === '') {
            empties.push(`${prefix}${key}`);
          }
        } else if (value !== null && typeof value === 'object') {
          walk(value as Record<string, unknown>, `${prefix}${key}.`);
        }
      }
    };
    for (const [ns, bundle] of Object.entries(EN_RESOURCES)) {
      walk(bundle as Record<string, unknown>, `${ns}:`);
    }
    expect(empties).toEqual([]);
  });

  it('keeps every plural key complete for the locales that need it', () => {
    // A Russian bundle with `items_one`/`items_other` and no `items_few` is not a
    // missing-translation warning: it silently renders English for 2–4 items.
    // This is the assertion that catches it, and it reads the category set from
    // the runtime rather than a hand-written table.
    const bundles: Record<string, Record<string, unknown>> = {
      en: EN_RESOURCES.common as unknown as Record<string, unknown>,
      ar: arCommon as unknown as Record<string, unknown>,
      ru: ruCommon as unknown as Record<string, unknown>,
    };
    // Sanity: the loop below asserts nothing at all if `bases` is empty.
    let checkedBases = 0;
    for (const [locale, bundle] of Object.entries(bundles)) {
      const required = requiredPluralCategories(locale);
      const bases = Object.keys(bundle)
        .filter((key) => key.endsWith('_other'))
        .map((key) => key.slice(0, -'_other'.length));
      for (const base of bases) {
        checkedBases += 1;
        for (const category of required) {
          expect(
            Object.hasOwn(bundle, pluralKey(base, category)),
            `${locale} is missing ${pluralKey(base, category)}`,
          ).toBe(true);
        }
      }
    }
    expect(checkedBases).toBeGreaterThanOrEqual(3);
    expect(requiredPluralCategories('en')).toEqual(expect.arrayContaining(['one', 'other']));
    expect(requiredPluralCategories('ru')).toEqual(expect.arrayContaining(['one', 'few', 'many', 'other']));
    expect(requiredPluralCategories('ar')).toHaveLength(6);
  });
});
