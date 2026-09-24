import type { Meta, StoryObj } from '@storybook/react';
import { Tooltip } from './Tooltip.js';

const meta: Meta<typeof Tooltip> = {
  title: 'Primitives/Tooltip',
  component: Tooltip,
  args: { label: 'Help', content: 'Plain hint', children: 'Hover me' },
};

export default meta;
type Story = StoryObj<typeof Tooltip>;

export const Default: Story = {};
