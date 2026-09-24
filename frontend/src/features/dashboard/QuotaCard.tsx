import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Alert } from '../../components/Alert/Alert.js';
import { Card } from '../../components/Card/Card.js';
import { ProgressBar } from '../../components/ProgressBar/ProgressBar.js';
import { formatDate, formatNumber } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import type { QuotaSlice, StorageSlice } from './api.js';
import { QUOTA_WARNING_RATIO, getStorageUsageRatio, isQuotaBlocked, isStorageWarning } from './api.js';

export interface QuotaCardProps {
  readonly quota: QuotaSlice | undefined;
  readonly storage: StorageSlice | undefined;
  readonly onRetry: () => void;
}

/**
 * Daily project quota plus the storage usage meter. ≥80% shows the warning
 * meter (R2); 100%/exhausted blocks costly actions with guidance plus a
 * manage link (`/settings`; Admin usage detail lands in Task 036). The parent
 * renders this card only with the usage permission.
 */
export function QuotaCard({ quota, storage, onRetry }: QuotaCardProps): ReactNode {
  const { t } = useTranslation();
  const locale = useAppStore((s) => s.locale);
  const tenantTimezone = useAppStore((s) => s.tenantTimezone);
  if (quota === undefined) {
    return (
      <Card title={t('dashboard:quota.title')}>
        <div data-testid="dashboard-quota" role="alert">
          <p>{t('dashboard:sectionError.message')}</p>
          <button type="button" data-testid="dashboard-quota-retry" onClick={onRetry}>
            {t('common:retry')}
          </button>
        </div>
      </Card>
    );
  }
  const ratio = getStorageUsageRatio(storage);
  const percentValue = formatNumber(ratio, { locale, style: 'percent' });
  const warning = isStorageWarning(storage);
  const blocked = isQuotaBlocked(storage, quota);
  const resetsText = formatDate(quota.resetsAt, { locale, timeZone: tenantTimezone });
  return (
    <Card title={t('dashboard:quota.title')}>
      <div data-testid="dashboard-quota">
        <p data-testid="dashboard-quota-remaining">
          {t('dashboard:quota.remaining', { count: quota.remaining })}
        </p>
        <p data-testid="dashboard-quota-resets">{t('dashboard:quota.resetsAt', { date: resetsText })}</p>
        {storage !== undefined && (
          <div data-testid="dashboard-quota-meter">
            <ProgressBar value={Math.min(100, Math.round(ratio * 100))} label={t('dashboard:quota.title')} />
            <p data-testid="dashboard-quota-percent">{percentValue}</p>
          </div>
        )}
        {warning && !blocked && (
          <div data-testid="dashboard-quota-warning">
            <Alert
              tone="warning"
              title={t('dashboard:quota.warning', {
                percent: percentValue,
              })}
            />
          </div>
        )}
        {blocked && (
          <div data-testid="dashboard-quota-blocked">
            <Alert tone="error" title={t('dashboard:quota.blocked', { date: resetsText })} />
          </div>
        )}
        <p data-testid="dashboard-quota-threshold" hidden>
          {QUOTA_WARNING_RATIO}
        </p>
        <Link to="/settings" data-testid="dashboard-quota-manage">
          {t('dashboard:quota.manage')}
        </Link>
      </div>
    </Card>
  );
}
