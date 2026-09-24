import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Input } from '../../components/Input/Input.js';
import { Select } from '../../components/Select/Select.js';
import { PAGE_SIZE_OPTIONS, PROJECT_STATUSES } from './api.js';
import type { ArchivedFilter, ProjectFilters, ProjectSort, SortDir } from './api.js';

export interface ProjectFiltersProps {
  readonly filters: ProjectFilters;
  readonly onChange: (patch: Partial<ProjectFilters>) => void;
  readonly onClear: () => void;
}

/**
 * List controls (Task 021). Every control writes into the URL-synced filter
 * state owned by `ProjectsPage`; this component holds no state of its own.
 * Review-state filtering is absent by design: the bundle `Project` carries
 * no review counts, so such a control would fabricate precision.
 */
export function ProjectFilters({ filters, onChange, onClear }: ProjectFiltersProps): ReactNode {
  const { t } = useTranslation();
  return (
    <form
      data-testid="projects-filters"
      aria-label={t('projects:title')}
      onSubmit={(e) => {
        e.preventDefault();
      }}
    >
      <Select
        label={t('projects:filters.status')}
        data-testid="filter-status"
        value={filters.status}
        onChange={(e) => {
          onChange({ status: e.target.value });
        }}
        options={[
          { value: '', label: t('projects:filters.statusAll') },
          ...PROJECT_STATUSES.map((status) => ({ value: status, label: status })),
        ]}
      />
      <Input
        label={t('projects:filters.targetLanguage')}
        data-testid="filter-target"
        placeholder={t('projects:filters.targetLanguagePlaceholder')}
        value={filters.targetLanguage}
        maxLength={8}
        onChange={(e) => {
          onChange({ targetLanguage: e.target.value });
        }}
      />
      <Select
        label={t('projects:filters.archived')}
        data-testid="filter-archived"
        value={filters.archived}
        onChange={(e) => {
          onChange({ archived: e.target.value as ArchivedFilter });
        }}
        options={[
          { value: 'active', label: t('projects:filters.archivedActive') },
          { value: 'archived', label: t('projects:filters.archivedOnly') },
          { value: 'all', label: t('projects:filters.archivedAll') },
        ]}
      />
      <Input
        label={t('projects:filters.owner')}
        data-testid="filter-owner"
        placeholder={t('projects:filters.ownerPlaceholder')}
        value={filters.owner}
        maxLength={128}
        onChange={(e) => {
          onChange({ owner: e.target.value });
        }}
      />
      <Input
        label={t('projects:filters.from')}
        data-testid="filter-from"
        type="date"
        value={filters.createdFrom}
        onChange={(e) => {
          onChange({ createdFrom: e.target.value });
        }}
      />
      <Input
        label={t('projects:filters.to')}
        data-testid="filter-to"
        type="date"
        value={filters.createdTo}
        onChange={(e) => {
          onChange({ createdTo: e.target.value });
        }}
      />
      <Select
        label={t('projects:filters.sort')}
        data-testid="sort-by"
        value={filters.sort}
        title={t('projects:table.progressSortNote')}
        onChange={(e) => {
          onChange({ sort: e.target.value as ProjectSort });
        }}
        options={[
          { value: 'created', label: t('projects:filters.sortCreated') },
          { value: 'activity', label: t('projects:filters.sortActivity') },
          { value: 'name', label: t('projects:filters.sortName') },
          { value: 'progress', label: t('projects:filters.sortProgress') },
        ]}
      />
      <Select
        label={t('projects:filters.sortDir')}
        data-testid="sort-dir"
        value={filters.sortDir}
        onChange={(e) => {
          onChange({ sortDir: e.target.value as SortDir });
        }}
        options={[
          { value: 'desc', label: t('projects:filters.sortDirDesc') },
          { value: 'asc', label: t('projects:filters.sortDirAsc') },
        ]}
      />
      <Select
        label={t('projects:filters.pageSize')}
        data-testid="page-size"
        value={String(filters.pageSize)}
        onChange={(e) => {
          onChange({ pageSize: Number.parseInt(e.target.value, 10) });
        }}
        options={PAGE_SIZE_OPTIONS.map((size) => ({ value: String(size), label: String(size) }))}
      />
      <button
        type="button"
        data-testid="filters-clear"
        className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring"
        onClick={onClear}
      >
        {t('projects:filters.clear')}
      </button>
    </form>
  );
}
