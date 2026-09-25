import type { ReactNode } from 'react';
import { DEFAULT_ACTIVITY_FILTERS, isDefaultActivityFilters } from './types.js';
import type { ActivityFilters } from './types.js';

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
  const isDefault = isDefaultActivityFilters(filters);
  return (
    <form
      data-testid="activity-filters"
      aria-label="Activity filters"
      onSubmit={(event) => {
        event.preventDefault();
      }}
    >
      <div style={{ display: 'flex', gap: 'var(--space-3)', flexWrap: 'wrap', alignItems: 'end' }}>
        <label>
          <span>Actor</span>
          <input
            type="text"
            data-testid="activity-filter-actor"
            value={filters.actor}
            placeholder="System"
            onChange={(event) => {
              onChange({ ...filters, actor: event.target.value.slice(0, 80) });
            }}
          />
        </label>
        <label>
          <span>Action</span>
          <input
            type="text"
            data-testid="activity-filter-action"
            value={filters.action}
            placeholder="Updated"
            onChange={(event) => {
              onChange({ ...filters, action: event.target.value.slice(0, 80) });
            }}
          />
        </label>
        <label>
          <span>From</span>
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
          <span>To</span>
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
          Reset filters
        </button>
      </div>
      <p data-testid="activity-filters-default" hidden>
        {JSON.stringify(DEFAULT_ACTIVITY_FILTERS)}
      </p>
    </form>
  );
}
