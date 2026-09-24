import type { ReactNode } from 'react';
import { ProjectsPage as FeatureProjectsPage } from '../../features/projects/ProjectsPage.js';

/** Project list route: renders the Task 021 filterable list (one lazy chunk). */
export default function ProjectsPage(): ReactNode {
  return <FeatureProjectsPage />;
}
