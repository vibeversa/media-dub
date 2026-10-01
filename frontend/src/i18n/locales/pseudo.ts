import type { NamespaceResources, TranslationBundle } from '../resources.js';

/**
 * Deterministic pseudo-localization (Task 045, R4 / instruction 4).
 *
 * WHAT THIS IS FOR
 * ----------------
 * A pseudo locale is `en` with every string made obviously fake: bracketed and
 * expanded by ~35%. It is the cheapest way to find the three classes of layout
 * bug that a real translation review finds late and a screenshot review misses:
 *
 *   1. **Hard-coded copy.** Anything still rendering English under `pseudo` did
 *      not come from the bundle, because the bundle has no English left.
 *   2. **Non-expanding labels.** A button whose label grows by 35% and whose
 *      sibling does not breaks the row; a fixed-width chip overflows.
 *   3. **Missing keys.** i18next renders the key name; under `pseudo` that is
 *      `[nav:dashboard]`, which is unmistakable in a screenshot.
 *
 * WHY IT IS DERIVED, NOT COMMITTED
 * -------------------------------
 * There is no `locales/pseudo.json` on disk, and that is the point.
 *
 * A committed pseudo bundle is a second copy of every English string in the
 * product. It goes stale the moment anyone adds a key; a stale pseudo bundle
 * produces exactly the "missing key" failure it exists to detect; and the
 * resulting red test is indistinguishable from a real missing key — so the
 * reflex is to regenerate it, which is how a second copy of the truth became
 * the first thing to break. Deriving the bundle from `EN_RESOURCES` at module
 * load makes it **structurally incapable** of missing a key: if `en` has it,
 * `pseudo` has it. `pseudo.spec.tsx` still asserts full key coverage, so the
 * derivation is proven rather than assumed.
 *
 * The rules below are pure and side-effect free, so the same function is usable
 * from a test, from a Node script, or from a future CLI that dumps the bundle.
 *
 * PSEUDO-LOCALIZATION RULES
 * -------------------------
 *   - ASCII letters map to an accented look-alike, so `Retry` becomes
 *     `Rèţŕÿ` — different enough to spot, close enough to read.
 *   - Non-ASCII text is left alone: an `en` bundle that legitimately contains
 *     `العربية` must still show that the key resolved, and re-writing it would
 *     destroy the only signal the reviewer has.
 *   - The result is padded to +35% with accented letters taken from the string
 *     itself, then wrapped in `[ ]`. Both halves are required: the brackets make
 *     a pseudo string unmissable in a screenshot, and the padding is what
 *     actually finds the layout bug.
 *   - `{{interpolation}}` placeholders are lifted out before transformation and
 *     put back afterwards, byte for byte. A pseudo bundle that mangled
 *     `{{count}}` would make a plural test pass for the wrong reason.
 *   - Keys are never transformed, only values — so `_one`/`_few`/`_other`
 *     plural suffixes keep selecting the same category in every locale.
 */

/** The tag the pseudo bundle registers under. Not user-selectable. */
export const PSEUDO_LOCALE = 'pseudo';

/** Characters that bracket a pseudo-localized string. */
export const PSEUDO_BRACKETS = Object.freeze({ open: '[', close: ']' } as const);

/** Fraction of the original letter count added as padding. */
export const PSEUDO_EXPANSION_RATIO = 0.35;

/** ASCII letter -> accented look-alike. */
const ACCENTED: Readonly<Record<string, string>> = Object.freeze({
  a: 'à',
  b: 'ƀ',
  c: 'ç',
  d: 'ð',
  e: 'è',
  f: 'ƒ',
  g: 'ĝ',
  h: 'ĥ',
  i: 'î',
  j: 'ĵ',
  k: 'ķ',
  l: 'ļ',
  m: 'ɱ',
  n: 'ñ',
  o: 'ô',
  p: 'þ',
  q: 'ǫ',
  r: 'ŕ',
  s: 'š',
  t: 'ţ',
  u: 'û',
  v: 'ṽ',
  w: 'ŵ',
  x: 'ẋ',
  y: 'ý',
  z: 'ž',
});

/** `{{name}}` placeholders, captured whole. */
const PLACEHOLDER_RE = /\{\{[^}]*\}\}/g;

/**
 * Sentinel for a lifted placeholder: U+0000, the index, U+0000.
 *
 * U+0000 cannot occur in a JSON translation value, so a sentinel can never
 * collide with real content, and the round-trip through
 * `value → replace → pseudoLocalize → restore` is lossless.
 */
function sentinel(index: number): string {
  return `\u0000${index}\u0000`;
}

/**
 * Pseudo-localizes one string. Pure.
 *
 * Whitespace-only and empty strings come back untouched: bracketing `''` would
 * turn "this key resolves to nothing" into a visible marker that hides the real
 * problem, and an empty string is never layout.
 */
export function pseudoLocalize(value: string): string {
  if (value.trim() === '') {
    return value;
  }

  const placeholders: string[] = [];
  const withoutPlaceholders = value.replace(PLACEHOLDER_RE, (match) => {
    placeholders.push(match);
    return sentinel(placeholders.length - 1);
  });

  let letters = 0;
  let mapped = '';
  const accents: string[] = [];
  for (const char of withoutPlaceholders) {
    const lower = char.toLowerCase();
    const accented = ACCENTED[lower];
    if (accented === undefined) {
      mapped += char;
      continue;
    }
    letters += 1;
    const replacement = char === lower ? accented : accented.toUpperCase();
    accents.push(replacement);
    mapped += replacement;
  }

  if (letters === 0) {
    // Nothing to expand (a value that is already non-ASCII). Bracket it so the
    // key is still visibly pseudo-localized rather than indistinguishable from
    // untranslated English.
    return `${PSEUDO_BRACKETS.open}${mapped}${PSEUDO_BRACKETS.close}`;
  }

  const paddingLength = Math.max(1, Math.ceil(letters * PSEUDO_EXPANSION_RATIO));
  let padding = '';
  for (let index = 0; index < paddingLength; index += 1) {
    padding += accents[index % accents.length] ?? ACCENTED.h;
  }

  const body = `${mapped}${padding}`;
  const restored = placeholders.reduce((text, placeholder, index) => text.split(sentinel(index)).join(placeholder), body);
  return `${PSEUDO_BRACKETS.open}${restored}${PSEUDO_BRACKETS.close}`;
}

function pseudoBundle(bundle: TranslationBundle): TranslationBundle {
  const out: Record<string, unknown> = {};
  for (const key of Object.keys(bundle)) {
    const value = bundle[key];
    if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
      out[key] = pseudoBundle(value as TranslationBundle);
    } else if (typeof value === 'string') {
      out[key] = pseudoLocalize(value);
    } else {
      // Non-string leaf (number, boolean, null): passed through unchanged. A
      // bundle value of the wrong type is a data bug, not something
      // pseudo-localization should invent a rendering for.
      out[key] = value;
    }
  }
  return out;
}

/**
 * Builds the pseudo bundle for every namespace of `resources` (normally
 * `EN_RESOURCES`). Pure: the input is not mutated, and two calls with equal
 * inputs produce deeply equal output.
 */
export function buildPseudoResources(resources: NamespaceResources): NamespaceResources {
  const out: Record<string, TranslationBundle> = {};
  for (const namespace of Object.keys(resources)) {
    out[namespace] = pseudoBundle(resources[namespace] as TranslationBundle);
  }
  return Object.freeze(out);
}