import type { ReactNode } from 'react';

// Telemetry provider implementation lands in Task 018; this stub fixes the
// provider slot so App.tsx is stable. Telemetry stays disabled unless
// VITE_TELEMETRY_ENABLED=true (see lib/env.ts).
interface TelemetryProviderProps {
  readonly children: ReactNode;
}

export function TelemetryProvider({ children }: TelemetryProviderProps): ReactNode {
  return <>{children}</>;
}
