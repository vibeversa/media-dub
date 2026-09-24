import { create } from 'zustand';

interface AppState {
  readonly ready: boolean;
  readonly markReady: () => void;
}

/** Zustand store root. Feature slices land in later tasks. */
export const useAppStore = create<AppState>()((set) => ({
  ready: false,
  markReady: () => {
    set({ ready: true });
  },
}));
