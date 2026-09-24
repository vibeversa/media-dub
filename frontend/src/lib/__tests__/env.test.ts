import { afterEach, describe, expect, it, vi } from 'vitest';
import { getEnv, resetEnvCache, tryGetEnv } from '../env.js';

afterEach(() => {
  vi.unstubAllEnvs();
  resetEnvCache();
});

describe('env guard', () => {
  it('parses valid VITE_* values', () => {
    vi.stubEnv('VITE_API_BASE_URL', 'https://api.example.com');
    vi.stubEnv('VITE_APP_VERSION', '1.2.3');
    vi.stubEnv('VITE_SSE_ENABLED', 'false');
    vi.stubEnv('VITE_TELEMETRY_ENABLED', 'true');
    resetEnvCache();
    const env = getEnv();
    expect(env.apiBaseUrl).toBe('https://api.example.com');
    expect(env.appVersion).toBe('1.2.3');
    expect(env.sseEnabled).toBe(false);
    expect(env.telemetryEnabled).toBe(true);
  });

  it('fails fast when VITE_API_BASE_URL is missing', () => {
    vi.stubEnv('VITE_API_BASE_URL', '');
    resetEnvCache();
    const result = tryGetEnv();
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.error.issues.length).toBeGreaterThan(0);
    }
  });
});
