import type { Meta, StoryObj } from '@storybook/react';
import { CostDisplay } from './CostDisplay.js';

const meta: Meta<typeof CostDisplay> = {
  title: 'Product/CostDisplay',
  component: CostDisplay,
  args: { amountUsd: 12.5 },
};

export default meta;
type Story = StoryObj<typeof CostDisplay>;

export const Default: Story = {};
export const Zero: Story = { args: { amountUsd: 0 } };
