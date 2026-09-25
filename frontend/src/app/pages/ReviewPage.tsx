import type { ReactNode } from 'react';
import { ReviewStudio } from '../../features/review/ReviewStudio.js';

/** Manual review studio (Task 031): queue + single-screen context at `/review`. */
export default function ReviewPage(): ReactNode {
  return (
    <section data-testid="page-review">
      <h1 className="text-xl font-semibold">Review</h1>
      <p className="mt-2 text-sm text-slate-600">Items awaiting human review.</p>
      <div className="mt-4">
        <ReviewStudio />
      </div>
    </section>
  );
}
