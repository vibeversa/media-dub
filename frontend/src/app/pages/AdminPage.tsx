import type { ReactNode } from 'react';

/** Elevated admin and diagnostics. Feature content lands in later tasks. */
export default function AdminPage(): ReactNode {
  return (
    <section data-testid="page-admin">
      <h1 className="text-xl font-semibold">Admin</h1>
      <p className="mt-2 text-sm text-slate-600">Usage, quotas, queues, and provider health.</p>
    </section>
  );
}
