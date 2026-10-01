import { Suspense, lazy } from 'react';
import type { ReactNode } from 'react';
import { useEnrichmentFlags } from './enrichmentFlags.js';
import type { EnrichmentFlagName } from './enrichmentFlags.js';

/**
 * The single enrichment gate (Task 044, R1).
 *
 * R1 IS "ZERO UI CHROME", AND THIS IS HOW THAT IS GUARANTEED
 * --------------------------------------------------------
 * When a flag is off, the requirement is not "the panel is hidden" — it is that
 * *nothing about enrichment appears anywhere*: no heading, no upsell banner, no
 * disabled button, no placeholder, no empty section, no tooltip offering a
 * future feature. Every one of those is chrome, and every one of them is the
 * thing R1 exists to forbid.
 *
 * So the gate returns `null` before anything else happens, and the panels are
 * behind a dynamic `import()` that the gate only reaches once the flag is
 * resolved ON. Three consequences, and the third is the one that matters:
 *
 * 1. Nothing renders. `null`, not a hidden container — a hidden container is
 *    still in the accessibility tree and still takes a node in the DOM.
 * 2. Nothing is fetched. The panels are code-split, so with the flag off the
 *    browser never requests the chunk. A `display: none` panel would have
 *    already downloaded itself.
 * 3. Nothing is queried. The panels' hooks are inside those chunks, so their
 *    `/enrichment/*` reads never fire either.
 *
 * THE IMPORT GATE (R6)
 * --------------------
 * `__tests__/enrichment.test.tsx` resolves the app's real chunk graph: it
 * reads the `import()` specifiers out of `src/app/pages/lazy.ts` (the single
 * lazy boundary in the app), walks each route module's STATIC closure, and
 * fails if any of them contains an enrichment panel, hook, parser or state
 * module. This file and `enrichmentFlags.ts` are the two files that ARE allowed
 * to ship in a chunk — the flag check travels, the payload does not — and that
 * exemption list is asserted to be exactly those two, so widening it is a
 * deliberate edit rather than a side effect.
 *
 * The gate is asserted in BOTH directions. It also checks that the panels are
 * reachable through a dynamic edge, because "no static import of the panels"
 * is equally true of a feature that was never mounted at all, and a check that
 * passes on absent code is not a check.
 *
 * Task 048 replaces the flag *source*, not this gate. Whatever
 * `useFeatureFlag` ends up reading, it lands here and nowhere else.
 */

// `lazy` wants a default export; both panels are named exports so the barrel
// convention (`react-refresh/only-export-components` forbids mixing) holds.
// The mapping is the load-bearing part: a STATIC import here would put both
// panels in every core bundle and R6 would fail for real.
const VideoIntelPanel = lazy(async () => ({ default: (await import('./VideoIntelPanel.js')).VideoIntelPanel }));
const LipSyncPanel = lazy(async () => ({ default: (await import('./LipSyncPanel.js')).LipSyncPanel }));

/**
 * Renders `children` only when `flag` is on. `null` otherwise — never a
 * placeholder, never a disabled affordance.
 *
 * Suspense is required because the children are lazily imported; the fallback
 * is `null` so a slow chunk never paints a spinner into a page that did not
 * ask for enrichment.
 */
export function EnrichmentGate({
  flag,
  children,
}: {
  readonly flag: EnrichmentFlagName;
  readonly children: ReactNode;
}): ReactNode {
  const flags = useEnrichmentFlags();
  if (flags[flag] !== true) {
    return null;
  }
  return <Suspense fallback={null}>{children}</Suspense>;
}

export interface ProjectEnrichmentProps {
  readonly projectId: string;
  /**
   * Core segment ids, for link resolution only. Passed down; never fetched
   * here. See `VideoIntelPanelProps.knownSegmentIds`.
   */
  readonly knownSegmentIds?: ReadonlySet<string>;
}

/**
 * Mounts every project-scoped enrichment panel the flags allow.
 *
 * Each capability is gated independently, so `videoIntel` on and `lipSync` off
 * shows scene cuts and nothing else — there is no combined "enrichment" toggle
 * that would let one flag switch another surface on.
 */
export function ProjectEnrichment({ projectId, knownSegmentIds }: ProjectEnrichmentProps): ReactNode {
  const flags = useEnrichmentFlags();
  // Not just the children: the WRAPPER. R1 asks for zero chrome, and a
  // `<div data-testid="enrichment-…">` with nothing inside it is chrome with
  // nothing in it — it is a DOM node, it is in the accessibility tree, and a
  // test that greps for `enrichment-` finds it.
  if (flags.videoIntel !== true && flags.lipSync !== true) {
    return null;
  }
  return (
    <div data-testid="enrichment-project-panels">
      <EnrichmentGate flag="videoIntel">
        <VideoIntelPanel projectId={projectId} enabled knownSegmentIds={knownSegmentIds} />
      </EnrichmentGate>
      <EnrichmentGate flag="lipSync">
        <LipSyncPanel projectId={projectId} enabled knownSegmentIds={knownSegmentIds} />
      </EnrichmentGate>
    </div>
  );
}

// The gate's decision as a pure function lives in `enrichmentFlags.ts`
// (`enrichmentGateAllows`), not here: `react-refresh/only-export-components`
// requires a component file to export components only, and a rule worth
// asserting belongs somewhere it can be asserted directly.
