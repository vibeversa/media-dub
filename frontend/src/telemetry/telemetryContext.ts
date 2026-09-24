import { createContext, useContext } from 'react';

export interface TelemetryContextValue {
  readonly enabled: boolean;
  readonly optOut: boolean;
  readonly setOptOut: (optOut: boolean) => void;
}

export const TelemetryContext = createContext<TelemetryContextValue>({
  enabled: false,
  optOut: false,
  setOptOut: () => {},
});

/** Telemetry switch + opt-out. Safe outside the provider (returns disabled). */
export function useTelemetry(): TelemetryContextValue {
  return useContext(TelemetryContext);
}
