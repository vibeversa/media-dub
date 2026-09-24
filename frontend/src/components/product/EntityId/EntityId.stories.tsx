import type { Meta, StoryObj } from '@storybook/react';
import { EntityId } from './EntityId.js';

const meta: Meta<typeof EntityId> = {
  title: 'Product/EntityId',
  component: EntityId,
  args: { id: 'prj_123', label: 'Project' },
};

export default meta;
type Story = StoryObj<typeof EntityId>;

export const Default: Story = {};
