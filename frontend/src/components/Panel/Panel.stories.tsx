import type { Meta, StoryObj } from '@storybook/react';
import { Panel } from './Panel.js';

const meta: Meta<typeof Panel> = {
  title: 'Primitives/Panel',
  component: Panel,
  args: { title: 'Quality', children: 'Panel body.' },
};

export default meta;
type Story = StoryObj<typeof Panel>;

export const Default: Story = {};
