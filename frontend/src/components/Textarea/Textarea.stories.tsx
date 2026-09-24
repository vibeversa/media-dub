import type { Meta, StoryObj } from '@storybook/react';
import { Textarea } from './Textarea.js';

const meta: Meta<typeof Textarea> = {
  title: 'Primitives/Textarea',
  component: Textarea,
  args: { label: 'Notes' },
};

export default meta;
type Story = StoryObj<typeof Textarea>;

export const Default: Story = {};
export const WithError: Story = { args: { error: 'Too long.' } };
