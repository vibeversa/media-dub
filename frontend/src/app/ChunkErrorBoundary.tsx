import { Component } from 'react';
import type { ReactNode } from 'react';
import { tryGetEnv } from '../lib/env.js';

interface ChunkErrorBoundaryProps {
  readonly children: ReactNode;
}

interface ChunkErrorBoundaryState {
  readonly failed: boolean;
}

/**
 * Recovers from lazy-chunk load failures (deploy skew, offline). Offers a
 * reload stamped with VITE_APP_VERSION so support can tell which bundle the
 * user runs. Only catches render/load errors inside the route outlet.
 */
export class ChunkErrorBoundary extends Component<ChunkErrorBoundaryProps, ChunkErrorBoundaryState> {
  public constructor(props: ChunkErrorBoundaryProps) {
    super(props);
    this.state = { failed: false };
  }

  public static getDerivedStateFromError(): ChunkErrorBoundaryState {
    return { failed: true };
  }

  public componentDidCatch(): void {
    // Telemetry wiring lands in Task 018; never log payloads here.
  }

  private appVersion(): string {
    const result = tryGetEnv();
    return result.ok ? result.env.appVersion : 'unknown';
  }

  public render(): ReactNode {
    if (this.state.failed) {
      return (
        <div role="alert" data-testid="chunk-error" className="mx-auto max-w-md py-12 text-center">
          <h1 className="text-lg font-semibold">This page failed to load</h1>
          <p className="mt-2 text-sm text-slate-600">
            The app was likely updated while you had it open. Reload to fetch the latest version.
          </p>
          <p className="mt-2 text-xs text-slate-500">Version: {this.appVersion()}</p>
          <button
            type="button"
            onClick={() => window.location.reload()}
            className="mt-4 rounded border border-slate-300 px-4 py-2"
          >
            Reload
          </button>
        </div>
      );
    }
    return this.props.children;
  }
}
