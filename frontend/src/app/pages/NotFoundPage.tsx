import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';

/** Unknown route: link home, never a redirect loop. */
export default function NotFoundPage(): ReactNode {
  return (
    <section data-testid="page-not-found" className="py-12 text-center">
      <h1 className="text-xl font-semibold">Page not found</h1>
      <p className="mt-2 text-sm dp-muted">The page you asked for does not exist.</p>
      <Link
        to="/"
        className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring mt-4 inline-block"
      >
        Go home
      </Link>
    </section>
  );
}
