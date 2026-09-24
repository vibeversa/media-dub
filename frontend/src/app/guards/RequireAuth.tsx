import type { ReactNode } from 'react';
import { RequireAuth as CanonicalRequireAuth } from '../../features/auth/RequireAuth.js';

/** App-shell guard: delegates to the canonical Task 019 implementation. */
export function RequireAuth(): ReactNode {
  return <CanonicalRequireAuth />;
}
