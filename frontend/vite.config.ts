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
    setupFiles: ['./src/testSetup.ts'],
    // Task 039A: coverage enforcement. `vite.config.ts` is the canonical
    // coverage owner (no separate `vitest.config.ts`, to avoid dual-config
    // drift — see `docs/coverage.md`). `npm run test -- --coverage` emits
    // text + lcov + html + json-summary; `json-summary` feeds
    // `scripts/coverage-gap.mjs`, which tracks the per-file 80% target that
    // 039B/039C close. `thresholds` below are the CI floor (measured baseline
    // 78.2 lines / 71.18 branches / 74.66 funcs, minus headroom so the gate is
    // stable run-to-run); the 80% target lives in the gap script + docs, not
    // here, until the gap-closure suites land. Generated OpenAPI output is
    // excluded by explicit policy list, never by accident.
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov', 'html', 'json-summary'],
      reportsDirectory: './coverage',
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/api/generated/**',
        'src/**/*.test.{ts,tsx}',
        'src/**/*.spec.{ts,tsx}',
        'src/testSetup.ts',
        '**/*.d.ts',
        'playwright.config.ts',
        'e2e/**',
        'dist/**',
        'storybook-static/**',
        '.storybook/**',
      ],
      thresholds: {
        lines: 72,
        branches: 64,
        functions: 68,
        statements: 72,
      },
    },
  },
});
