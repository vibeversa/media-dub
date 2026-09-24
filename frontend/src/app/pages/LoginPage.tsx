import type { ReactNode } from 'react';
import { LoginPage as AuthLoginPage } from '../../features/auth/LoginPage.js';

/** Sign-in route: renders the Task 019 login form (one lazy chunk). */
export default function LoginPage(): ReactNode {
  return <AuthLoginPage />;
}
