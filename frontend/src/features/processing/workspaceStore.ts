import { create } from 'zustand';

export const DEFAULT_WORKSPACE_TAB = 'overview';

export const DEFAULT_MAIN_WIDTH = 66;

export const DEFAULT_SECONDARY_WIDTH = 34;

const MIN_PANEL_WIDTH = 20;

const MAX_PANEL_WIDTH = 80;

function clampWidth(value: number): number {
  if (!Number.isFinite(value)) {
    return DEFAULT_MAIN_WIDTH;
  }
  return Math.min(MAX_PANEL_WIDTH, Math.max(MIN_PANEL_WIDTH, Math.round(value)));
}

export interface WorkspaceUiState {
  readonly selectedTab: string;
  readonly mainWidth: number;
  readonly secondaryWidth: number;
  readonly setSelectedTab: (tab: string) => void;
  readonly setPanelSizes: (main: number, secondary: number) => void;
  readonly resetForTests: () => void;
}

/**
 * UI-only workspace chrome state (Task 025).
 *
 * Holds the locally selected workspace tab plus the main/secondary column
 * widths. Aggregate data stays in TanStack (`useWorkspace`); this store never
 * fetches, caches server rows, or carries secrets.
 */
export const useWorkspaceStore = create<WorkspaceUiState>()((set) => ({
  selectedTab: DEFAULT_WORKSPACE_TAB,
  mainWidth: DEFAULT_MAIN_WIDTH,
  secondaryWidth: DEFAULT_SECONDARY_WIDTH,
  setSelectedTab: (tab) => {
    set({ selectedTab: tab });
  },
  setPanelSizes: (main, secondary) => {
    const nextMain = clampWidth(main);
    const nextSecondary = clampWidth(secondary);
    set({ mainWidth: nextMain, secondaryWidth: nextSecondary });
  },
  resetForTests: () => {
    set({
      selectedTab: DEFAULT_WORKSPACE_TAB,
      mainWidth: DEFAULT_MAIN_WIDTH,
      secondaryWidth: DEFAULT_SECONDARY_WIDTH,
    });
  },
}));
