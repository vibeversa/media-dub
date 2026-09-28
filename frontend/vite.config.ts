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
    // Task 039 (superseded-split note: thresholds enforced in 039A).
    // Minimal coverage wiring so `npm run test -- --coverage` emits an
    // artifact (text + lcov + html). Generated OpenAPI output is excluded
    // per the task spec; no `thresholds` here — 039A measures the gap and
    // raises `lines/branches/functions/statements` to >=80 on
    // `src/features`, `src/api`, `src/telemetry` once the gap-closure suites
    // (039B frontend matrices) land. Enforcing 80 now would fail on the
    // pre-existing uncovered surface and block this task's validation.
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov', 'html'],
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
    },
  },
});
