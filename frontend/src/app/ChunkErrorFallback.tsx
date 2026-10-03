import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

/**
 * Chunk-error copy (GAP-022). Its own module because the error boundary itself
 * must stay a class (React requires `componentDidCatch`), and a class cannot
 * call `useTranslation`.
 */
export function ChunkErrorFallback({ version }: { readonly version: string }): ReactNode {
  const { t } = useTranslation();
  return (
    <div role="alert" data-testid="chunk-error" className="mx-auto max-w-md py-12 text-center">
      <h1 className="text-lg font-semibold">{t('common:chunkError.title')}</h1>
      <p className="mt-2 text-sm dp-muted">{t('common:chunkError.description')}</p>
      <p className="mt-2 text-xs dp-muted">{`${t('common:chunkError.version')}: ${version}`}</p>
      <button
        type="button"
        onClick={() => window.location.reload()}
        className="dp-btn dp-btn-secondary dp-btn-md dp-focus-ring mt-4"
      >
        {t('common:chunkError.reload')}
      </button>
    </div>
  );
}
