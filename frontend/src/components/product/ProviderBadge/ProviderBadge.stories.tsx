import type { Meta, StoryObj } from '@storybook/react';
import { ProviderBadge } from './ProviderBadge.js';

const meta: Meta<typeof ProviderBadge> = {
  title: 'Product/ProviderBadge',
  component: ProviderBadge,
  args: { status: 'Healthy', provider: 'tts' },
};

export default meta;
type Story = StoryObj<typeof ProviderBadge>;

export const Healthy: Story = {};
export const Degraded: Story = { args: { status: 'Degraded' } };
export const Down: Story = { args: { status: 'Down' } };
