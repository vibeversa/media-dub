import type { Meta, StoryObj } from '@storybook/react';
import { Card } from './Card.js';

const meta: Meta<typeof Card> = {
  title: 'Primitives/Card',
  component: Card,
  args: { title: 'Usage', children: 'Card body.' },
};

export default meta;
type Story = StoryObj<typeof Card>;

export const Default: Story = {};
