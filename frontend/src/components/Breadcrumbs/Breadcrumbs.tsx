import type { ReactNode } from 'react';

export interface Crumb {
  readonly label: string;
  readonly href?: string;
}

export interface BreadcrumbsProps {
  readonly items: readonly Crumb[];
}

/** Breadcrumb trail; last item is current page. */
export function Breadcrumbs({ items }: BreadcrumbsProps): ReactNode {
  return (
    <>
      <style>{`.dp-crumbs{display:flex;flex-wrap:wrap;gap:var(--space-1);font-size:var(--font-size-sm);color:var(--color-text-muted)}.dp-crumbs a{color:var(--color-brand)}.dp-crumb-sep{margin-inline:var(--space-1)}`}</style>
      <nav aria-label="Breadcrumb">
        <ol className="dp-crumbs">
          {items.map((item, i) => {
            const last = i === items.length - 1;
            return (
              <li key={`${item.label}-${i}`}>
                {item.href && !last ? <a href={item.href}>{item.label}</a> : <span aria-current={last ? 'page' : undefined}>{item.label}</span>}
                {!last ? <span aria-hidden="true" className="dp-crumb-sep">/</span> : null}
              </li>
            );
          })}
        </ol>
      </nav>
    </>
  );
}
