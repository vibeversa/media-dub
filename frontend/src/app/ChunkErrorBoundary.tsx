import { Component } from 'react';
import type { ReactNode } from 'react';
import { tryGetEnv } from '../lib/env.js';
import { ChunkErrorFallback } from './ChunkErrorFallback.js';

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
      // The boundary stays a class (React requires componentDidCatch); the
      // copy lives in a functional child so it can translate (GAP-022).
      return <ChunkErrorFallback version={this.appVersion()} />;
    }
    return this.props.children;
  }
}
