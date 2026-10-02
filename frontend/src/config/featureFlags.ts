import { getDeployConfig } from './env.js';
import type { DeployConfig } from './env.js';

/**
 * Product-wide feature-flag vocabulary and resolution rule (Task 048).
 *
 * WHAT THIS FILE OWNS
 * -------------------
 * Three things, and nothing else:
 *
 *   1. The KEY VOCABULARY - `FEATURE_FLAG_KEYS`. Six keys, and every one of
 *      them is a real presentation switch in this product.
 *   2. The FAIL-CLOSED DEFAULTS - `FEATURE_FLAG_DEFAULTS`. Every value `false`.
 *   3. The RESOLUTION RULE - `resolveFeatureFlag` / `evaluateFeatureFlag`. One
 *      pure function plus one thin impure wrapper.
 *
 * There is no I/O here and no React. That is deliberate: the rule that decides
 * whether an experimental surface is visible is the rule worth testing
 * exhaustively, and it can only be tested exhaustively if it is a pure
 * function of two plain inputs.
 *
 * WHERE A FLAG VALUE MAY COME FROM (R4)
 * -------------------------------------
 * Exactly two places, in this order:
 *
 *   1. `GET /me`'s `featureFlags` slice (Task 006). The only server -> client
 *      flag channel an ordinary user can read: the admin flag list
 *      (`GET /admin/feature-flags`) is tenant-admin gated and 403s for everyone
 *      else, so it cannot gate a workspace surface.
 *   2. Build-time bootstrap config - the `VITE_ENABLE_*` family that
 *      `config/env.ts` already resolves (`VITE_ENABLE_ANALYTICS`,
 *      `_DIAGNOSTICS`, `_EXPERIMENTAL_FEATURES`).
 *
 * Precedence is one sentence: **a flag is ON when `/me` states it ON, or -
 * when `/me` states nothing about it - when bootstrap config states it ON.
 * Everything else is OFF.** So where both speak, `/me` wins; where neither
 * speaks, the fail-closed default wins. There is no third source, no
 * experimentation service, no DB-backed flag table (plan section 6.9: none is
 * required), and no client-side persistence.
 *
 * WHY "STATES NOTHING" IS NOT "STATES FALSE"
 * ------------------------------------------
 * The rule distinguishes three states, not two, and the middle one is the one
 * that is easy to get wrong:
 *
 *   - `/me` carries the wire key with `true`   -> ON, and bootstrap is ignored.
 *   - `/me` carries the wire key with `false`  -> OFF. A recognised-but-off key
 *     is not an absent key: an operator who turned enrichment off in the admin
 *     panel must not see it back because the bundle happens to be built with
 *     the bootstrap switch on.
 *   - `/me` says nothing (no slice, no key, unreadable document, a value that
 *     is not a boolean) -> bootstrap decides, and the fail-closed default if
 *     bootstrap says nothing either.
 *
 * `/me` FLAGS ARE UX, NEVER AUTHORIZATION (R3, security section)
 * --------------------------------------------------------------
 * Nothing in this file accepts a permission list, a role, or a token, and that
 * omission is the enforcement: a flag cannot grant anything, so no caller can
 * accidentally read one as if it could. `GET /me`'s `permissions` are
 * themselves documented as "UX hints" by Task 006, and every controller
 * re-authorizes server-side; the browser's opinion is a rendering decision
 * only. The hook in `src/hooks/useFeatureFlag.ts` keeps that separation
 * visible by never importing the permission helpers.
 *
 * WHY THE WIRE KEYS ARE DECLARED HERE AND NOWHERE ELSE (R1)
 * --------------------------------------------------------
 * `src/hooks/useFeatureFlag.gate.test.tsx` fails the suite if any other
 * non-test module names a `/me` flag wire key, reads `VITE_ENABLE_*` directly,
 * or issues its own `/me` read. A vocabulary declared in two places is a
 * vocabulary that will disagree with itself the first time a flag is renamed.
 */

/**
 * The keys, in a fixed order. Order is load-bearing for two things: the
 * wire-key map below, and a suite that asserts the key list has not silently
 * grown a seventh entry.
 */
export const FEATURE_FLAG_KEYS = [
  'analytics',
  'diagnostics',
  'experimentalFeatures',
  'videoIntelligence',
  'lipSync',
  'localInference',
] as const;

export type FeatureFlagKey = (typeof FEATURE_FLAG_KEYS)[number];

/** A fully resolved flag map: every key present, every value a boolean. */
export type FeatureFlagValue = Readonly<Record<FeatureFlagKey, boolean>>;

/**
 * Every default is `false`.
 *
 * "Closed" is the safe direction for every flag in this product: the three
 * `/me` flags gate enrichment artifacts and an operator device summary, and the
 * three bootstrap flags gate presentation surfaces. Guessing wrong in one
 * direction shows a user a panel that cannot work; in the other it ships
 * experimental code and device detail to people who must not have them. There
 * is therefore no `true` default anywhere in this file, and a test asserts it.
 */
export const FEATURE_FLAG_DEFAULTS: FeatureFlagValue = Object.freeze({
  analytics: false,
  diagnostics: false,
  experimentalFeatures: false,
  videoIntelligence: false,
  lipSync: false,
  localInference: false,
});

/**
 * `/me` `featureFlags` wire keys, per client key.
 *
 * These are the EXACT fields of `MeFeatureFlags`
 * (`src/DubbingPlatform.Api/Models/AuthMeDtos.cs`) and nothing else. An earlier
 * draft also accepted client-side aliases (`videoIntel`, `localGpu`), and that
 * was a mistake recorded in Task 044: `featureFlags: { videoIntel: true }`
 * then read as "on" for a key no backend has ever emitted, so a payload shaped
 * by anything other than the platform could switch a surface on. Accepting a
 * name the server does not send is how a fail-closed parser stops being
 * fail-closed.
 *
 * An empty list means "bootstrap only, today": the server has no such field.
 * When it grows one, this table gains the wire key and nothing else changes -
 * `/me` then wins over bootstrap by the rule above.
 */
export const ME_FEATURE_FLAG_WIRE_KEYS: Readonly<Record<FeatureFlagKey, readonly string[]>> = Object.freeze({
  analytics: [],
  diagnostics: [],
  experimentalFeatures: [],
  videoIntelligence: ['videoIntelligenceEnabled'],
  lipSync: ['lipSyncEnabled'],
  localInference: ['localInferenceEnabled'],
});

/**
 * How each key reads its bootstrap value, out of the already-validated
 * `DeployConfig`.
 *
 * Typed as `(config: DeployConfig) => boolean`, so a rename of a config field
 * is a typecheck error here rather than a runtime `undefined` that reads as
 * "off" for a flag somebody meant to ship on. `undefined` means the key has no
 * bootstrap source at all: the three enrichment capabilities come only from
 * `/me`, and that is why the literal is here rather than inferred.
 *
 * The `VITE_ENABLE_*` names are NOT repeated in this file. They are read once,
 * in `config/env.ts`, and this module asks for the resolved boolean - which is
 * also what lets the R1 gate forbid `VITE_ENABLE_` anywhere else.
 */
const BOOTSTRAP_READERS: Readonly<
  Record<FeatureFlagKey, ((config: DeployConfig) => boolean) | undefined>
> = Object.freeze({
  analytics: (config: DeployConfig): boolean => config.analyticsEnabled,
  diagnostics: (config: DeployConfig): boolean => config.diagnosticsEnabled,
  experimentalFeatures: (config: DeployConfig): boolean => config.experimentalFeaturesEnabled,
  videoIntelligence: undefined,
  lipSync: undefined,
  localInference: undefined,
});

/**
 * The `/me` slice in wire spelling: wire key -> boolean.
 *
 * Wire-shaped rather than client-shaped on purpose: this is what the cache
 * holds, so a document read once (by `authStore`'s session resolution, or by
 * this module's own query) is the single source for every consumer, and the
 * client-shaped map is derived per consumer. An unwrapped slice is accepted by
 * {@link readMeFeatureFlagSlice} so a caller holding the slice does not have to
 * re-wrap it to test the parser.
 */
export type MeFeatureFlagSlice = Readonly<Record<string, boolean>>;

/** The one value that means "no flag was stated". Frozen and shared. */
export const EMPTY_ME_FEATURE_FLAG_SLICE: MeFeatureFlagSlice = Object.freeze({});

/** Which source decided a flag. Exported so callers can explain, not guess. */
export type FeatureFlagSourceName = 'ME' | 'BOOTSTRAP' | 'DEFAULT';

export interface ResolvedFeatureFlag {
  readonly enabled: boolean;
  readonly source: FeatureFlagSourceName;
}

/** Every wire key the platform emits, flattened and de-duplicated. */
export function meFeatureFlagWireKeys(): readonly string[] {
  const keys = new Set<string>();
  for (const wireKeys of Object.values(ME_FEATURE_FLAG_WIRE_KEYS)) {
    for (const wireKey of wireKeys) {
      keys.add(wireKey);
    }
  }
  return [...keys].sort();
}

function toRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

/**
 * Reads the flag slice out of a raw `GET /me` document. Pure. Never throws.
 *
 * Accepts either the whole document (`{ featureFlags: {...} }`) or an
 * already-unwrapped slice, because `apiFetch` hands back the body and a caller
 * holding the slice should not have to re-wrap it to exercise this parser.
 *
 * Three deliberate drops, each of which is the fail-closed outcome:
 *
 *   - a key outside {@link ME_FEATURE_FLAG_WIRE_KEYS} - a name this platform
 *     has never emitted;
 *   - a value that is not a boolean - the backend emits real booleans, so
 *     `'true'`, `1` and `{}` are shapes this parser does not understand;
 *   - a `featureFlags` field that is not an object at all.
 *
 * Dropped means *unstated*, which is not the same as *stated false*: an
 * unstated key falls through to bootstrap config per the rule at the top of
 * this file.
 */
export function readMeFeatureFlagSlice(raw: unknown): MeFeatureFlagSlice {
  const document = toRecord(raw);
  if (document === undefined) {
    return EMPTY_ME_FEATURE_FLAG_SLICE;
  }
  // Only descend when the document still carries a container, so an unwrapped
  // slice is read in place.
  const slice = toRecord(document['featureFlags']) ?? document;
  const known = meFeatureFlagWireKeys();
  const resolved: Record<string, boolean> = {};
  for (const [name, value] of Object.entries(slice)) {
    if (!known.includes(name) || typeof value !== 'boolean') {
      continue;
    }
    resolved[name] = value;
  }
  return Object.keys(resolved).length === 0 ? EMPTY_ME_FEATURE_FLAG_SLICE : Object.freeze(resolved);
}

/**
 * Resolves one flag. Pure. Total.
 *
 * `bootstrap` is a parameter rather than a closed-over constant for the same
 * reason `auditDeployConfig` takes one: it makes the whole matrix reachable
 * from a test without a `VITE_*` stub, and it makes this function incapable of
 * throwing.
 *
 * Total in the strong sense: a key outside the vocabulary resolves `false`
 * without reading anything. The type system already says such a key cannot
 * arrive, and a flag resolver is the wrong place to be the second line of
 * defence that decides it matters.
 */
export function resolveFeatureFlagDetailed(
  slice: MeFeatureFlagSlice | undefined,
  key: FeatureFlagKey,
  bootstrap: FeatureFlagValue | undefined,
): ResolvedFeatureFlag {
  if (!Object.prototype.hasOwnProperty.call(FEATURE_FLAG_DEFAULTS, key)) {
    return { enabled: false, source: 'DEFAULT' };
  }
  for (const wireKey of ME_FEATURE_FLAG_WIRE_KEYS[key]) {
    const stated = slice?.[wireKey];
    if (typeof stated === 'boolean') {
      // A recognised key is the whole answer, `false` included: `/me` wins.
      return { enabled: stated === true, source: 'ME' };
    }
  }
  if (bootstrap?.[key] === true) {
    return { enabled: true, source: 'BOOTSTRAP' };
  }
  return { enabled: FEATURE_FLAG_DEFAULTS[key], source: 'DEFAULT' };
}

/** {@link resolveFeatureFlagDetailed}, flattened. Pure. */
export function resolveFeatureFlag(
  slice: MeFeatureFlagSlice | undefined,
  key: FeatureFlagKey,
  bootstrap: FeatureFlagValue | undefined,
): boolean {
  return resolveFeatureFlagDetailed(slice, key, bootstrap).enabled;
}

/** Resolves every key. Pure, and total: the result always has all six keys. */
export function resolveFeatureFlags(
  slice: MeFeatureFlagSlice | undefined,
  bootstrap: FeatureFlagValue | undefined,
): FeatureFlagValue {
  let resolved: FeatureFlagValue = FEATURE_FLAG_DEFAULTS;
  for (const key of FEATURE_FLAG_KEYS) {
    const flag = resolveFeatureFlagDetailed(slice, key, bootstrap);
    resolved = { ...resolved, [key]: flag.enabled };
  }
  return resolved;
}

/**
 * The build-time flag values, read from the validated deploy config.
 *
 * Never throws, which is the whole point. `getDeployConfig()` throws by design
 * when an injected value is not allowlisted or is secret-shaped, and a flag
 * evaluation is not a place to surface a configuration error: the surfaces it
 * gates are experimental, so degrading them to off is the correct response and
 * crashing the shell over one mis-set `VITE_*` would not be. The config error
 * is reported by `src/lib/env.ts`'s startup guard and by
 * `scripts/vite-env-audit.sh`, where a human is looking.
 */
export function readBootstrapFeatureFlags(): FeatureFlagValue {
  let config: DeployConfig;
  try {
    config = getDeployConfig();
  } catch {
    return FEATURE_FLAG_DEFAULTS;
  }
  let resolved: FeatureFlagValue = FEATURE_FLAG_DEFAULTS;
  for (const key of FEATURE_FLAG_KEYS) {
    const read = BOOTSTRAP_READERS[key];
    if (read === undefined) {
      continue;
    }
    try {
      resolved = { ...resolved, [key]: read(config) === true };
    } catch {
      // A reader that throws is an unknown bootstrap value, which is off.
      resolved = { ...resolved, [key]: false };
    }
  }
  return resolved;
}

/** The resolved flag map for a `/me` slice, bootstrap included. */
export function evaluateFeatureFlags(slice: MeFeatureFlagSlice | undefined): FeatureFlagValue {
  return resolveFeatureFlags(slice, readBootstrapFeatureFlags());
}

/** One flag, for a `/me` slice, bootstrap included. */
export function evaluateFeatureFlag(slice: MeFeatureFlagSlice | undefined, key: FeatureFlagKey): boolean {
  return resolveFeatureFlag(slice, key, readBootstrapFeatureFlags());
}