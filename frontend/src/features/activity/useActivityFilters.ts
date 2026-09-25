import { useSearchParams } from 'react-router-dom';
import type { ActivityFilters } from './types.js';

/**
 * URL-backed activity filters (Task 035). Values live in the search params
 * so timelines are shareable; reset strips the filter keys plus the page
 * cursor. Filtering itself is client-side over the fetched page.
 */
export function useActivityFiltersFromUrl(): {
  filters: ActivityFilters;
  setFilters: (next: ActivityFilters) => void;
  resetFilters: () => void;
} {
  const [searchParams, setSearchParams] = useSearchParams();
  const filters: ActivityFilters = {
    actor: (searchParams.get('actor') ?? '').slice(0, 80),
    action: (searchParams.get('action') ?? '').slice(0, 80),
    from: (searchParams.get('from') ?? '').slice(0, 20),
    to: (searchParams.get('to') ?? '').slice(0, 20),
  };
  function setFilters(next: ActivityFilters): void {
    const params = new URLSearchParams(searchParams.toString());
    if (next.actor !== '') {
      params.set('actor', next.actor);
    } else {
      params.delete('actor');
    }
    if (next.action !== '') {
      params.set('action', next.action);
    } else {
      params.delete('action');
    }
    if (next.from !== '') {
      params.set('from', next.from);
    } else {
      params.delete('from');
    }
    if (next.to !== '') {
      params.set('to', next.to);
    } else {
      params.delete('to');
    }
    params.delete('page');
    setSearchParams(params, { replace: true });
  }
  function resetFilters(): void {
    const params = new URLSearchParams(searchParams.toString());
    params.delete('actor');
    params.delete('action');
    params.delete('from');
    params.delete('to');
    params.delete('page');
    setSearchParams(params, { replace: true });
  }
  return { filters, setFilters, resetFilters };
}
