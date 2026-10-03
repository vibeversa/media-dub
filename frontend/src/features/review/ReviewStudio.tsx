import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { useSearchParams } from 'react-router-dom';
import { ReviewCard } from './ReviewCard.js';
import { ReviewQueue } from './ReviewQueue.js';
import { EMPTY_REVIEW_FILTERS, reviewFiltersFromSearchParams, reviewFiltersToSearchParams } from './types.js';
import type { ReviewFilters } from './types.js';
import { useTranslation } from 'react-i18next';

export interface ReviewStudioProps {
  /** Fixed project scope (project tab). When absent, the `project` URL param owns the scope. */
  readonly projectId?: string;
}

/**
 * Manual review studio (Task 031).
 *
 * Queue + single-screen card. Filter state lives in URL search params
 * (shareable links); the queue fetches server-side per filter set and the
 * card fetches the eleven-section context for the selection. Selecting a row
 * opens its card in place — no navigation away.
 */
export function ReviewStudio({ projectId: fixedProjectId }: ReviewStudioProps): ReactNode {
    const { t } = useTranslation();
const [searchParams, setSearchParams] = useSearchParams();
  const urlFilters = useMemo(() => reviewFiltersFromSearchParams(searchParams), [searchParams]);
  const [selectedId, setSelectedId] = useState<string | undefined>(undefined);
  // Local draft for the project field. See the note at the field: writing
  // through on every keystroke dropped keyboard focus after one character.
  const [projectDraft, setProjectDraft] = useState<string>(urlFilters.project);

  const projectId = fixedProjectId ?? urlFilters.project;
  const filters: ReviewFilters = useMemo(
    () => ({ ...urlFilters, project: fixedProjectId ?? urlFilters.project }),
    [urlFilters, fixedProjectId],
  );

  useEffect(() => {
    setSelectedId(undefined);
  }, [projectId]);

  // Keep the draft in step with the URL when the URL changes from somewhere
  // else (a shared link, the back button, `Clear filters`).
  useEffect(() => {
    setProjectDraft(urlFilters.project);
  }, [urlFilters.project]);

  function writeFilters(next: ReviewFilters): void {
    const params = reviewFiltersToSearchParams(fixedProjectId === undefined ? next : { ...next, project: urlFilters.project });
    setSearchParams(params, { preventScrollReset: true });
  }

  function writeProject(nextProject: string): void {
    const trimmed = nextProject.trim();
    if (trimmed === projectId) {
      return;
    }
    const params = reviewFiltersToSearchParams({ ...filters, project: trimmed });
    setSearchParams(params, { preventScrollReset: true });
    setSelectedId(undefined);
  }

  return (
    <section data-testid="review-studio" aria-label={t('review:reviewStudio.manual-review-studio')} data-project={projectId}>
      {/*
        Task 041C: the project filter used to write through to the URL on every
        keystroke. That made it unusable from the keyboard: each character
        replaced the search params, React Router re-rendered, and focus was lost
        after the first character - so a keyboard user could type `p` and nothing
        else. The audit's keyboard walk hit this and could not scope the queue
        at all.

        It now behaves like every other filter on this screen: a local draft,
        committed on blur or Enter. The URL is still the source of truth once
        committed, so the shareable-link behaviour is unchanged - it just stops
        changing on every character.
      */}
      {fixedProjectId === undefined ? (
        <div style={{ marginBlockEnd: 'var(--space-3)' }}>
          <label htmlFor="review-project">
            {t('review:reviewStudio.project')}
            <input
              id="review-project"
              data-testid="review-filter-project"
              value={projectDraft}
              autoComplete="off"
              placeholder={t('review:reviewStudio.prj')}
              onChange={(event) => {
                setProjectDraft(event.target.value);
              }}
              onBlur={() => {
                writeProject(projectDraft);
              }}
              onKeyDown={(event) => {
                if (event.key === 'Enter') {
                  event.preventDefault();
                  writeProject(projectDraft);
                }
              }}
            />
          </label>
        </div>
      ) : null}
      <div style={{ display: 'flex', gap: 'var(--space-4)', alignItems: 'flex-start', flexWrap: 'wrap' }}>
        <div style={{ flex: '0 0 360px', minWidth: 0 }}>
          <ReviewQueue
            projectId={projectId}
            filters={{ ...EMPTY_REVIEW_FILTERS, ...filters, project: '' }}
            onFiltersChange={(next) => {
              writeFilters({ ...next, project: filters.project });
            }}
            selectedReviewId={selectedId}
            onSelect={setSelectedId}
          />
        </div>
        <div style={{ flexGrow: 1, minWidth: 0 }} data-testid="review-studio-detail">
          {selectedId === undefined ? (
            <p data-testid="review-studio-empty" className="dp-muted">
              {t('review:reviewStudio.select-a-review-to-see-its')}
            </p>
          ) : (
            <ReviewCard
              projectId={projectId}
              reviewId={selectedId}
              onSettled={() => {
                setSelectedId(undefined);
              }}
            />
          )}
        </div>
      </div>
    </section>
  );
}
