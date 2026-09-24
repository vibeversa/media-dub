import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Card } from '../../components/Card/Card.js';
import { formatNumber } from '../../i18n/format.js';
import { useAppStore } from '../../stores/index.js';
import type { CostSlice, StorageSlice } from './api.js';
import { getStorageUsageRatio } from './api.js';

export interface StorageCostCardProps {
  readonly storage: StorageSlice | undefined;
  readonly cost: CostSlice | undefined;
  readonly onRetry: () => void;
}

/**
 * Storage + month-to-date cost totals. The parent renders this card only with
 * the usage permission (hidden otherwise — never an error flash). Cost uses
 * the tenant currency/locale formatters (Task 018).
 */
export function StorageCostCard({ storage, onRetry, cost }: StorageCostCardProps): ReactNode {
  const { t } = useTranslation();
  const locale = useAppStore((s) => s.locale);
  if (storage === undefined || cost === undefined) {
    return (
      <Card title={t('dashboard:storageCost.title')}>
        <div data-testid="dashboard-storage-cost" role="alert">
          <p>{t('dashboard:sectionError.message')}</p>
          <button type="button" data-testid="dashboard-storage-retry" onClick={onRetry}>
            {t('common:retry')}
          </button>
        </div>
      </Card>
    );
  }
  const ratio = getStorageUsageRatio(storage);
  const percent = formatNumber(ratio, { locale, style: 'percent' });
  return (
    <Card title={t('dashboard:storageCost.title')}>
      <div data-testid="dashboard-storage-cost">
        <dl>
          <div>
            <dt>{t('dashboard:storageCost.storage')}</dt>
            <dd data-testid="dashboard-storage-used">
              {formatNumber(storage.usedBytes, { locale })} / {formatNumber(storage.quotaBytes, { locale })} (
              {percent})
            </dd>
          </div>
          <div>
            <dt>{t('dashboard:storageCost.cost')}</dt>
            <dd data-testid="dashboard-cost-total">
              {formatNumber(cost.monthToDate, { locale, style: 'currency', currency: cost.currency })}
            </dd>
          </div>
        </dl>
        <Link to="/settings" data-testid="dashboard-storage-manage">
          {t('dashboard:storageCost.manage')}
        </Link>
      </div>
    </Card>
  );
}
