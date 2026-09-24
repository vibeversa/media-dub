import { Suspense } from 'react';
import type { ReactNode } from 'react';
import { RouterProvider } from 'react-router-dom';
import { ToastProvider } from '../components/Toast/Toast.js';
import { ChunkErrorBoundary } from './ChunkErrorBoundary.js';
import { ConfigErrorScreen } from './ConfigErrorScreen.js';
import { RouteFallback } from './RouteFallback.js';
import { LocaleProvider } from './providers/LocaleProvider.js';
import { QueryProvider } from './providers/QueryProvider.js';
import { StoreProvider } from './providers/StoreProvider.js';
import { TelemetryProvider } from './providers/TelemetryProvider.js';
import { ThemeProvider } from './providers/ThemeProvider.js';
import { ROUTER_PROVIDER_FUTURE_FLAGS, createAppRouter } from './router.js';
import { tryGetEnv } from '../lib/env.js';

// Router singleton: one history + route tree per page load. The Suspense
// boundary covers the top-level lazy NotFound route; nested lazy pages fall
// back inside the shell.
const router = createAppRouter();

/** App shell: env guard, then query/store/theme/locale/telemetry/toast providers + router. */
export function App(): ReactNode {
  const config = tryGetEnv();
  if (!config.ok) {
    return <ConfigErrorScreen error={config.error} />;
  }
  return (
    <QueryProvider>
      <StoreProvider>
        <ThemeProvider>
          <LocaleProvider>
            <TelemetryProvider>
              <ToastProvider>
                <ChunkErrorBoundary>
                  <Suspense fallback={<RouteFallback />}>
                    <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
                  </Suspense>
                </ChunkErrorBoundary>
              </ToastProvider>
            </TelemetryProvider>
          </LocaleProvider>
        </ThemeProvider>
      </StoreProvider>
    </QueryProvider>
  );
}
