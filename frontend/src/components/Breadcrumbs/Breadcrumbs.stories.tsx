import type { Meta, StoryObj } from '@storybook/react';
import { Breadcrumbs } from './Breadcrumbs.js';

const meta: Meta<typeof Breadcrumbs> = {
  title: 'Primitives/Breadcrumbs',
  component: Breadcrumbs,
  args: { items: [{ label: 'Projects', href: '/projects' }, { label: 'Detail' }] },
};

export default meta;
type Story = StoryObj<typeof Breadcrumbs>;

export const Default: Story = {};
