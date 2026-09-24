import type { ReactNode } from 'react';
import type { EnvError } from '../lib/env.js';

/** Startup guard screen: invalid/missing VITE_* config never renders a blank page. */
export function ConfigErrorScreen({ error }: { readonly error: EnvError }): ReactNode {
  return (
    <div role="alert" data-testid="config-error" className="mx-auto max-w-md py-12 text-center">
      <h1 className="text-lg font-semibold">Configuration error</h1>
      <p className="mt-2 text-sm text-slate-600">
        The app is missing required configuration and cannot start. Contact your administrator.
      </p>
      <ul className="mt-4 text-left text-xs text-slate-500">
        {error.issues.map((issue) => (
          <li key={issue}>{issue}</li>
        ))}
      </ul>
    </div>
  );
}
