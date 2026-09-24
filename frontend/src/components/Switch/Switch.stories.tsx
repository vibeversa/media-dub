import type { Meta, StoryObj } from '@storybook/react';
import { Switch } from './Switch.js';

const meta: Meta<typeof Switch> = {
  title: 'Primitives/Switch',
  component: Switch,
  args: { label: 'Enable SSE', checked: false },
};

export default meta;
type Story = StoryObj<typeof Switch>;

export const Off: Story = {};
export const On: Story = { args: { checked: true } };
