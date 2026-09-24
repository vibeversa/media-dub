import type { Meta, StoryObj } from '@storybook/react';
import { Badge } from './Badge.js';

const meta: Meta<typeof Badge> = {
  title: 'Primitives/Badge',
  component: Badge,
  args: { children: 'Badge' },
};

export default meta;
type Story = StoryObj<typeof Badge>;

export const Success: Story = { args: { tone: 'success' } };
export const Warning: Story = { args: { tone: 'warning' } };
export const Error: Story = { args: { tone: 'error' } };
export const Info: Story = { args: { tone: 'info' } };
export const Neutral: Story = { args: { tone: 'neutral' } };
export const Processing: Story = { args: { tone: 'processing' } };
export const Review: Story = { args: { tone: 'review' } };
export const Cancelled: Story = { args: { tone: 'cancelled' } };
