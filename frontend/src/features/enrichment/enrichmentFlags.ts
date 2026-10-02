import type { FeatureFlagKey } from '../../config/featureFlags.js';

/**
 * Enrichment flag vocabulary (Task 044 vocabulary, Task 048 evaluation).
 *
 * WHY THIS FILE SURVIVED A REFACTOR
 * ---------------------------------
 * Task 048 moved flag *evaluation* out of this file and into the product-wide
 * hook (`src/hooks/useFeatureFlag.ts`), which is where the resolution rule, the
 * sources and the `/me` read now live. What stayed here is the part that is
 * genuinely Task 044's: the capability names this feature area uses, and the
 * mapping from those names to the shared flag keys.
 *
 * Keeping it is not nostalgia. It is what lets `EnrichmentGate`'s public prop
 * stay `flag="lipSync"` - the vocabulary every enrichment call site and every
 * Task 044 assertion already speaks - while the decision itself comes from the
 * one shared hook. A second vocabulary declared per feature area is only a
 * problem when it can disagree with the first; this one is a total, typed map
 * onto the shared keys, so it cannot.
 *
 * WHY THE MAPPING IS NOT THE INVERSE OF THE WIRE NAMES
 * ---------------------------------------------------
 * `/me` speaks `videoIntelligenceEnabled` / `lipSyncEnabled` /
 * `localInferenceEnabled` (`MeFeatureFlags`, Task 006). This module speaks the
 * capability: `videoIntel`, `lipSync`, `localGpu`. Three names, one of which
 * (`localGpu`) is not what the server calls the capability it exposes, and one
 * of which (`lipSync`) is identical on both sides. Renaming either side would
 * otherwise mean finding every use of the other, and the wire names are declared
 * in exactly one place - `config/featureFlags.ts`, enforced by the R1 gate in
 * `src/hooks/useFeatureFlag.gate.test.tsx`.
 */

/** The three enrichment capabilities, in the feature area's own vocabulary. */
export const ENRICHMENT_FLAG_NAMES = ['videoIntel', 'lipSync', 'localGpu'] as const;

export type EnrichmentFlagName = (typeof ENRICHMENT_FLAG_NAMES)[number];

/**
 * Every enrichment capability mapped to its shared feature-flag key. Total and
 * typed, so adding a fourth capability without a key is a typecheck error here
 * rather than an undefined lookup at runtime.
 */
export const ENRICHMENT_FLAG_KEY_BY_NAME: Readonly<Record<EnrichmentFlagName, FeatureFlagKey>> = Object.freeze({
  videoIntel: 'videoIntelligence',
  lipSync: 'lipSync',
  localGpu: 'localInference',
});