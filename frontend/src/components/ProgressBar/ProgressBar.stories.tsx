import type { Meta, StoryObj } from '@storybook/react';
import { ProgressBar } from './ProgressBar.js';

const meta: Meta<typeof ProgressBar> = {
  title: 'Primitives/ProgressBar',
  component: ProgressBar,
  args: { value: 40 },
};

export default meta;
type Story = StoryObj<typeof ProgressBar>;

export const Partial: Story = {};
export const Complete: Story = { args: { value: 100 } };
