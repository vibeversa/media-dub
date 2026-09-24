import type { Meta, StoryObj } from '@storybook/react';
import { Alert } from './Alert.js';

const meta: Meta<typeof Alert> = {
  title: 'Primitives/Alert',
  component: Alert,
  args: { tone: 'info', title: 'Heads up', children: 'Body text.' },
};

export default meta;
type Story = StoryObj<typeof Alert>;

export const Info: Story = {};
export const Success: Story = { args: { tone: 'success', title: 'Done' } };
export const Warning: Story = { args: { tone: 'warning', title: 'Check this' } };
export const Error: Story = { args: { tone: 'error', title: 'Failed', details: 'code=PROVIDER_FAILED' } };
