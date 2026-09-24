import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { NavLink, Outlet } from 'react-router-dom';
import { EMPTY_WORKSPACE_STATE, getProjectTabs } from '../navigation/projectTabs.js';
import type { ProjectWorkspaceState } from '../navigation/projectTabs.js';

export interface ProjectLayoutProps {
  /**
   * Workspace-derived tab state. Feature tasks pass the live aggregate;
   * defaults to the empty state (state-gated tabs disabled with reason
   * tooltips until real data arrives).
   */
  readonly workspaceState?: ProjectWorkspaceState;
}

/**
 * Project workspace shell (Task 018): project header + adaptive tab bar +
 * nested outlet. Tabs come from `getProjectTabs` (R5) — never hardcoded per
 * page. Disabled tabs render as plain text with a reason tooltip (no dead
 * links); badged tabs carry counts only.
 */
export function ProjectLayout({ workspaceState }: ProjectLayoutProps): ReactNode {
  const { t } = useTranslation();
  const tabs = getProjectTabs(workspaceState ?? EMPTY_WORKSPACE_STATE);
  return (
    <div data-testid="project-layout">
      <h1 className="text-xl font-semibold">{t('nav:projectTabs.header')}</h1>
      <nav aria-label={t('nav:projectTabs.header')} className="mt-3 border-b">
        <ul className="flex flex-wrap gap-4">
          {tabs.map((tab) => (
            <li key={tab.id}>
              {tab.disabled ? (
                <span aria-disabled="true" title={tab.disabledReasonKey !== undefined ? t(tab.disabledReasonKey) : undefined}>
                  {t(tab.labelKey)}
                </span>
              ) : (
                <NavLink to={tab.to} end={tab.id === 'overview'} data-testid={`project-tab-${tab.id}`}>
                  {t(tab.labelKey)}
                  {tab.badge !== undefined && <span data-testid={`project-tab-${tab.id}-badge`}>{tab.badge}</span>}
                </NavLink>
              )}
            </li>
          ))}
        </ul>
      </nav>
      <div className="mt-4">
        <Outlet />
      </div>
    </div>
  );
}
