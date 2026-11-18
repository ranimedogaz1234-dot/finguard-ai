import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Badge, Pagination, RichText, ScoreBar, Modal } from '../components/ui';
import { humanize, money, pct } from '../lib/format';
import { qs } from '../api/client';

describe('format helpers', () => {
  it('formats money with currency and compact notation', () => {
    expect(money(1234.5, 'EUR')).toBe('€1,234.50');
    expect(money(1_250_000, 'EUR', true)).toBe('€1.3M');
  });
  it('humanizes enum and rule codes', () => {
    expect(humanize('ConfirmedFraud')).toBe('Confirmed fraud');
    expect(humanize('PAYMENT_DETAILS_MISMATCH')).toBe('Payment details mismatch');
  });
  it('computes safe percentages', () => {
    expect(pct(1, 4)).toBe('25.0%');
    expect(pct(1, 0)).toBe('0%');
  });
  it('builds query strings skipping empty values', () => {
    expect(qs({ q: '', page: 2, ids: ['a', 'b'], x: undefined })).toBe('?page=2&ids=a&ids=b');
    expect(qs({})).toBe('');
  });
});

describe('Badge and ScoreBar', () => {
  it('maps risk levels to level styles', () => {
    render(<Badge value="Critical" kind="level" />);
    expect(screen.getByText('Critical').className).toContain('b-critical');
  });
  it('exposes the score as an accessible meter', () => {
    render(<ScoreBar score={72} />);
    expect(screen.getByRole('meter', { name: 'Risk score' })).toHaveAttribute('aria-valuenow', '72');
  });
});

describe('Pagination', () => {
  it('disables navigation at the bounds and reports page changes', async () => {
    const onPage = vi.fn();
    render(<Pagination page={1} totalPages={3} total={45} onPage={onPage} label="invoices" />);
    expect(screen.getByRole('button', { name: 'Previous page' })).toBeDisabled();
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(onPage).toHaveBeenCalledWith(2);
    expect(screen.getByText('45 invoices')).toBeInTheDocument();
  });
});

describe('RichText (assistant output)', () => {
  it('renders bold, lists and italics without interpreting HTML', () => {
    const { container } = render(<RichText text={'**Summary**\n- first <img src=x onerror=alert(1)>\n- second\n_note_'} />);
    expect(screen.getByText('Summary').tagName).toBe('STRONG');
    const list = container.querySelector('ul')!;
    expect(within(list).getAllByRole('listitem')).toHaveLength(2);
    expect(container.querySelector('img')).toBeNull();
    expect(screen.getByText(/first <img/)).toBeInTheDocument();
    expect(screen.getByText('note').tagName).toBe('EM');
  });
  it('renders numbered lists as ordered lists', () => {
    const { container } = render(<RichText text={'1. one\n2. two'} />);
    expect(container.querySelectorAll('ol li')).toHaveLength(2);
  });
});

describe('Modal', () => {
  it('is an accessible dialog that closes on Escape', async () => {
    const onClose = vi.fn();
    render(<Modal title="Approve INV-1" onClose={onClose}><input aria-label="note" /></Modal>);
    expect(screen.getByRole('dialog', { name: 'Approve INV-1' })).toBeInTheDocument();
    expect(screen.getByLabelText('note')).toHaveFocus();
    await userEvent.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalled();
  });
});
