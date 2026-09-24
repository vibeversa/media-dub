import type { Meta, StoryObj } from '@storybook/react';
import { Slider } from './Slider.js';

const meta: Meta<typeof Slider> = {
  title: 'Primitives/Slider',
  component: Slider,
  args: { label: 'Volume', min: 0, max: 100, defaultValue: 40 },
};

export default meta;
type Story = StoryObj<typeof Slider>;

export const Default: Story = {};
export const Disabled: Story = { args: { disabled: true } };
