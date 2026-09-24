import type { Meta, StoryObj } from '@storybook/react';
import { StatusBadge } from './StatusBadge.js';

const meta: Meta<typeof StatusBadge> = {
  title: 'Primitives/StatusBadge',
  component: StatusBadge,
  args: { status: 'Running' },
};

export default meta;
type Story = StoryObj<typeof StatusBadge>;

export const Processing: Story = { args: { status: 'Running' } };
export const Success: Story = { args: { status: 'Completed' } };
export const Error: Story = { args: { status: 'Failed' } };
export const Review: Story = { args: { status: 'ManualReviewRequired' } };
export const Cancelled: Story = { args: { status: 'Cancelled' } };
export const Unknown: Story = { args: { status: 'SomethingNew' } };
