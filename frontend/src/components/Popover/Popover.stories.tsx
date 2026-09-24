import type { Meta, StoryObj } from '@storybook/react';
import { Popover } from './Popover.js';

const meta: Meta<typeof Popover> = {
  title: 'Primitives/Popover',
  component: Popover,
  args: { label: 'More', content: 'Popover body.' },
};

export default meta;
type Story = StoryObj<typeof Popover>;

export const Default: Story = {};
