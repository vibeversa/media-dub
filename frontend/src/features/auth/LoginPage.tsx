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
 *
 * Task 041A: the form collects a tenant id and an external subject, not an
 * email, a password and a tenant slug. The platform is passwordless -
 * `AuthService.LoginAsync` resolves the `(tenantId, externalSubject)` pair
 * against `tenant_users` and has no credential to verify - so the previous
 * three fields could never authenticate against the real API. The committed
 * OpenAPI bundle had drifted to describe an email+password model, the
 * generated client inherited it, and every login answered 400. See
 * `tasks_report_B/041A-journeys-smoke.md`.
 */
export function LoginPage(): ReactNode {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const login = useAuthStore((s) => s.login);
  const sessionStatus = useAuthStore((s) => s.status);
  const [tenantId, setTenantId] = useState('');
  const [externalSubject, setExternalSubject] = useState('');
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
      await login({ tenantId: tenantId.trim(), externalSubject: externalSubject.trim() });
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
          label={t('auth:tenantId')}
          data-testid="auth-tenant-id"
          value={tenantId}
          onChange={(event) => {
            setTenantId(event.target.value);
          }}
          autoComplete="organization"
          required
        />
        <Input
          label={t('auth:externalSubject')}
          data-testid="auth-external-subject"
          value={externalSubject}
          onChange={(event) => {
            setExternalSubject(event.target.value);
          }}
          autoComplete="username"
          required
        />
        <Button type="submit" data-testid="auth-submit" loading={pending} disabled={pending}>
          {pending ? t('auth:signingIn') : t('auth:submit')}
        </Button>
      </form>
    </section>
  );
}
