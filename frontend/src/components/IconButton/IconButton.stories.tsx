import type { Meta, StoryObj } from '@storybook/react';
import { IconButton } from './IconButton.js';

const meta: Meta<typeof IconButton> = {
  title: 'Primitives/IconButton',
  component: IconButton,
  args: { label: 'Close', children: 'X' },
};

export default meta;
type Story = StoryObj<typeof IconButton>;

export const Default: Story = {};
export const Disabled: Story = { args: { disabled: true } };
