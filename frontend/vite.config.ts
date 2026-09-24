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
    include: ['src/**/*.test.{ts,tsx}'],
    reporters: ['default'],
  },
});
