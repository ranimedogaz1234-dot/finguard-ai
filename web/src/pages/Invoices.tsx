import { useMemo, useRef, useState, type DragEvent, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { CheckCircle2, Download, FileCheck2, FileText, FileUp, Plus, RefreshCw, ScanText, Trash2, Upload, XCircle, Wallet } from 'lucide-react';
import { api, ApiError, qs } from '../api/client';
import type { Extraction, InvoiceDetail, InvoiceInput, InvoiceLine, InvoiceListItem, Paged, SupplierListItem } from '../api/types';
import { PageHead } from '../components/Layout';
import { Badge, Card, DemoBadge, Empty, ErrorState, Field, Modal, Pagination, Skeleton, SkeletonRows, errorText, useToast } from '../components/ui';
import { date, dateTime, humanize, money } from '../lib/format';
import { useListState } from '../lib/hooks';
import { SearchBox, SortHeader } from './Suppliers';

export function InvoicesPage() {
  const { state, set, toggleSort } = useListState({ q: '', status: '', decision: '', supplierId: '', from: '', to: '', sort: 'date', dir: 'desc', page: '1' });
  const navigate = useNavigate();
  const list = useQuery({
    queryKey: ['invoices', state],
    queryFn: () => api.get<Paged<InvoiceListItem>>(`/api/invoices${qs({ ...state, pageSize: 15 })}`),
    placeholderData: keepPreviousData,
  });
  const filtered = state.q || state.status || state.decision || state.supplierId || state.from || state.to;

  return (
    <>
      <PageHead title="Invoice verification" subtitle="Every invoice is checked for duplicates, arithmetic, purchase-order match and payment-detail consistency."
        actions={<Link to="/invoices/new" className="btn btn-primary"><Plus size={16} /> New invoice</Link>} />
      <Card>
        <div className="toolbar">
          <SearchBox value={state.q} onChange={(q) => set({ q })} placeholder="Search number, supplier or PO" />
          <select className="select" aria-label="Rule decision" value={state.decision} onChange={(e) => set({ decision: e.target.value })}>
            <option value="">All decisions</option><option value="AutoApprove">Passed rules</option><option value="flagged">Flagged (any)</option>
            <option value="ManualReview">Manual review</option><option value="Reject">Reject recommended</option>
          </select>
          <select className="select" aria-label="Status" value={state.status} onChange={(e) => set({ status: e.target.value })}>
            <option value="">All statuses</option>{['Pending', 'Approved', 'Rejected', 'Paid'].map((s) => <option key={s}>{s}</option>)}
          </select>
          <input className="input" style={{ width: 150 }} type="date" aria-label="From date" value={state.from} onChange={(e) => set({ from: e.target.value })} />
          <input className="input" style={{ width: 150 }} type="date" aria-label="To date" value={state.to} onChange={(e) => set({ to: e.target.value })} />
          {filtered && <button className="btn btn-sm btn-ghost" onClick={() => set({ q: '', status: '', decision: '', supplierId: '', from: '', to: '' })}>Clear filters</button>}
        </div>
        {list.isLoading ? <SkeletonRows /> : list.isError ? <ErrorState error={list.error} onRetry={() => list.refetch()} /> : list.data!.items.length === 0 ? (
          <Empty title={filtered ? 'No invoices match these filters' : 'No invoices yet'} icon={<FileText size={22} />}>
            {filtered ? 'Adjust or clear the filters.' : <Link to="/invoices/new" style={{ color: 'var(--cyan)' }}>Submit the first invoice</Link>}
          </Empty>
        ) : (
          <>
            <div className="table-wrap" style={{ opacity: list.isFetching ? 0.65 : 1, transition: 'opacity .2s' }}>
              <table className="data">
                <thead>
                  <tr>
                    <SortHeader label="Invoice" k="number" {...state} onSort={toggleSort} />
                    <SortHeader label="Supplier" k="supplier" {...state} onSort={toggleSort} className="hide-sm" />
                    <SortHeader label="Date" k="date" {...state} onSort={toggleSort} className="hide-xs" />
                    <SortHeader label="Amount" k="amount" {...state} onSort={toggleSort} className="right" />
                    <SortHeader label="Rules" k="risk" {...state} onSort={toggleSort} />
                    <th>Status</th>
                  </tr>
                </thead>
                <tbody>
                  {list.data!.items.map((i) => (
                    <tr key={i.id} className="clickable" onClick={() => navigate(`/invoices/${i.id}`)}>
                      <td>
                        <Link to={`/invoices/${i.id}`} className="cell-title" onClick={(e) => e.stopPropagation()}>{i.number}</Link>
                        <div className="cell-sub row" style={{ gap: 6 }}>
                          {i.discrepancyCount > 0 ? `${i.discrepancyCount} finding(s)` : 'No findings'}{i.hasPdf && <FileText size={12} aria-label="PDF attached" />}{i.isDemo && <DemoBadge />}
                        </div>
                      </td>
                      <td className="hide-sm">{i.supplierName}</td>
                      <td className="hide-xs">{date(i.invoiceDate)}</td>
                      <td className="right num">{money(i.total, i.currency)}</td>
                      <td><Badge value={i.decision} label={i.decision === 'AutoApprove' ? 'Passed' : i.decision === 'Reject' ? 'Reject' : 'Review'} /></td>
                      <td><Badge value={i.status} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={list.data!.page} totalPages={list.data!.totalPages} total={list.data!.total} label="invoices" onPage={(p) => set({ page: String(p) }, false)} />
          </>
        )}
      </Card>
    </>
  );
}

const today = () => new Date().toISOString().slice(0, 10);
const blankLine = (): InvoiceLine => ({ description: '', quantity: 1, unitPrice: 0 });

export function InvoiceNewPage() {
  const navigate = useNavigate();
  const toast = useToast();
  const qc = useQueryClient();
  const suppliers = useQuery({ queryKey: ['suppliers', 'all-active'], queryFn: () => api.get<Paged<SupplierListItem>>('/api/suppliers?sort=name&dir=asc&pageSize=100') });
  const [form, setForm] = useState<InvoiceInput>({ supplierId: '', number: '', poNumber: null, invoiceDate: today(), dueDate: null, currency: 'EUR', declaredTotal: 0, iban: null, lines: [blankLine()] });
  const [file, setFile] = useState<File | null>(null);
  const [extraction, setExtraction] = useState<Extraction | null>(null);
  const [over, setOver] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const fileInput = useRef<HTMLInputElement>(null);
  const linesTotal = useMemo(() => form.lines.reduce((s, l) => s + Math.round(l.quantity * l.unitPrice * 100) / 100, 0), [form.lines]);

  const extract = useMutation({
    mutationFn: (f: File) => api.upload<Extraction>('/api/invoices/extract', f),
    onSuccess: (x) => {
      setExtraction(x);
      if (!x.textFound) return;
      setForm((f) => ({
        ...f,
        supplierId: x.matchedSupplierId ?? f.supplierId,
        number: x.number ?? f.number,
        invoiceDate: x.invoiceDate ?? f.invoiceDate,
        dueDate: x.dueDate ?? f.dueDate,
        currency: x.currency ?? f.currency,
        declaredTotal: x.total ?? f.declaredTotal,
        iban: x.iban ?? f.iban,
        poNumber: x.poNumber ?? f.poNumber,
        lines: f.lines.length === 1 && !f.lines[0].description && x.total ? [{ description: 'Invoice total (from PDF, edit lines as needed)', quantity: 1, unitPrice: x.total }] : f.lines,
      }));
    },
    onError: (e) => setError(errorText(e)),
  });

  const create = useMutation({
    mutationFn: async () => {
      const { id } = await api.post<{ id: string }>('/api/invoices', form);
      if (file) {
        try { await api.upload(`/api/invoices/${id}/pdf`, file); } catch (e) { toast('error', `Invoice saved, but the PDF upload failed: ${errorText(e)}`); }
      }
      return id;
    },
    onSuccess: (id) => {
      qc.invalidateQueries({ queryKey: ['invoices'] });
      qc.invalidateQueries({ queryKey: ['dashboard'] });
      qc.invalidateQueries({ queryKey: ['alerts'] });
      toast('success', 'Invoice submitted and verified');
      navigate(`/invoices/${id}`);
    },
    onError: (e) => { setError(errorText(e)); if (e instanceof ApiError && e.fieldErrors) setFieldErrors(e.fieldErrors); },
  });

  function pick(f: File | undefined) {
    if (!f) return;
    setError(null);
    if (f.type !== 'application/pdf' && !f.name.toLowerCase().endsWith('.pdf')) { setError('Only PDF files are accepted.'); return; }
    if (f.size > 5 * 1024 * 1024) { setError('The PDF must be 5 MB or smaller.'); return; }
    setFile(f);
    extract.mutate(f);
  }
  const onDrop = (e: DragEvent) => { e.preventDefault(); setOver(false); pick(e.dataTransfer.files[0]); };
  const setLine = (i: number, patch: Partial<InvoiceLine>) => setForm((f) => ({ ...f, lines: f.lines.map((l, j) => (j === i ? { ...l, ...patch } : l)) }));
  const fe = (k: string) => Object.entries(fieldErrors).find(([key]) => key.toLowerCase() === k.toLowerCase())?.[1]?.[0];
  const submit = (e: FormEvent) => { e.preventDefault(); setError(null); setFieldErrors({}); create.mutate(); };
  const mismatch = Math.abs(linesTotal - form.declaredTotal) > 0.01;

  return (
    <>
      <PageHead crumbs={[{ to: '/invoices', label: 'Invoices' }]} title="Submit an invoice" subtitle="The invoice is verified as soon as it is saved. Approval is a separate human step." />
      <form onSubmit={submit} noValidate className="grid grid-3" style={{ alignItems: 'start' }}>
        <div className="stack">
          <Card title="Invoice details">
            <div className="form-grid">
              <Field label="Supplier" className="full" error={fe('supplierId')}>
                {(id) => (
                  <select id={id} className="select" value={form.supplierId} onChange={(e) => setForm({ ...form, supplierId: e.target.value })} required>
                    <option value="">{suppliers.isLoading ? 'Loading suppliers…' : 'Select a supplier'}</option>
                    {suppliers.data?.items.map((s) => <option key={s.id} value={s.id}>{s.name} ({s.riskLevel} risk)</option>)}
                  </select>
                )}
              </Field>
              <Field label="Invoice number" error={fe('number')}>{(id) => <input id={id} className="input" value={form.number} onChange={(e) => setForm({ ...form, number: e.target.value })} required maxLength={64} />}</Field>
              <Field label="Purchase order" hint="Optional, but invoices without a PO are flagged.">{(id, d) => <input id={id} className="input" value={form.poNumber ?? ''} aria-describedby={d} onChange={(e) => setForm({ ...form, poNumber: e.target.value || null })} placeholder="PO-7001" />}</Field>
              <Field label="Invoice date">{(id) => <input id={id} className="input" type="date" value={form.invoiceDate} onChange={(e) => setForm({ ...form, invoiceDate: e.target.value })} required />}</Field>
              <Field label="Due date">{(id) => <input id={id} className="input" type="date" value={form.dueDate ?? ''} onChange={(e) => setForm({ ...form, dueDate: e.target.value || null })} />}</Field>
              <Field label="Currency" error={fe('currency')}>{(id) => <select id={id} className="select" value={form.currency} onChange={(e) => setForm({ ...form, currency: e.target.value })}>{['EUR', 'USD', 'GBP', 'TND', 'CHF'].map((c) => <option key={c}>{c}</option>)}</select>}</Field>
              <Field label="Declared total" error={fe('declaredTotal')} hint={mismatch ? `Lines add up to ${money(linesTotal, form.currency)}` : undefined}>
                {(id, d) => <input id={id} className="input num" type="number" step="0.01" min={0} value={form.declaredTotal} aria-describedby={d} onChange={(e) => setForm({ ...form, declaredTotal: Number(e.target.value) })} />}
              </Field>
              <Field label="Pay to IBAN (as printed on the invoice)" className="full" hint="Compared with the verified account on the supplier record.">
                {(id, d) => <input id={id} className="input mono" value={form.iban ?? ''} aria-describedby={d} onChange={(e) => setForm({ ...form, iban: e.target.value || null })} />}
              </Field>
            </div>
          </Card>
          <Card title="Line items" actions={<button type="button" className="btn btn-sm" onClick={() => setForm({ ...form, lines: [...form.lines, blankLine()] })} disabled={form.lines.length >= 100}><Plus size={14} /> Add line</button>}>
            <div className="table-wrap">
              <table className="data lines-table">
                <thead><tr><th>Description</th><th style={{ width: 100 }}>Qty</th><th style={{ width: 140 }}>Unit price</th><th className="right" style={{ width: 120 }}>Total</th><th style={{ width: 40 }}><span className="sr-only">Remove</span></th></tr></thead>
                <tbody>
                  {form.lines.map((l, i) => (
                    <tr key={i}>
                      <td><input className="input" aria-label={`Line ${i + 1} description`} value={l.description} onChange={(e) => setLine(i, { description: e.target.value })} /></td>
                      <td><input className="input num" aria-label={`Line ${i + 1} quantity`} type="number" min={0} step="any" value={l.quantity} onChange={(e) => setLine(i, { quantity: Number(e.target.value) })} /></td>
                      <td><input className="input num" aria-label={`Line ${i + 1} unit price`} type="number" min={0} step="0.01" value={l.unitPrice} onChange={(e) => setLine(i, { unitPrice: Number(e.target.value) })} /></td>
                      <td className="right num">{money(l.quantity * l.unitPrice, form.currency)}</td>
                      <td><button type="button" className="btn btn-ghost btn-sm btn-icon" aria-label={`Remove line ${i + 1}`} disabled={form.lines.length === 1} onClick={() => setForm({ ...form, lines: form.lines.filter((_, j) => j !== i) })}><Trash2 size={14} /></button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="row" style={{ justifyContent: 'flex-end', marginTop: 12, gap: 18 }}>
              <span className="muted">Lines total</span><strong className="num">{money(linesTotal, form.currency)}</strong>
              <button type="button" className="btn btn-sm" onClick={() => setForm({ ...form, declaredTotal: Math.round(linesTotal * 100) / 100 })} disabled={!mismatch}>Use as declared total</button>
            </div>
          </Card>
        </div>
        <div className="stack">
          <Card title="Invoice PDF">
            <div className={`drop ${over ? 'over' : ''}`} role="button" tabIndex={0} onClick={() => fileInput.current?.click()} onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && fileInput.current?.click()}
              onDragOver={(e) => { e.preventDefault(); setOver(true); }} onDragLeave={() => setOver(false)} onDrop={onDrop} aria-label="Upload invoice PDF">
              <FileUp size={26} style={{ color: 'var(--cyan)' }} />
              <div style={{ fontWeight: 600, marginTop: 6 }}>{file ? file.name : 'Drop a PDF or click to browse'}</div>
              <div className="subtle">PDF only · max 5 MB · text-based PDFs can be read automatically</div>
              <input ref={fileInput} type="file" accept="application/pdf,.pdf" hidden onChange={(e) => pick(e.target.files?.[0])} />
            </div>
            {extract.isPending && <div className="callout info" style={{ marginTop: 12 }}><ScanText size={18} /> Reading the PDF…</div>}
            {extraction && (
              <div className={`callout ${extraction.textFound ? 'info' : 'warn'}`} style={{ marginTop: 12 }}>
                <div>
                  <strong>{extraction.textFound ? 'Fields pre-filled from the PDF' : 'No text found'}</strong>
                  <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>{extraction.notes.map((n, i) => <li key={i}>{n}</li>)}</ul>
                </div>
              </div>
            )}
          </Card>
          <Card title="What happens next">
            <ol style={{ margin: 0, paddingLeft: 18, display: 'grid', gap: 8 }} className="muted">
              <li>Verification rules run: duplicates, totals, PO match, prices, quantities.</li>
              <li>Fraud rules check bank details, unusual amounts and split invoicing.</li>
              <li>Alerts are raised for suspicious patterns and the supplier is re-scored.</li>
              <li>A different team member approves or rejects. You can't approve your own invoice.</li>
            </ol>
          </Card>
          {error && <div className="callout danger" role="alert">{error}</div>}
          <button className="btn btn-primary" style={{ height: 44 }} disabled={create.isPending || !form.supplierId || !form.number || form.lines.some((l) => !l.description)}>
            <FileCheck2 size={17} /> {create.isPending ? 'Verifying…' : 'Submit & verify'}
          </button>
        </div>
      </form>
    </>
  );
}

export function InvoiceDetailPage() {
  const { id = '' } = useParams();
  const qc = useQueryClient();
  const toast = useToast();
  const [review, setReview] = useState<'approve' | 'reject' | null>(null);
  const [note, setNote] = useState('');
  const pdfInput = useRef<HTMLInputElement>(null);
  const q = useQuery({ queryKey: ['invoice', id], queryFn: () => api.get<InvoiceDetail>(`/api/invoices/${id}`) });

  const after = (msg: string) => (d: InvoiceDetail | void) => {
    if (d) qc.setQueryData(['invoice', id], d); else qc.invalidateQueries({ queryKey: ['invoice', id] });
    qc.invalidateQueries({ queryKey: ['invoices'] });
    qc.invalidateQueries({ queryKey: ['dashboard'] });
    toast('success', msg);
  };
  const act = useMutation({
    mutationFn: (kind: 'approve' | 'reject') => api.post<InvoiceDetail>(`/api/invoices/${id}/${kind}`, { note: note || null }),
    onSuccess: (d, kind) => { after(kind === 'approve' ? 'Invoice approved' : 'Invoice rejected')(d); setReview(null); setNote(''); },
  });
  const paid = useMutation({ mutationFn: () => api.post<InvoiceDetail>(`/api/invoices/${id}/mark-paid`), onSuccess: after('Payment recorded (simulated)'), onError: (e) => toast('error', errorText(e)) });
  const reverify = useMutation({ mutationFn: () => api.post<InvoiceDetail>(`/api/invoices/${id}/verify`), onSuccess: after('Verification re-run'), onError: (e) => toast('error', errorText(e)) });
  const upload = useMutation({ mutationFn: (f: File) => api.upload(`/api/invoices/${id}/pdf`, f), onSuccess: () => after('PDF attached')(), onError: (e) => toast('error', errorText(e)) });

  if (q.isError) return <Card><ErrorState error={q.error} onRetry={() => q.refetch()} /></Card>;
  const d = q.data;
  const flagged = d && d.decision !== 'AutoApprove';

  return (
    <>
      <PageHead
        crumbs={[{ to: '/invoices', label: 'Invoices' }]}
        title={d ? <span className="row" style={{ gap: 10 }}>Invoice {d.number}{d.isDemo && <DemoBadge />}</span> : <Skeleton h={28} w={240} />}
        subtitle={d && <>From <Link to={`/suppliers/${d.supplierId}`} style={{ color: 'var(--cyan)' }}>{d.supplierName}</Link> · {date(d.invoiceDate)} · <strong className="num">{money(d.total, d.currency)}</strong></>}
        actions={d && <>
          <a className="btn" href={`/api/invoices/${id}/report`} download><Download size={16} /> Report</a>
          {d.status === 'Pending' && <button className="btn" onClick={() => reverify.mutate()} disabled={reverify.isPending}><RefreshCw size={16} /> Re-verify</button>}
          {d.permissions.canMarkPaid && <button className="btn" onClick={() => confirm('Record this invoice as paid? This is a simulation; no money moves.') && paid.mutate()}><Wallet size={16} /> Mark paid</button>}
          {d.status === 'Pending' && <>
            <button className="btn btn-danger" onClick={() => setReview('reject')} disabled={!d.permissions.canReject}><XCircle size={16} /> Reject</button>
            <button className="btn btn-primary" onClick={() => setReview('approve')} disabled={!d.permissions.canApprove} title={d.permissions.reason ?? undefined}><CheckCircle2 size={16} /> Approve</button>
          </>}
        </>}
      />
      {!d ? <Skeleton h={420} /> : (
        <div className="stack">
          {d.status === 'Pending' && d.permissions.reason && <div className="callout info"><span><strong>Approval policy:</strong> {d.permissions.reason}</span></div>}
          {d.status === 'Approved' && d.permissions.reason && <div className="callout warn"><span><strong>Payment:</strong> {d.permissions.reason}</span></div>}
          <div className="grid grid-3">
            <Card title="Verification result" actions={<><Badge value={d.decision} label={humanize(d.decision)} /><Badge value={d.status} /></>}>
              <div className={`callout ${d.decision === 'AutoApprove' ? 'ok' : d.decision === 'Reject' ? 'danger' : 'warn'}`}>
                <div><strong>{d.verificationPoints}/100 verification points</strong><div>{d.summary}</div></div>
              </div>
              {d.discrepancies.length > 0 && (
                <div className="table-wrap" style={{ marginTop: 14 }}>
                  <table className="data">
                    <thead><tr><th>Severity</th><th>Rule</th><th>Explanation</th></tr></thead>
                    <tbody>{d.discrepancies.map((x, i) => (
                      <tr key={i}><td><Badge value={x.severity} /></td><td className="mono">{x.code}</td><td>{x.explanation}</td></tr>
                    ))}</tbody>
                  </table>
                </div>
              )}
              <p className="subtle" style={{ marginTop: 12 }}>Verified {dateTime(d.verifiedAtUtc)}. Rule decisions are recommendations; authorised staff make the final call.</p>
            </Card>
            <Card title="Details">
              <dl className="kv">
                <dt>Supplier risk</dt><dd><Badge value={d.supplierRiskLevel} kind="level" /></dd>
                <dt>Purchase order</dt><dd className="mono">{d.poNumber ?? '—'}</dd>
                <dt>Due date</dt><dd>{date(d.dueDate)}</dd>
                <dt>Pay to</dt><dd>{d.ibanMasked ? <span className="mono">{d.ibanMasked}</span> : <span className="muted">Not provided</span>}</dd>
                <dt>Declared total</dt><dd className="num">{money(d.total, d.currency)}</dd>
                <dt>Lines total</dt><dd className="num">{money(d.linesTotal, d.currency)}</dd>
                <dt>Submitted by</dt><dd>{d.createdBy}</dd>
                <dt>Reviewed</dt><dd>{d.reviewedBy ? <>{d.reviewedBy} · {dateTime(d.reviewedAtUtc)}{d.reviewNote && <div className="cell-sub">“{d.reviewNote}”</div>}</> : 'Not yet'}</dd>
                <dt>PDF</dt>
                <dd>
                  {d.pdfOriginalName ? <a href={`/api/invoices/${id}/pdf`} style={{ color: 'var(--cyan)' }}><FileText size={14} style={{ verticalAlign: -2 }} /> {d.pdfOriginalName}</a> : 'None'}
                  <div><button className="btn btn-sm" style={{ marginTop: 6 }} onClick={() => pdfInput.current?.click()} disabled={upload.isPending}><Upload size={14} /> {d.pdfOriginalName ? 'Replace' : 'Attach'} PDF</button></div>
                  <input ref={pdfInput} type="file" accept="application/pdf,.pdf" hidden onChange={(e) => e.target.files?.[0] && upload.mutate(e.target.files[0])} />
                </dd>
              </dl>
            </Card>
          </div>
          <div className="grid grid-2">
            <Card title="Line items">
              <div className="table-wrap">
                <table className="data">
                  <thead><tr><th>Description</th><th className="right">Qty</th><th className="right">Unit price</th><th className="right">PO price</th></tr></thead>
                  <tbody>{d.lines.map((l, i) => {
                    const po = d.purchaseOrder?.lines.find((p) => p.description.toLowerCase() === l.description.toLowerCase());
                    const over = po && l.unitPrice > po.unitPrice + 0.01;
                    return (
                      <tr key={i}>
                        <td>{l.description}</td><td className="right num">{l.quantity}</td>
                        <td className="right num" style={{ color: over ? 'var(--orange)' : undefined, fontWeight: over ? 650 : undefined }}>{money(l.unitPrice, d.currency)}</td>
                        <td className="right num muted">{po ? money(po.unitPrice, d.purchaseOrder!.currency) : '—'}</td>
                      </tr>
                    );
                  })}</tbody>
                </table>
              </div>
              {d.purchaseOrder && <p className="subtle" style={{ marginTop: 10 }}>Matched against {d.purchaseOrder.number} issued {date(d.purchaseOrder.issuedOn)} for {money(d.purchaseOrder.total, d.purchaseOrder.currency)}.</p>}
            </Card>
            <Card title={`Fraud alerts (${d.alerts.length})`}>
              {d.alerts.length === 0 ? <Empty title="No suspicious patterns detected" /> : d.alerts.map((a) => (
                <Link key={a.id} to={`/alerts/${a.id}`} className="list-item">
                  <Badge value={a.severity} kind="level" />
                  <div style={{ flex: 1 }}><div className="cell-title">#{a.number} {a.title}</div><div className="cell-sub">{humanize(a.code)}</div></div>
                  <Badge value={a.status} />
                </Link>
              ))}
            </Card>
          </div>
        </div>
      )}
      {review && d && (
        <Modal title={review === 'approve' ? `Approve ${d.number}` : `Reject ${d.number}`} onClose={() => { setReview(null); act.reset(); }}
          footer={<>
            <button className="btn" onClick={() => setReview(null)}>Cancel</button>
            <button className={`btn ${review === 'approve' ? 'btn-primary' : 'btn-danger'}`} onClick={() => act.mutate(review)} disabled={act.isPending || ((review === 'reject' || flagged) && !note.trim())}>
              {act.isPending ? 'Saving…' : review === 'approve' ? 'Approve invoice' : 'Reject invoice'}
            </button>
          </>}>
          {review === 'approve' && flagged && <div className="callout warn" style={{ marginBottom: 14 }}>This invoice was flagged by the rules. Explain why it can still be approved. The note goes into the audit log.</div>}
          <Field label={review === 'reject' ? 'Reason for rejection (required)' : flagged ? 'Justification (required)' : 'Note (optional)'}>
            {(fid) => <textarea id={fid} className="textarea" value={note} onChange={(e) => setNote(e.target.value)} maxLength={1000} />}
          </Field>
          {act.isError && <div className="callout danger" role="alert" style={{ marginTop: 12 }}>{errorText(act.error)}</div>}
        </Modal>
      )}
    </>
  );
}
