import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { formatNumber } from '../../i18n/format.js';
import type { CostEstimate, PreflightQuota } from './usePreflight.js';

export interface CostEstimateCardProps {
  readonly estimate: CostEstimate;
  readonly currency: string;
  readonly monthToDate: number;
  readonly quota: PreflightQuota;
  readonly configHash: string | undefined;
}

/**
 * Preflight figures card (Task 024). Presentational: every figure arrives as
 * props from `loadPreflight`, so this component never fetches. The cost figure
 * is always labeled as an estimate (R2) with the never-a-promise note; quota
 * shows the daily project allowance honestly (starting a run creates no
 * project, so the allowance is unchanged — only the server-side per-project
 * cost budget can still reject the start with 429).
 */
export function CostEstimateCard({ estimate, currency, monthToDate, quota, configHash }: CostEstimateCardProps): ReactNode {
  const { t } = useTranslation();
  const amount = formatNumber(estimate.amountUsd, { style: 'currency', currency });
  const spent = formatNumber(monthToDate, { style: 'currency', currency });
  return (
    <section data-testid="preflight-estimate-card" aria-label={t('processing:estimate.label')}>
      <h3 data-testid="preflight-estimate-label">{t('processing:estimate.label')}</h3>
      <p data-testid="preflight-estimate-amount">{t('processing:estimate.amount', { amount })}</p>
      <p className="dp-muted">{t('processing:estimate.note')}</p>
      <dl>
        <div>
          <dt>{t('processing:estimate.segments')}</dt>
          <dd data-testid="preflight-estimate-segments">{t('processing:estimate.segmentsValue', { count: String(estimate.segmentCount) })}</dd>
        </div>
        <div>
          <dt>{t('processing:estimate.priceTable')}</dt>
          <dd>{t('processing:estimate.priceTableValue', { version: estimate.priceVersion })}</dd>
        </div>
        <div>
          <dt>{t('processing:quota.spend')}</dt>
          <dd data-testid="preflight-spend">{spent}</dd>
        </div>
        <div>
          <dt>{t('processing:quota.remaining')}</dt>
          <dd data-testid="preflight-quota-remaining">
            {t('processing:quota.remainingValue', { count: String(quota.remaining) })}
          </dd>
        </div>
        <div>
          <dt>{t('processing:quota.remainingAfter')}</dt>
          <dd data-testid="preflight-quota-after">{t('processing:quota.remainingValue', { count: String(quota.remaining) })}</dd>
        </div>
        <div>
          <dt>{t('processing:config.hash')}</dt>
          <dd data-testid="preflight-config-hash">{configHash ?? t('processing:config.hashMissing')}</dd>
        </div>
      </dl>
      <p className="dp-muted">{t('processing:quota.impactNote')}</p>
    </section>
  );
}
