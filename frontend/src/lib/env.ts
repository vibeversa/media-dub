import { z } from 'zod';

// Single typed reader for every VITE_* variable (Task 015, R3). No other
// module may touch import.meta.env directly — eslint enforces this with
// no-restricted-syntax, with this file as the only exemption.
const envSchema = z.object({
  VITE_API_BASE_URL: z.string().url('VITE_API_BASE_URL must be a valid URL.'),
  VITE_APP_VERSION: z.string().min(1).default('0.0.0-dev'),
  VITE_SSE_ENABLED: z.enum(['true', 'false']).default('true'),
  VITE_TELEMETRY_ENABLED: z.enum(['true', 'false']).default('false'),
});

export interface AppEnv {
  readonly apiBaseUrl: string;
  readonly appVersion: string;
  readonly sseEnabled: boolean;
  readonly telemetryEnabled: boolean;
}

export interface EnvError {
  readonly issues: readonly string[];
}

let cached: AppEnv | undefined;

function parse(raw: Record<string, string | undefined>): AppEnv {
  const parsed = envSchema.safeParse({
    VITE_API_BASE_URL: raw['VITE_API_BASE_URL'],
    VITE_APP_VERSION: raw['VITE_APP_VERSION'],
    VITE_SSE_ENABLED: raw['VITE_SSE_ENABLED'],
    VITE_TELEMETRY_ENABLED: raw['VITE_TELEMETRY_ENABLED'],
  });
  if (!parsed.success) {
    const issues = parsed.error.issues.map((issue) => {
      const path = issue.path.join('.');
      return path === '' ? issue.message : `${path}: ${issue.message}`;
    });
    throw new Error(`Invalid frontend configuration: ${issues.join('; ')}`);
  }
  return {
    apiBaseUrl: parsed.data.VITE_API_BASE_URL,
    appVersion: parsed.data.VITE_APP_VERSION,
    sseEnabled: parsed.data.VITE_SSE_ENABLED === 'true',
    telemetryEnabled: parsed.data.VITE_TELEMETRY_ENABLED === 'true',
  };
}

/** Reads and validates VITE_* once, then returns the cached value. Throws on invalid config. */
export function getEnv(): AppEnv {
  if (cached === undefined) {
    cached = parse(import.meta.env as unknown as Record<string, string | undefined>);
  }
  return cached;
}

/** Non-throwing variant for the startup guard; renders the config-error screen on failure. */
export function tryGetEnv(): { ok: true; env: AppEnv } | { ok: false; error: EnvError } {
  try {
    return { ok: true, env: getEnv() };
  } catch (error) {
    const message = error instanceof Error ? error.message : 'Unknown configuration error.';
    return { ok: false, error: { issues: [message] } };
  }
}

/** Test-only reset for the module cache. Never used in production code. */
export function resetEnvCache(): void {
  cached = undefined;
}
