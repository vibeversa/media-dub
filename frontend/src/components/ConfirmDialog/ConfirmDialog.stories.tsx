import type { Meta, StoryObj } from '@storybook/react';
import { ConfirmDialog } from './ConfirmDialog.js';

const meta: Meta<typeof ConfirmDialog> = {
  title: 'Primitives/ConfirmDialog',
  component: ConfirmDialog,
  args: { open: true, title: 'Delete?', description: 'This cannot be undone.', confirmLabel: 'Delete' },
};

export default meta;
type Story = StoryObj<typeof ConfirmDialog>;

export const Open: Story = {};
