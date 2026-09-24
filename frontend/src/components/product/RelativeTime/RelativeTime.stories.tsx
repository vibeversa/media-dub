import type { Meta, StoryObj } from '@storybook/react';
import { RelativeTime } from './RelativeTime.js';

const meta: Meta<typeof RelativeTime> = {
  title: 'Product/RelativeTime',
  component: RelativeTime,
  args: { value: new Date(Date.now() - 3_600_000).toISOString(), locale: 'en' },
};

export default meta;
type Story = StoryObj<typeof RelativeTime>;

export const HourAgo: Story = {};
export const Invalid: Story = { args: { value: 'not-a-date' } };
