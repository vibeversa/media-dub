import type { Meta, StoryObj } from '@storybook/react';
import { Select } from './Select.js';

const meta: Meta<typeof Select> = {
  title: 'Primitives/Select',
  component: Select,
  args: {
    label: 'Voice',
    options: [
      { value: 'a', label: 'Alpha' },
      { value: 'b', label: 'Beta' },
    ],
  },
};

export default meta;
type Story = StoryObj<typeof Select>;

export const Default: Story = {};
export const WithError: Story = { args: { error: 'Pick a voice.' } };
