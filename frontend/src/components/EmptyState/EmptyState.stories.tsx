import type { Meta, StoryObj } from '@storybook/react';
import { EmptyState } from './EmptyState.js';

const meta: Meta<typeof EmptyState> = {
  title: 'Primitives/EmptyState',
  component: EmptyState,
  args: { title: 'No projects', description: 'Create your first project.' },
};

export default meta;
type Story = StoryObj<typeof EmptyState>;

export const Default: Story = {};
