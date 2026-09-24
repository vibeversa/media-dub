import type { ReactNode } from 'react';

/** Sign-in screen. Auth wiring lands in later tasks. */
export default function LoginPage(): ReactNode {
  return (
    <section data-testid="page-login">
      <h1 className="text-xl font-semibold">Sign in</h1>
      <p className="mt-2 text-sm text-slate-600">Authentication is wired in a later task.</p>
    </section>
  );
}
