import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// Frontend scaffold (Task 015). Production source maps stay disabled so built
// bundles do not leak source context; enable explicitly for a debug build.
export default defineConfig({
  plugins: [react()],
  build: {
    sourcemap: false,
    chunkSizeWarningLimit: 600,
  },
  preview: {
    port: 4173,
  },
  server: {
    port: 5173,
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}', 'src/**/*.spec.{ts,tsx}'],
    reporters: ['default'],
    setupFiles: ['./src/test/setup.ts'],
    // Task 046 R1: MSW is opt-in per suite via `installMocks()`
    // (`src/mocks/server.ts`), NOT installed globally here. Installing it for
    // every suite would put the ~1760 tests that use `setInnerFetchForTests` and
    // never reach the network behind a request interceptor, and it would make an
    // unmocked request in a pure state-machine suite fail for a reason that has
    // nothing to do with that suite.
    // Task 039A: coverage enforcement. `vite.config.ts` is the canonical
    // coverage owner (no separate `vitest.config.ts`, to avoid dual-config
    // drift — see `docs/coverage.md`). `npm run test -- --coverage` emits
    // text + lcov + html + json-summary; `json-summary` feeds
    // `scripts/coverage-gap.mjs`, which tracks the per-file 80% target.
    // `thresholds` below are the CI gate; 039B raised them from the initial
    // 72/64/68/72 floor to 80 across the board once the state-matrix suites
    // emptied the gap report. Generated OpenAPI output is excluded by explicit
    // policy list, never by accident.
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov', 'html', 'json-summary'],
      reportsDirectory: './coverage',
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/api/generated/**',
        'src/**/*.test.{ts,tsx}',
        'src/**/*.spec.{ts,tsx}',
        // Task 039B: Storybook stories are dev-only demos, never shipped.
        // Visual regressions own them in 041B; Vitest must not count them.
        'src/**/*.stories.{ts,tsx}',
        // Task 046: the shared harness. It is loaded by every test file and has
        // no branch a suite can exercise, so counting it would add a permanent
        // uncovered line that trains everyone to ignore the threshold. It
        // replaced the single-purpose `src/testSetup.ts`; keeping both would mean
        // two files loaded in an order nobody could reason about.
        'src/test/**',
        '**/*.d.ts',
        'playwright.config.ts',
        'e2e/**',
        'dist/**',
        'storybook-static/**',
        '.storybook/**',
      ],
      thresholds: {
        // Task 039B: raised from the 72/64/68/72 floor to the 80 target once
        // the state-matrix suites closed every `coverage-gap.mjs` line
        // (measured 96.5 / 91.9 / 98.5 / 96.5). Never lower this to green a
        // red gate — see `docs/coverage.md`.
        lines: 80,
        branches: 80,
        functions: 80,
        statements: 80,
      },
    },
  },
});
