import type { Meta, StoryObj } from '@storybook/react';
import { Tabs } from './Tabs.js';

const meta: Meta<typeof Tabs> = {
  title: 'Primitives/Tabs',
  component: Tabs,
  args: {
    items: [
      { id: 'a', label: 'Alpha', content: 'Panel A' },
      { id: 'b', label: 'Beta', content: 'Panel B' },
    ],
  },
};

export default meta;
type Story = StoryObj<typeof Tabs>;

export const Default: Story = {};
