import type { ReactNode } from 'react';
import { CreateWizard } from '../../features/projects/wizard/CreateWizard.js';

/** Project creation route: renders the Task 022 wizard (one lazy chunk). */
export default function ProjectCreatePage(): ReactNode {
  return <CreateWizard />;
}
