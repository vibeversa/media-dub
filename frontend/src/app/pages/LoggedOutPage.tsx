import type { ReactNode } from 'react';
import { LoggedOutPage as AuthLoggedOutPage } from '../../features/auth/LoggedOutPage.js';

/** Post-logout confirmation route (one lazy chunk). */
export default function LoggedOutPage(): ReactNode {
  return <AuthLoggedOutPage />;
}
