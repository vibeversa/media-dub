import type { Meta, StoryObj } from '@storybook/react';
import { ErrorState } from './ErrorState.js';

const meta: Meta<typeof ErrorState> = {
  title: 'Primitives/ErrorState',
  component: ErrorState,
  args: { title: 'Load failed', message: 'Try again.', correlationId: 'abc-123' },
};

export default meta;
type Story = StoryObj<typeof ErrorState>;

export const Default: Story = {};
