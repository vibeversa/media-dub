import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Pagination } from './Pagination.js';

afterEach(() => {
  cleanup();
});

describe('Pagination', () => {
  it('moves pages', () => {
    const onPageChange = vi.fn();
    render(<Pagination page={1} pageSize={20} total={60} onPageChange={onPageChange} />);
    expect(screen.getByText('Page 1 of 3')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(onPageChange).toHaveBeenCalledWith(2);
  });
});
