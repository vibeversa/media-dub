// Shared hooks. `useFeatureFlag` is the product-wide feature-flag evaluator
// (Task 048, R1): the only sanctioned way for a surface to ask whether a flag is
// on. Re-exported here so the barrel test in `src/app/__tests__/barrels.test.ts`
// covers it, and so `R1`'s gate has a second place to check.
export {
  FeatureFlagProvider,
  FEATURE_FLAG_STALE_TIME_MS,
  useFeatureFlag,
  useFeatureFlags,
  useFeatureFlagsQuery,
  useFeatureFlagSnapshot,
} from './useFeatureFlag.js';
export type {
  FeatureFlagProviderProps,
  FeatureFlagSnapshot,
  FeatureFlagSnapshotSource,
} from './useFeatureFlag.js';