import type { Meta, StoryObj } from '@storybook/react';
import { Input } from './Input.js';

const meta: Meta<typeof Input> = {
  title: 'Primitives/Input',
  component: Input,
  args: { label: 'Name', placeholder: 'Ada Lovelace' },
};

export default meta;
type Story = StoryObj<typeof Input>;

export const Default: Story = {};
export const WithHint: Story = { args: { hint: 'Shown on the project card.' } };
export const WithError: Story = { args: { error: 'Name is required.' } };
export const Disabled: Story = { args: { disabled: true } };
