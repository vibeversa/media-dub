import type { Meta, StoryObj } from '@storybook/react';
import { CommandMenu } from './CommandMenu.js';

const meta: Meta<typeof CommandMenu> = {
  title: 'Primitives/CommandMenu',
  component: CommandMenu,
  args: {
    items: [
      { id: 'a', label: 'Go to dashboard' },
      { id: 'b', label: 'Go to review' },
    ],
  },
};

export default meta;
type Story = StoryObj<typeof CommandMenu>;

export const Default: Story = {};
