import { useState } from 'react';
import type { FormEvent, ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate } from 'react-router-dom';
import { Alert } from '../../components/Alert/Alert.js';
import { Button } from '../../components/Button/Button.js';
import { Input } from '../../components/Input/Input.js';
import { useAuthStore } from './authStore.js';
import { getNextPath } from './destination.js';
import { SessionExpiredDialog } from './SessionExpiredDialog.js';

/**
 * Login page (Task 019). Reads `?next=` for the post-login destination
 * (expired deep links included, R2), submits a single request even on
 * double-click (button disabled while pending plus store-level promise
 * sharing), and shows one generic failure message for every rejection so
 * callers cannot enumerate users.
 */
export function LoginPage(): ReactNode {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const login = useAuthStore((s) => s.login);
  const sessionStatus = useAuthStore((s) => s.status);
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [tenantSlug, setTenantSlug] = useState('');
  const [pending, setPending] = useState(false);
  const [failed, setFailed] = useState(false);

  const next = getNextPath(location.search);

  async function onSubmit(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();
    if (pending) {
      return;
    }
    setPending(true);
    setFailed(false);
    try {
      await login({ email, password, tenantSlug });
      navigate(next, { replace: true });
    } catch {
      setFailed(true);
    } finally {
      setPending(false);
    }
  }

  return (
    <section data-testid="page-login">
      <h1 className="text-xl font-semibold">{t('auth:signIn')}</h1>
      {sessionStatus === 'expired' && <SessionExpiredDialog destination={next} />}
      {failed && (
        <div data-testid="auth-error" className="mt-3">
          <Alert tone="error" title={t('auth:loginError')} />
        </div>
      )}
      <form onSubmit={(event) => void onSubmit(event)} className="mt-4 flex flex-col gap-3">
        <Input
          label={t('auth:tenant')}
          data-testid="auth-tenant"
          value={tenantSlug}
          onChange={(event) => {
            setTenantSlug(event.target.value);
          }}
          autoComplete="organization"
          required
        />
        <Input
          label={t('auth:email')}
          type="email"
          data-testid="auth-email"
          value={email}
          onChange={(event) => {
            setEmail(event.target.value);
          }}
          autoComplete="username"
          required
        />
        <Input
          label={t('auth:password')}
          type="password"
          data-testid="auth-password"
          value={password}
          onChange={(event) => {
            setPassword(event.target.value);
          }}
          autoComplete="current-password"
          required
        />
        <Button type="submit" data-testid="auth-submit" loading={pending} disabled={pending}>
          {pending ? t('auth:signingIn') : t('auth:submit')}
        </Button>
      </form>
    </section>
  );
}
