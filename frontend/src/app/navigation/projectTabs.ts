/**
 * Project tab model (Task 018, R5). Tabs derive from workspace state — never
 * hardcoded per page. `ProjectLayout` renders from `getProjectTabs`; feature
 * tasks (020+) fill the tab outlets without touching this model.
 */
export type ProjectTabId =
  | 'overview'
  | 'media'
  | 'transcript'
  | 'translation'
  | 'voices'
  | 'timeline'
  | 'quality'
  | 'exports'
  | 'activity';

export interface ProjectTab {
  readonly id: ProjectTabId;
  readonly labelKey: string;
  /** Relative subpath under `/projects/:id`. */
  readonly to: string;
  readonly disabled: boolean;
  /** i18n key explaining the disabled state (tooltip); absent when enabled. */
  readonly disabledReasonKey?: string;
  /** Badge text (counts only, never content); absent when nothing to flag. */
  readonly badge?: string;
}

/**
 * Minimal workspace state the tab model reads. Feature tasks pass richer
 * aggregates (Task 008 workspace); only these booleans/counters gate tabs.
 */
export interface ProjectWorkspaceState {
  readonly hasMedia: boolean;
  readonly hasTranscript: boolean;
  readonly exportReadyCount: number;
  readonly openReviewCount: number;
}

export const EMPTY_WORKSPACE_STATE: ProjectWorkspaceState = {
  hasMedia: false,
  hasTranscript: false,
  exportReadyCount: 0,
  openReviewCount: 0,
};

const TAB_IDS: readonly ProjectTabId[] = [
  'overview',
  'media',
  'transcript',
  'translation',
  'voices',
  'timeline',
  'quality',
  'exports',
  'activity',
];

/**
 * Derives the nine project tabs from workspace state: `translation` stays
 * disabled until a transcript exists, `exports` until output is ready;
 * `exports` badges the ready count and `quality` the open-review count.
 */
export function getProjectTabs(state: ProjectWorkspaceState): readonly ProjectTab[] {
  return TAB_IDS.map((id): ProjectTab => {
    const base = { id, labelKey: `nav:projectTabs.${id}`, to: id === 'overview' ? '.' : `./${id}` } as const;
    switch (id) {
      case 'translation':
        return state.hasTranscript
          ? { ...base, disabled: false }
          : { ...base, disabled: true, disabledReasonKey: 'nav:projectTabs.disabledReason.needsTranscript' };
      case 'exports':
        return state.exportReadyCount > 0
          ? { ...base, disabled: false, badge: String(state.exportReadyCount) }
          : { ...base, disabled: true, disabledReasonKey: 'nav:projectTabs.disabledReason.nothingToExport' };
      case 'quality':
        return state.openReviewCount > 0
          ? { ...base, disabled: false, badge: String(state.openReviewCount) }
          : { ...base, disabled: false };
      case 'transcript':
      case 'voices':
      case 'timeline':
        return state.hasMedia
          ? { ...base, disabled: false }
          : { ...base, disabled: true, disabledReasonKey: 'nav:projectTabs.disabledReason.needsMedia' };
      default:
        return { ...base, disabled: false };
    }
  });
}
