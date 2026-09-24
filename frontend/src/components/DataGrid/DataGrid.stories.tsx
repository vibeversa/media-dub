import type { Meta, StoryObj } from '@storybook/react';
import { DataGrid } from './DataGrid.js';

const meta: Meta<typeof DataGrid<{ id: string }>> = {
  title: 'Primitives/DataGrid',
  component: DataGrid,
  args: {
    caption: 'Segments',
    getRowId: (r) => r.id,
    columns: [{ key: 'id', header: 'ID', render: (r) => r.id }],
    rows: [{ id: 's1' }, { id: 's2' }],
  },
};

export default meta;
type Story = StoryObj<typeof DataGrid<{ id: string }>>;

export const Default: Story = {};
