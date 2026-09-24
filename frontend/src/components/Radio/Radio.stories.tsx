import type { Meta, StoryObj } from '@storybook/react';
import { Radio } from './Radio.js';

const meta: Meta<typeof Radio> = {
  title: 'Primitives/Radio',
  component: Radio,
  args: { label: 'Option A', name: 'demo' },
};

export default meta;
type Story = StoryObj<typeof Radio>;

export const Default: Story = {};
export const Checked: Story = { args: { checked: true } };
