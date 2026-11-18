const moneyFormatters = new Map<string, Intl.NumberFormat>();
// Amounts use en-US number conventions: "€1,234.50" and compact "€1.3M". en-GB's compact suffixes changed in
// recent ICU/CLDR data ("€1.3m"), which made the UI's output depend on the browser/Node version.
const MONEY_LOCALE = 'en-US';

export function money(value: number, currency = 'EUR', compact = false): string {
  const key = `${currency}-${compact}`;
  let f = moneyFormatters.get(key);
  if (!f) {
    f = new Intl.NumberFormat(MONEY_LOCALE, {
      style: 'currency',
      currency,
      notation: compact ? 'compact' : 'standard',
      maximumFractionDigits: compact ? 1 : 2,
      minimumFractionDigits: compact ? 0 : 2,
    });
    moneyFormatters.set(key, f);
  }
  return f.format(value);
}

export const num = (v: number) => new Intl.NumberFormat('en-GB').format(v);
export const pct = (part: number, whole: number) => (whole === 0 ? '0%' : `${((part / whole) * 100).toFixed(1)}%`);

export function date(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = iso.length === 10 ? new Date(`${iso}T00:00:00`) : new Date(iso);
  return d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
}

export function dateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleString('en-GB', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' });
}

export function relative(iso: string, now = Date.now()): string {
  const diff = (now - new Date(iso).getTime()) / 1000;
  if (diff < 60) return 'just now';
  if (diff < 3600) return `${Math.floor(diff / 60)} min ago`;
  if (diff < 86400) return `${Math.floor(diff / 3600)} h ago`;
  if (diff < 86400 * 30) return `${Math.floor(diff / 86400)} d ago`;
  return date(iso);
}

/** "ConfirmedFraud" → "Confirmed fraud", "PAYMENT_DETAILS_MISMATCH" → "Payment details mismatch" */
export function humanize(s: string): string {
  const spaced = s.includes('_') ? s.toLowerCase().replace(/_/g, ' ') : s.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

export const ACTION_LABELS: Record<string, string> = {
  'invoice.created': 'submitted invoice',
  'invoice.verified': 'verified invoice',
  'invoice.reverified': 're-verified invoice',
  'invoice.approved': 'approved invoice',
  'invoice.approved_override': 'approved flagged invoice',
  'invoice.rejected': 'rejected invoice',
  'invoice.paid': 'recorded payment',
  'invoice.pdf_attached': 'attached PDF',
  'supplier.created': 'onboarded supplier',
  'supplier.updated': 'updated supplier',
  'supplier.bank_details_changed': 'changed bank details',
  'supplier.archived': 'archived supplier',
  'supplier.restored': 'restored supplier',
  'supplier.risk_reassessed': 'reassessed risk',
  'alert.status_changed': 'updated alert',
  'alert.assigned': 'assigned alert',
  'alert.note_added': 'commented on alert',
  'auth.login': 'signed in',
  'auth.logout': 'signed out',
  'auth.locked_out': 'account locked',
  'user.registered': 'registered',
  'user.role_changed': 'changed role',
  'demo.seeded': 'generated demo data',
  'demo.purged': 'removed demo data',
  'assistant.query': 'asked the assistant',
};
