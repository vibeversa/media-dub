import type { Meta, StoryObj } from '@storybook/react';
import { CorrelationId } from './CorrelationId.js';

const meta: Meta<typeof CorrelationId> = {
  title: 'Product/CorrelationId',
  component: CorrelationId,
  args: { value: 'corr-123' },
};

export default meta;
type Story = StoryObj<typeof CorrelationId>;

export const Default: Story = {};
