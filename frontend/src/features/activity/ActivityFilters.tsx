import type { ReactNode } from 'react';
import { DEFAULT_ACTIVITY_FILTERS, isDefaultActivityFilters } from './types.js';
import type { ActivityFilters } from './types.js';
import { useTranslation } from 'react-i18next';

/**
 * Shareable filter bar for the audit timeline. Controlled via URL state from
 * the activity filters hook; every control is labeled and the reset button
 * restores `DEFAULT_ACTIVITY_FILTERS` cleanly.
 */
export interface ActivityFiltersProps {
  readonly filters: ActivityFilters;
  readonly onChange: (next: ActivityFilters) => void;
  readonly onReset: () => void;
}

export function ActivityFilters({ filters, onChange, onReset }: ActivityFiltersProps): ReactNode {
    const { t } = useTranslation();
const isDefault = isDefaultActivityFilters(filters);
  return (
    <form
      data-testid="activity-filters"
      aria-label={t('activity:activityFilters.activity-filters')}
      onSubmit={(event) => {
        event.preventDefault();
      }}
    >
      <div style={{ display: 'flex', gap: 'var(--space-3)', flexWrap: 'wrap', alignItems: 'end' }}>
        <label>
          <span>{t('activity:activityFilters.actor')}</span>
          <input
            type="text"
            data-testid="activity-filter-actor"
            value={filters.actor}
            placeholder={t('activity:activityFilters.system')}
            onChange={(event) => {
              onChange({ ...filters, actor: event.target.value.slice(0, 80) });
            }}
          />
        </label>
        <label>
          <span>{t('activity:activityFilters.action')}</span>
          <input
            type="text"
            data-testid="activity-filter-action"
            value={filters.action}
            placeholder={t('activity:activityFilters.updated')}
            onChange={(event) => {
              onChange({ ...filters, action: event.target.value.slice(0, 80) });
            }}
          />
        </label>
        <label>
          <span>{t('activity:activityFilters.from')}</span>
          <input
            type="date"
            data-testid="activity-filter-from"
            value={filters.from}
            onChange={(event) => {
              onChange({ ...filters, from: event.target.value.slice(0, 20) });
            }}
          />
        </label>
        <label>
          <span>{t('activity:activityFilters.to')}</span>
          <input
            type="date"
            data-testid="activity-filter-to"
            value={filters.to}
            onChange={(event) => {
              onChange({ ...filters, to: event.target.value.slice(0, 20) });
            }}
          />
        </label>
        <button
          type="button"
          data-testid="activity-filters-reset"
          className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
          disabled={isDefault}
          onClick={onReset}
        >
          {t('activity:activityFilters.reset-filters')}
        </button>
      </div>
      <p data-testid="activity-filters-default" hidden>
        {JSON.stringify(DEFAULT_ACTIVITY_FILTERS)}
      </p>
    </form>
  );
}
