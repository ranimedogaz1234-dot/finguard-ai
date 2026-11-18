import { useEffect, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Archive, ArrowDownUp, Building2, GitCompare, Landmark, Pencil, Plus, RefreshCw, RotateCcw, Search } from 'lucide-react';
import { api, ApiError, qs } from '../api/client';
import type { BankChange, InvoiceListItem, Paged, RiskHistoryPoint, SupplierDetail, SupplierInput, SupplierListItem } from '../api/types';
import { useAuth } from '../auth';
import { RiskGauge, RiskHistoryChart } from '../components/charts';
import { PageHead } from '../components/Layout';
import { Badge, Card, DemoBadge, Empty, ErrorState, Field, Modal, Pagination, ScoreBar, Skeleton, SkeletonRows, errorText, useToast } from '../components/ui';
import { date, dateTime, money, relative } from '../lib/format';
import { useDebounced, useListState } from '../lib/hooks';

export function SearchBox({ value, onChange, placeholder }: { value: string; onChange: (v: string) => void; placeholder: string }) {
  const [v, setV] = useState(value);
  const d = useDebounced(v, 300);
  useEffect(() => { if (d !== value) onChange(d); }, [d]); // only react to debounced input
  useEffect(() => setV(value), [value]);
  return (
    <div className="search">
      <Search size={16} aria-hidden />
      <input className="input" type="search" placeholder={placeholder} aria-label={placeholder} value={v} onChange={(e) => setV(e.target.value)} />
    </div>
  );
}

export function SortHeader({ label, k, sort, dir, onSort, className }: { label: string; k: string; sort: string; dir: string; onSort: (k: string) => void; className?: string }) {
  const active = sort === k;
  return (
    <th className={className} aria-sort={active ? (dir === 'asc' ? 'ascending' : 'descending') : 'none'}>
      <button onClick={() => onSort(k)}>{label}<ArrowDownUp size={12} style={{ opacity: active ? 1 : 0.35 }} aria-hidden /></button>
    </th>
  );
}

export function SuppliersPage() {
  const { state, set, toggleSort } = useListState({ q: '', status: 'active', level: '', category: '', sort: 'risk', dir: 'desc', page: '1' });
  const [creating, setCreating] = useState(false);
  const [selected, setSelected] = useState<string[]>([]);
  const navigate = useNavigate();
  const list = useQuery({
    queryKey: ['suppliers', state],
    queryFn: () => api.get<Paged<SupplierListItem>>(`/api/suppliers${qs({ ...state, pageSize: 12 })}`),
    placeholderData: keepPreviousData,
  });
  const facets = useQuery({ queryKey: ['supplier-facets'], queryFn: () => api.get<{ categories: string[]; countries: string[] }>('/api/suppliers/facets') });
  const toggle = (id: string) => setSelected((s) => (s.includes(id) ? s.filter((x) => x !== id) : s.length >= 4 ? s : [...s, id]));

  return (
    <>
      <PageHead
        title="Supplier risk intelligence"
        subtitle="Explainable 0–100 risk scores computed from verification results, alerts and bank-detail history."
        actions={<>
          <button className="btn" disabled={selected.length < 2} onClick={() => navigate(`/suppliers/compare${qs({ ids: selected })}`)}><GitCompare size={16} /> Compare ({selected.length})</button>
          <button className="btn btn-primary" onClick={() => setCreating(true)}><Plus size={16} /> Add supplier</button>
        </>}
      />
      <Card>
        <div className="toolbar">
          <SearchBox value={state.q} onChange={(q) => set({ q })} placeholder="Search name, category, tax ID" />
          <select className="select" aria-label="Risk level" value={state.level} onChange={(e) => set({ level: e.target.value })}>
            <option value="">All risk levels</option>{['Low', 'Medium', 'High', 'Critical'].map((l) => <option key={l}>{l}</option>)}
          </select>
          <select className="select" aria-label="Category" value={state.category} onChange={(e) => set({ category: e.target.value })}>
            <option value="">All categories</option>{facets.data?.categories.map((c) => <option key={c}>{c}</option>)}
          </select>
          <select className="select" aria-label="Status" value={state.status} onChange={(e) => set({ status: e.target.value })}>
            <option value="active">Active</option><option value="archived">Archived</option><option value="all">All</option>
          </select>
        </div>
        {list.isLoading ? <SkeletonRows /> : list.isError ? <ErrorState error={list.error} onRetry={() => list.refetch()} /> : list.data!.items.length === 0 ? (
          <Empty title="No suppliers match" icon={<Building2 size={22} />}>Try clearing the filters or add a new supplier.</Empty>
        ) : (
          <>
            <div className="table-wrap" style={{ opacity: list.isFetching ? 0.65 : 1, transition: 'opacity .2s' }}>
              <table className="data">
                <thead>
                  <tr>
                    <th style={{ width: 36 }}><span className="sr-only">Select to compare</span></th>
                    <SortHeader label="Supplier" k="name" {...state} onSort={toggleSort} />
                    <SortHeader label="Country" k="country" {...state} onSort={toggleSort} className="hide-sm" />
                    <SortHeader label="Risk" k="risk" {...state} onSort={toggleSort} />
                    <th>Level</th>
                    <th className="right hide-sm">Invoices</th>
                    <th className="right hide-sm">Open alerts</th>
                    <SortHeader label="Annual spend" k="spend" {...state} onSort={toggleSort} className="right hide-xs" />
                  </tr>
                </thead>
                <tbody>
                  {list.data!.items.map((s) => (
                    <tr key={s.id} className="clickable" onClick={() => navigate(`/suppliers/${s.id}`)}>
                      <td onClick={(e) => e.stopPropagation()}>
                        <input type="checkbox" aria-label={`Compare ${s.name}`} checked={selected.includes(s.id)} onChange={() => toggle(s.id)} />
                      </td>
                      <td>
                        <Link to={`/suppliers/${s.id}`} className="cell-title" onClick={(e) => e.stopPropagation()}>{s.name}</Link>
                        <div className="cell-sub row" style={{ gap: 6 }}>{s.category}{s.isDemo && <DemoBadge />}{s.status === 'Archived' && <Badge value="Archived" />}</div>
                      </td>
                      <td className="hide-sm">{s.country}</td>
                      <td><ScoreBar score={s.riskScore} /></td>
                      <td><Badge value={s.riskLevel} kind="level" /></td>
                      <td className="right num hide-sm">{s.invoiceCount}</td>
                      <td className="right num hide-sm">{s.openAlerts > 0 ? <span style={{ color: 'var(--orange)', fontWeight: 650 }}>{s.openAlerts}</span> : 0}</td>
                      <td className="right num hide-xs">{money(s.annualSpend, s.currency, true)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={list.data!.page} totalPages={list.data!.totalPages} total={list.data!.total} label="suppliers" onPage={(p) => set({ page: String(p) }, false)} />
          </>
        )}
      </Card>
      {creating && <SupplierFormModal onClose={() => setCreating(false)} onSaved={(id) => navigate(`/suppliers/${id}`)} />}
    </>
  );
}

const EMPTY: SupplierInput = { name: '', country: '', category: '', taxId: null, contactEmail: null, contactPhone: null, address: null, iban: null, currency: 'EUR', annualSpend: 0, paymentTermsDays: 30, notes: null };

export function SupplierFormModal({ supplier, onClose, onSaved }: { supplier?: SupplierDetail; onClose: () => void; onSaved?: (id: string) => void }) {
  const qc = useQueryClient();
  const toast = useToast();
  const [form, setForm] = useState<SupplierInput>(supplier ? {
    name: supplier.name, country: supplier.country, category: supplier.category, taxId: supplier.taxId, contactEmail: supplier.contactEmail,
    contactPhone: supplier.contactPhone, address: supplier.address, iban: null, currency: supplier.currency, annualSpend: supplier.annualSpend,
    paymentTermsDays: supplier.paymentTermsDays, notes: supplier.notes,
  } : EMPTY);
  const [keepIban, setKeepIban] = useState(!!supplier);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [error, setError] = useState<string | null>(null);

  const save = useMutation({
    mutationFn: async () => {
      if (supplier) {
        await api.put(`/api/suppliers/${supplier.id}`, { ...form, iban: keepIban ? null : form.iban, keepExistingIban: keepIban });
        return supplier.id;
      }
      return (await api.post<{ id: string }>('/api/suppliers', { ...form })).id;
    },
    onSuccess: (id) => {
      toast('success', supplier ? 'Supplier updated' : 'Supplier created and risk assessed');
      qc.invalidateQueries({ queryKey: ['suppliers'] });
      qc.invalidateQueries({ queryKey: ['supplier', id] });
      qc.invalidateQueries({ queryKey: ['dashboard'] });
      onSaved?.(id);
      onClose();
    },
    onError: (e) => { if (e instanceof ApiError && e.fieldErrors) setErrors(e.fieldErrors); setError(errorText(e)); },
  });
  const fe = (k: string) => errors[k.charAt(0).toUpperCase() + k.slice(1)]?.[0] ?? errors[k]?.[0];
  const set = <K extends keyof SupplierInput>(k: K, v: SupplierInput[K]) => setForm((f) => ({ ...f, [k]: v }));
  const submit = (e: FormEvent) => { e.preventDefault(); setErrors({}); setError(null); save.mutate(); };

  return (
    <Modal title={supplier ? `Edit ${supplier.name}` : 'Add supplier'} onClose={onClose}
      footer={<><button className="btn" onClick={onClose}>Cancel</button><button className="btn btn-primary" form="supplier-form" disabled={save.isPending}>{save.isPending ? 'Saving…' : 'Save supplier'}</button></>}>
      <form id="supplier-form" className="form-grid" onSubmit={submit} noValidate>
        <Field label="Legal name" className="full" error={fe('name')}>{(id) => <input id={id} className="input" value={form.name} onChange={(e) => set('name', e.target.value)} required maxLength={200} />}</Field>
        <Field label="Country (ISO code)" error={fe('country')}>{(id) => <input id={id} className="input" value={form.country} onChange={(e) => set('country', e.target.value.toUpperCase().slice(0, 2))} placeholder="FR" required />}</Field>
        <Field label="Category" error={fe('category')}>{(id) => <input id={id} className="input" value={form.category} onChange={(e) => set('category', e.target.value)} placeholder="Consulting" required />}</Field>
        <Field label="Tax ID / VAT">{(id) => <input id={id} className="input" value={form.taxId ?? ''} onChange={(e) => set('taxId', e.target.value || null)} />}</Field>
        <Field label="Currency" error={fe('currency')}>{(id) => <select id={id} className="select" value={form.currency} onChange={(e) => set('currency', e.target.value)}>{['EUR', 'USD', 'GBP', 'TND', 'CHF'].map((c) => <option key={c}>{c}</option>)}</select>}</Field>
        <Field label="Accounts email" error={fe('contactEmail')}>{(id) => <input id={id} className="input" type="email" value={form.contactEmail ?? ''} onChange={(e) => set('contactEmail', e.target.value || null)} />}</Field>
        <Field label="Phone">{(id) => <input id={id} className="input" value={form.contactPhone ?? ''} onChange={(e) => set('contactPhone', e.target.value || null)} />}</Field>
        <div className="full">
          {supplier && (
            <label className="row" style={{ gap: 8, marginBottom: 8, fontSize: 13 }}>
              <input type="checkbox" checked={keepIban} onChange={(e) => setKeepIban(e.target.checked)} /> Keep current bank account ({supplier.ibanMasked ?? 'none'})
            </label>
          )}
          {!keepIban && (
            <Field label="IBAN" hint={supplier ? 'Changing bank details is logged, raises the risk score and triggers alerts on the next invoices.' : 'Validated with the ISO 13616 checksum.'} error={fe('iban')}>
              {(id, d) => <input id={id} className="input mono" value={form.iban ?? ''} onChange={(e) => set('iban', e.target.value || null)} aria-describedby={d} placeholder="FR76 3000 6000 0112 3456 7890 189" />}
            </Field>
          )}
        </div>
        <Field label="Annual spend" error={fe('annualSpend')}>{(id) => <input id={id} className="input" type="number" min={0} value={form.annualSpend} onChange={(e) => set('annualSpend', Number(e.target.value))} />}</Field>
        <Field label="Payment terms (days)" error={fe('paymentTermsDays')}>{(id) => <input id={id} className="input" type="number" min={0} max={365} value={form.paymentTermsDays} onChange={(e) => set('paymentTermsDays', Number(e.target.value))} />}</Field>
        <Field label="Address" className="full">{(id) => <input id={id} className="input" value={form.address ?? ''} onChange={(e) => set('address', e.target.value || null)} />}</Field>
        <Field label="Notes" className="full">{(id) => <textarea id={id} className="textarea" value={form.notes ?? ''} onChange={(e) => set('notes', e.target.value || null)} maxLength={2000} />}</Field>
        {error && <div className="callout danger full" role="alert">{error}</div>}
      </form>
    </Modal>
  );
}

export function SupplierDetailPage() {
  const { id = '' } = useParams();
  const { isAdmin } = useAuth();
  const qc = useQueryClient();
  const toast = useToast();
  const [editing, setEditing] = useState(false);
  const s = useQuery({ queryKey: ['supplier', id], queryFn: () => api.get<SupplierDetail>(`/api/suppliers/${id}`) });
  const history = useQuery({ queryKey: ['supplier', id, 'history'], queryFn: () => api.get<RiskHistoryPoint[]>(`/api/suppliers/${id}/risk-history`) });
  const banks = useQuery({ queryKey: ['supplier', id, 'banks'], queryFn: () => api.get<BankChange[]>(`/api/suppliers/${id}/bank-changes`) });
  const invoices = useQuery({ queryKey: ['invoices', { supplierId: id }], queryFn: () => api.get<Paged<InvoiceListItem>>(`/api/invoices${qs({ supplierId: id, pageSize: 8 })}`) });
  const refresh = () => qc.invalidateQueries({ queryKey: ['supplier', id] });

  const reassess = useMutation({ mutationFn: () => api.post(`/api/suppliers/${id}/reassess`), onSuccess: () => { toast('success', 'Risk score recomputed from current data'); refresh(); }, onError: (e) => toast('error', errorText(e)) });
  const archive = useMutation({
    mutationFn: (archived: boolean) => api.post(`/api/suppliers/${id}/${archived ? 'archive' : 'restore'}`),
    onSuccess: () => { toast('success', 'Supplier status updated'); refresh(); qc.invalidateQueries({ queryKey: ['suppliers'] }); },
    onError: (e) => toast('error', errorText(e)),
  });

  if (s.isError) return <Card><ErrorState error={s.error} onRetry={() => s.refetch()} /></Card>;
  const d = s.data;
  const risk = d?.risk;

  return (
    <>
      <PageHead
        crumbs={[{ to: '/suppliers', label: 'Suppliers' }]}
        title={d ? <span className="row" style={{ gap: 10 }}>{d.name}{d.isDemo && <DemoBadge />}{d.status === 'Archived' && <Badge value="Archived" />}</span> : <Skeleton h={28} w={260} />}
        subtitle={d ? `${d.category} · ${d.country} · onboarded ${date(d.onboardedOn)}` : undefined}
        actions={d && <>
          <button className="btn" onClick={() => reassess.mutate()} disabled={reassess.isPending}><RefreshCw size={16} /> Reassess</button>
          {d.status === 'Active' && <button className="btn" onClick={() => setEditing(true)}><Pencil size={16} /> Edit</button>}
          {isAdmin && (d.status === 'Active'
            ? <button className="btn btn-danger" onClick={() => confirm(`Archive ${d.name}? New invoices will be blocked.`) && archive.mutate(true)}><Archive size={16} /> Archive</button>
            : <button className="btn" onClick={() => archive.mutate(false)}><RotateCcw size={16} /> Restore</button>)}
        </>}
      />
      {!d ? <Skeleton h={400} /> : (
        <div className="stack">
          <div className="grid grid-3">
            <Card title="Risk assessment" actions={risk && <span className="subtle">Confidence: {risk.confidence} · {relative(risk.assessedAtUtc)}</span>}>
              {!risk ? <Empty title="Not assessed yet" /> : (
                <div className="grid grid-2" style={{ alignItems: 'start' }}>
                  <div style={{ display: 'grid', justifyItems: 'center', gap: 12 }}>
                    <RiskGauge score={risk.score} level={risk.level} />
                    <Badge value={risk.level} kind="level" label={`${risk.level} risk`} />
                    <div className={`callout ${risk.level === 'Low' ? 'ok' : risk.level === 'Medium' ? 'warn' : 'danger'}`} style={{ width: '100%' }}>
                      <div><strong>Recommendation</strong><div>{risk.recommendation}</div></div>
                    </div>
                  </div>
                  <div>
                    <div className="subtle" style={{ marginBottom: 4 }}>Contributing factors. The score is the sum of the points.</div>
                    {risk.factors.map((f) => (
                      <div key={f.key} className="factor">
                        <div><div style={{ fontWeight: 600, fontSize: 13 }}>{f.label}</div><div className="cell-sub">{f.detail}</div></div>
                        <div className="num" style={{ fontWeight: 700, color: f.points > 0 ? 'var(--text)' : 'var(--text-3)' }}>+{f.points}<span className="subtle">/{f.maxPoints}</span></div>
                        <div className="bar"><span style={{ width: `${(f.points / f.maxPoints) * 100}%` }} /></div>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </Card>
            <Card title="Profile">
              <dl className="kv">
                <dt>Tax ID</dt><dd className="mono">{d.taxId ?? '—'}</dd>
                <dt>Bank account</dt><dd><span className="mono">{d.ibanMasked ?? '—'}</span> {d.ibanMasked && (d.ibanValid ? <Badge value="Approved" label="Valid" /> : <Badge value="Critical" label="Invalid" />)}</dd>
                <dt>Bank changed</dt><dd>{d.ibanChangedAtUtc ? dateTime(d.ibanChangedAtUtc) : 'Never'}</dd>
                <dt>Email</dt><dd>{d.contactEmail ?? '—'}</dd>
                <dt>Phone</dt><dd>{d.contactPhone ?? '—'}</dd>
                <dt>Payment terms</dt><dd>{d.paymentTermsDays} days</dd>
                <dt>Annual spend</dt><dd className="num">{money(d.annualSpend, d.currency)}</dd>
                <dt>Address</dt><dd>{d.address ?? '—'}</dd>
              </dl>
              {d.notes && <p className="muted" style={{ marginTop: 14 }}>{d.notes}</p>}
            </Card>
          </div>
          <div className="grid grid-kpi">
            {[
              ['Invoices', String(d.financials.invoiceCount)],
              ['Total invoiced', money(d.financials.totalInvoiced, d.currency, true)],
              ['Awaiting payment', money(d.financials.pendingAmount, d.currency, true)],
              ['Flagged invoices', String(d.financials.flaggedInvoices)],
              ['Open alerts', String(d.financials.openAlerts)],
            ].map(([k, v]) => <div key={k} className="card kpi"><div className="kpi-top">{k}</div><div className="kpi-value" style={{ fontSize: 22 }}>{v}</div></div>)}
          </div>
          <div className="grid grid-2">
            <Card title="Risk score history">
              {history.isLoading ? <Skeleton h={230} /> : (history.data?.length ?? 0) < 2 ? <Empty title="Not enough history yet">Each assessment (monthly review, bank change, rejected invoice) adds a point.</Empty> : <RiskHistoryChart data={history.data!} />}
            </Card>
            <Card title="Bank-detail changes">
              {banks.isLoading ? <Skeleton h={120} /> : !banks.data?.length ? <Empty title="No changes recorded" icon={<Landmark size={22} />} /> : (
                <ul className="timeline">{banks.data.map((b, i) => (
                  <li key={i}><div style={{ fontSize: 13 }}><span className="mono">{b.oldIbanMasked ?? 'none'}</span> → <span className="mono">{b.newIbanMasked ?? 'none'}</span></div><div className="cell-sub">{dateTime(b.changedAtUtc)}</div></li>
                ))}</ul>
              )}
            </Card>
          </div>
          <Card title="Recent invoices" actions={<Link className="btn btn-sm" to={`/invoices?supplierId=${id}`}>View all</Link>}>
            {invoices.isLoading ? <SkeletonRows rows={3} /> : !invoices.data?.items.length ? <Empty title="No invoices yet" /> : <InvoiceMiniTable items={invoices.data.items} />}
          </Card>
        </div>
      )}
      {editing && d && <SupplierFormModal supplier={d} onClose={() => setEditing(false)} />}
    </>
  );
}

export function InvoiceMiniTable({ items }: { items: InvoiceListItem[] }) {
  return (
    <div className="table-wrap">
      <table className="data">
        <thead><tr><th>Invoice</th><th className="hide-sm">Date</th><th className="right">Amount</th><th>Rules</th><th>Status</th></tr></thead>
        <tbody>
          {items.map((i) => (
            <tr key={i.id}>
              <td><Link to={`/invoices/${i.id}`} className="cell-title">{i.number}</Link></td>
              <td className="hide-sm">{date(i.invoiceDate)}</td>
              <td className="right num">{money(i.total, i.currency)}</td>
              <td><Badge value={i.decision} /></td>
              <td><Badge value={i.status} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function SupplierComparePage() {
  const [params] = useSearchParams();
  const ids = params.getAll('ids');
  const q = useQuery({ queryKey: ['compare', ids], queryFn: () => api.get<SupplierDetail[]>(`/api/suppliers/compare${qs({ ids })}`), enabled: ids.length >= 2 });
  const factorKeys = q.data?.[0]?.risk?.factors.map((f) => ({ key: f.key, label: f.label })) ?? [];
  return (
    <>
      <PageHead crumbs={[{ to: '/suppliers', label: 'Suppliers' }]} title="Supplier comparison" subtitle="Side-by-side risk factors and financials." />
      {ids.length < 2 ? <Card><Empty title="Select 2 to 4 suppliers">Tick the checkboxes in the supplier list, then choose Compare.</Empty></Card>
        : q.isLoading ? <Skeleton h={400} /> : q.isError ? <Card><ErrorState error={q.error} /></Card> : (
          <Card>
            <div className="table-wrap">
              <table className="data">
                <thead><tr><th>Metric</th>{q.data!.map((s) => <th key={s.id}><Link to={`/suppliers/${s.id}`} style={{ textTransform: 'none', color: 'var(--text)' }}>{s.name}</Link></th>)}</tr></thead>
                <tbody>
                  <tr><td className="muted">Risk score</td>{q.data!.map((s) => <td key={s.id}><ScoreBar score={s.risk?.score ?? 0} /></td>)}</tr>
                  <tr><td className="muted">Level</td>{q.data!.map((s) => <td key={s.id}>{s.risk && <Badge value={s.risk.level} kind="level" />}</td>)}</tr>
                  <tr><td className="muted">Confidence</td>{q.data!.map((s) => <td key={s.id}>{s.risk?.confidence}</td>)}</tr>
                  {factorKeys.map((f) => (
                    <tr key={f.key}><td className="muted">{f.label}</td>{q.data!.map((s) => {
                      const v = s.risk?.factors.find((x) => x.key === f.key);
                      return <td key={s.id} className="num">{v ? <span style={{ fontWeight: v.points > 0 ? 700 : 400, color: v.points > 0 ? 'var(--text)' : 'var(--text-3)' }}>+{v.points}</span> : '—'}</td>;
                    })}</tr>
                  ))}
                  <tr><td className="muted">Invoices (flagged)</td>{q.data!.map((s) => <td key={s.id} className="num">{s.financials.invoiceCount} ({s.financials.flaggedInvoices})</td>)}</tr>
                  <tr><td className="muted">Total invoiced</td>{q.data!.map((s) => <td key={s.id} className="num">{money(s.financials.totalInvoiced, s.currency)}</td>)}</tr>
                  <tr><td className="muted">Open alerts</td>{q.data!.map((s) => <td key={s.id} className="num">{s.financials.openAlerts}</td>)}</tr>
                  <tr><td className="muted">Bank changed</td>{q.data!.map((s) => <td key={s.id}>{s.ibanChangedAtUtc ? relative(s.ibanChangedAtUtc) : 'Never'}</td>)}</tr>
                  <tr><td className="muted">Recommendation</td>{q.data!.map((s) => <td key={s.id} style={{ fontSize: 12.5 }}>{s.risk?.recommendation}</td>)}</tr>
                </tbody>
              </table>
            </div>
          </Card>
        )}
    </>
  );
}
