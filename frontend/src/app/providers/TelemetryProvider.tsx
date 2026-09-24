import { useCallback, useMemo } from 'react';
import type { ReactNode } from 'react';
import { TelemetryContext } from '../../telemetry/telemetryContext.js';
import { readTelemetryFlag } from '../../telemetry/telemetry.js';
import { useAppStore } from '../../stores/index.js';

interface TelemetryProviderProps {
  readonly children: ReactNode;
}

/**
 * Telemetry provider (Task 018): exposes the kill-switch state plus the
 * persisted opt-out. Event emission stays in `src/telemetry` (pure
 * functions); this provider only owns the React seam. Disabled unless
 * `VITE_TELEMETRY_ENABLED=true` and the user has not opted out.
 */
export function TelemetryProvider({ children }: TelemetryProviderProps): ReactNode {
  const optOut = useAppStore((s) => s.telemetryOptOut);
  const setTelemetryOptOut = useAppStore((s) => s.setTelemetryOptOut);
  const setOptOut = useCallback((next: boolean) => setTelemetryOptOut(next), [setTelemetryOptOut]);
  const value = useMemo(
    () => ({ enabled: readTelemetryFlag() && !optOut, optOut, setOptOut }),
    [optOut, setOptOut],
  );
  return <TelemetryContext.Provider value={value}>{children}</TelemetryContext.Provider>;
}
