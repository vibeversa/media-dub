import type { Meta, StoryObj } from '@storybook/react';
import { Table } from './Table.js';

const meta: Meta<typeof Table<{ id: string }>> = {
  title: 'Primitives/Table',
  component: Table,
  args: {
    caption: 'Voices',
    getRowId: (r) => r.id,
    columns: [{ key: 'id', header: 'ID', render: (r) => r.id }],
    rows: [{ id: 'v1' }, { id: 'v2' }],
  },
};

export default meta;
type Story = StoryObj<typeof Table<{ id: string }>>;

export const Default: Story = {};
export const Empty: Story = { args: { rows: [] } };
