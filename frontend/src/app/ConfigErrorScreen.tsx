import type { ReactNode } from 'react';
import type { EnvError } from '../lib/env.js';

/** Startup guard screen: invalid/missing VITE_* config never renders a blank page. */
export function ConfigErrorScreen({ error }: { readonly error: EnvError }): ReactNode {
  return (
    <div role="alert" data-testid="config-error" className="mx-auto max-w-md py-12 text-center">
      <h1 className="text-lg font-semibold">Configuration error</h1>
      <p className="mt-2 text-sm dp-muted">
        The app is missing required configuration and cannot start. Contact your administrator.
      </p>
      {/* Task 045, R3: `text-start`, not `text-left`. This is the one physical
          text alignment in `frontend/src` and the start screen renders under
          whatever `dir` is active, so `text-left` would left-align the issue
          list inside an RTL document — a config error is exactly the moment
          somebody is reading carefully. */}
      <ul className="mt-4 text-start text-xs dp-muted">
        {error.issues.map((issue) => (
          <li key={issue}>{issue}</li>
        ))}
      </ul>
    </div>
  );
}
