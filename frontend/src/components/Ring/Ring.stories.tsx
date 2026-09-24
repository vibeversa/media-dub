import type { Meta, StoryObj } from '@storybook/react';
import { Ring } from './Ring.js';

const meta: Meta<typeof Ring> = {
  title: 'Primitives/Ring',
  component: Ring,
  args: { value: 65 },
};

export default meta;
type Story = StoryObj<typeof Ring>;

export const Default: Story = {};
export const Full: Story = { args: { value: 100 } };
