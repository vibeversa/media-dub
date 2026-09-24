import type { Meta, StoryObj } from '@storybook/react';
import { Drawer } from './Drawer.js';

const meta: Meta<typeof Drawer> = {
  title: 'Primitives/Drawer',
  component: Drawer,
  args: { open: true, title: 'Filters', children: 'Drawer body.' },
};

export default meta;
type Story = StoryObj<typeof Drawer>;

export const Open: Story = {};
export const Closed: Story = { args: { open: false } };
