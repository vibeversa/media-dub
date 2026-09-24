import type { Meta, StoryObj } from '@storybook/react';
import { Combobox } from './Combobox.js';

const meta: Meta<typeof Combobox> = {
  title: 'Primitives/Combobox',
  component: Combobox,
  args: {
    label: 'Language',
    options: [
      { value: 'en', label: 'English' },
      { value: 'es', label: 'Spanish' },
    ],
  },
};

export default meta;
type Story = StoryObj<typeof Combobox>;

export const Default: Story = {};
export const Selected: Story = { args: { value: 'en' } };
