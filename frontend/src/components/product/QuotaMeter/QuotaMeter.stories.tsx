import type { Meta, StoryObj } from '@storybook/react';
import { QuotaMeter } from './QuotaMeter.js';

const meta: Meta<typeof QuotaMeter> = {
  title: 'Product/QuotaMeter',
  component: QuotaMeter,
  args: { used: 40, quota: 100, label: 'Storage' },
};

export default meta;
type Story = StoryObj<typeof QuotaMeter>;

export const Normal: Story = {};
export const Warning: Story = { args: { used: 90 } };
