import type { ReactNode } from 'react';

/** Review queue. Feature content lands in later tasks. */
export default function ReviewPage(): ReactNode {
  return (
    <section data-testid="page-review">
      <h1 className="text-xl font-semibold">Review</h1>
      <p className="mt-2 text-sm text-slate-600">Items awaiting human review.</p>
    </section>
  );
}
