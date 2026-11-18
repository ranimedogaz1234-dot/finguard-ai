import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { MessageSquarePlus, ShieldAlert, ShieldCheck, UserCheck } from 'lucide-react';
import { api, qs } from '../api/client';
import type { AlertDetail, AlertListItem, AlertStatus, Paged } from '../api/types';
import { useAuth } from '../auth';
import { PageHead } from '../components/Layout';
import { Badge, Card, DemoBadge, Empty, ErrorState, Field, Modal, Pagination, Skeleton, SkeletonRows, errorText, useToast } from '../components/ui';
import { dateTime, humanize, money, relative } from '../lib/format';
import { useListState } from '../lib/hooks';
import { SearchBox, SortHeader } from './Suppliers';

const STATUS_TABS = [['open', 'Open'], ['closed', 'Closed'], ['all', 'All']] as const;

export function AlertsPage() {
  const { state, set, toggleSort } = useListState({ q: '', status: 'open', severity: '', mine: '', sort: 'severity', dir: 'desc', page: '1' });
  const navigate = useNavigate();
  const list = useQuery({
    queryKey: ['alerts', state],
    queryFn: () => api.get<Paged<AlertListItem>>(`/api/alerts${qs({ ...state, pageSize: 15 })}`),
    placeholderData: keepPreviousData,
  });

  return (
    <>
      <PageHead title="Fraud detection & alerts" subtitle="Rule-based detections of suspicious activity. An alert is not confirmed fraud until an administrator confirms it." />
      <Card>
        <div className="toolbar">
          <div className="tabs" role="tablist" aria-label="Alert status">
            {STATUS_TABS.map(([v, l]) => <button key={v} role="tab" className="tab" aria-selected={state.status === v} onClick={() => set({ status: v })}>{l}</button>)}
          </div>
          <SearchBox value={state.q} onChange={(q) => set({ q })} placeholder="Search #, title, supplier, invoice" />
          <select className="select" aria-label="Severity" value={state.severity} onChange={(e) => set({ severity: e.target.value })}>
            <option value="">All severities</option>{['Critical', 'High', 'Medium', 'Low'].map((s) => <option key={s}>{s}</option>)}
          </select>
          <label className="row" style={{ gap: 6, fontSize: 13 }}><input type="checkbox" checked={state.mine === 'true'} onChange={(e) => set({ mine: e.target.checked ? 'true' : '' })} /> Assigned to me</label>
        </div>
        {list.isLoading ? <SkeletonRows /> : list.isError ? <ErrorState error={list.error} onRetry={() => list.refetch()} /> : list.data!.items.length === 0 ? (
          <Empty title="No alerts here" icon={<ShieldCheck size={22} />}>{state.status === 'open' ? 'There is nothing open to investigate. Good news.' : 'No alerts match these filters.'}</Empty>
        ) : (
          <>
            <div className="table-wrap" style={{ opacity: list.isFetching ? 0.65 : 1 }}>
              <table className="data">
                <thead>
                  <tr>
                    <SortHeader label="Alert" k="number" {...state} onSort={toggleSort} />
                    <SortHeader label="Severity" k="severity" {...state} onSort={toggleSort} />
                    <th className="hide-sm">Supplier</th>
                    <th>Status</th>
                    <th className="hide-sm">Assignee</th>
                    <SortHeader label="Exposure" k="exposure" {...state} onSort={toggleSort} className="right hide-xs" />
                    <SortHeader label="Raised" k="created" {...state} onSort={toggleSort} className="hide-sm" />
                  </tr>
                </thead>
                <tbody>
                  {list.data!.items.map((a) => (
                    <tr key={a.id} className="clickable" onClick={() => navigate(`/alerts/${a.id}`)}>
                      <td>
                        <Link to={`/alerts/${a.id}`} className="cell-title" onClick={(e) => e.stopPropagation()}>#{a.number} {a.title}</Link>
                        <div className="cell-sub row" style={{ gap: 6 }}>{a.invoiceNumber ? `Invoice ${a.invoiceNumber}` : humanize(a.code)}{a.isDemo && <DemoBadge />}</div>
                      </td>
                      <td><Badge value={a.severity} kind="level" /></td>
                      <td className="hide-sm">{a.supplierName}</td>
                      <td><Badge value={a.status} /></td>
                      <td className="hide-sm">{a.assignedTo ?? <span className="subtle">Unassigned</span>}</td>
                      <td className="right num hide-xs">{money(a.exposure)}</td>
                      <td className="hide-sm subtle">{relative(a.createdAtUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={list.data!.page} totalPages={list.data!.totalPages} total={list.data!.total} label="alerts" onPage={(p) => set({ page: String(p) }, false)} />
          </>
        )}
      </Card>
    </>
  );
}

const TRANSITION_LABEL: Partial<Record<AlertStatus, string>> = {
  Investigating: 'Start investigation', Escalated: 'Escalate', Dismissed: 'Dismiss as false positive',
  Resolved: 'Resolve', ConfirmedFraud: 'Confirm fraud',
};
const NEEDS_NOTE: AlertStatus[] = ['Dismissed', 'Resolved', 'ConfirmedFraud'];

export function AlertDetailPage() {
  const { id = '' } = useParams();
  const { user, isAdmin } = useAuth();
  const qc = useQueryClient();
  const toast = useToast();
  const [target, setTarget] = useState<AlertStatus | null>(null);
  const [note, setNote] = useState('');
  const [comment, setComment] = useState('');
  const q = useQuery({ queryKey: ['alert', id], queryFn: () => api.get<AlertDetail>(`/api/alerts/${id}`) });
  const users = useQuery({ queryKey: ['assignable'], queryFn: () => api.get<{ id: string; displayName: string; role: string }[]>('/api/users/assignable'), enabled: isAdmin });

  const done = (msg: string) => (d: AlertDetail) => {
    qc.setQueryData(['alert', id], d);
    qc.invalidateQueries({ queryKey: ['alerts'] });
    qc.invalidateQueries({ queryKey: ['dashboard'] });
    qc.invalidateQueries({ queryKey: ['priorities'] });
    toast('success', msg);
  };
  const status = useMutation({
    mutationFn: (s: AlertStatus) => api.post<AlertDetail>(`/api/alerts/${id}/status`, { status: s, note: note || null }),
    onSuccess: (d, s) => { done(`Alert moved to ${humanize(s)}`)(d); setTarget(null); setNote(''); },
  });
  const assign = useMutation({ mutationFn: (userId: string | null) => api.post<AlertDetail>(`/api/alerts/${id}/assign`, { userId }), onSuccess: done('Assignment updated'), onError: (e) => toast('error', errorText(e)) });
  const addNote = useMutation({ mutationFn: () => api.post<AlertDetail>(`/api/alerts/${id}/notes`, { note: comment }), onSuccess: (d) => { done('Note added')(d); setComment(''); }, onError: (e) => toast('error', errorText(e)) });

  if (q.isError) return <Card><ErrorState error={q.error} onRetry={() => q.refetch()} /></Card>;
  const d = q.data;
  const a = d?.alert;

  return (
    <>
      <PageHead
        crumbs={[{ to: '/alerts', label: 'Alerts' }]}
        title={a ? <span className="row" style={{ gap: 10 }}>#{a.number} {a.title}{a.isDemo && <DemoBadge />}</span> : <Skeleton h={28} w={300} />}
        subtitle={a && <>{humanize(a.code)} · raised {dateTime(a.createdAtUtc)} · <Link to={`/suppliers/${a.supplierId}`} style={{ color: 'var(--cyan)' }}>{a.supplierName}</Link>{a.invoiceId && <> · <Link to={`/invoices/${a.invoiceId}`} style={{ color: 'var(--cyan)' }}>Invoice {a.invoiceNumber}</Link></>}</>}
        actions={d?.canEdit && d.allowedTransitions.map((t) => (
          <button key={t} className={`btn ${t === 'ConfirmedFraud' ? 'btn-danger' : t === 'Investigating' ? 'btn-primary' : ''}`} onClick={() => NEEDS_NOTE.includes(t) ? setTarget(t) : status.mutate(t)} disabled={status.isPending}>
            {TRANSITION_LABEL[t] ?? humanize(t)}
          </button>
        ))}
      />
      {!d || !a ? <Skeleton h={400} /> : (
        <div className="stack">
          {status.isError && !target && <div className="callout danger" role="alert">{errorText(status.error)}</div>}
          <div className={`callout ${a.status === 'ConfirmedFraud' ? 'danger' : a.status === 'Dismissed' || a.status === 'Resolved' ? 'ok' : 'warn'}`}>
            <ShieldAlert size={18} />
            <span>{a.status === 'ConfirmedFraud' ? <><strong>Confirmed fraud.</strong> An administrator confirmed this case after investigation.</>
              : a.status === 'Dismissed' ? <><strong>Dismissed as a false positive.</strong> {d.resolutionNote}</>
              : a.status === 'Resolved' ? <><strong>Resolved.</strong> {d.resolutionNote}</>
              : <><strong>Suspicious, not confirmed.</strong> This alert was raised automatically by a detection rule. Investigate before drawing conclusions.</>}</span>
          </div>
          {!d.canEdit && <div className="callout info">This alert is assigned to {a.assignedTo}. Only they or an administrator can change it.</div>}
          <div className="grid grid-3">
            <Card title="Why this was flagged" actions={<><Badge value={a.severity} kind="level" label={`${a.severity} severity`} /><Badge value={a.status} /></>}>
              <p style={{ marginTop: 0, fontSize: 14.5 }}>{d.explanation}</p>
              {Object.keys(d.evidence).length > 0 && (
                <>
                  <h3 style={{ fontSize: 13, margin: '18px 0 8px' }} className="muted">Evidence</h3>
                  <dl className="kv">{Object.entries(d.evidence).map(([k, v]) => <div key={k} style={{ display: 'contents' }}><dt>{humanize(k.replace(/([A-Z])/g, '_$1'))}</dt><dd className="mono">{v}</dd></div>)}</dl>
                </>
              )}
              <dl className="kv" style={{ marginTop: 18 }}>
                <dt>Exposure</dt><dd className="num">{money(a.exposure)}</dd>
                <dt>Rule</dt><dd className="mono">{a.code}</dd>
              </dl>
            </Card>
            <Card title="Assignment">
              <div className="row" style={{ marginBottom: 12 }}><UserCheck size={18} className="muted" /><span>{a.assignedTo ?? 'Unassigned'}</span></div>
              {isAdmin ? (
                <select className="select" aria-label="Assign to" value={d.assignedToId ?? ''} onChange={(e) => assign.mutate(e.target.value || null)} disabled={assign.isPending}>
                  <option value="">Unassigned</option>
                  {users.data?.map((u) => <option key={u.id} value={u.id}>{u.displayName} ({u.role})</option>)}
                </select>
              ) : d.canEdit && (
                d.assignedToId === user?.id
                  ? <button className="btn btn-sm" onClick={() => assign.mutate(null)}>Unassign me</button>
                  : <button className="btn btn-sm" onClick={() => assign.mutate(user!.id)}>Assign to me</button>
              )}
            </Card>
          </div>
          <Card title="Investigation timeline">
            <ul className="timeline">
              {d.events.map((e, i) => (
                <li key={i}>
                  <div style={{ fontSize: 13.5 }}>
                    <strong>{e.actorName}</strong>{' '}
                    {e.kind === 'status' ? <>moved it from <Badge value={e.fromStatus ?? ''} /> to <Badge value={e.toStatus ?? ''} /></> : e.kind === 'created' ? 'raised the alert' : e.kind === 'assigned' ? 'changed the assignment' : 'added a note'}
                  </div>
                  {e.note && <div className="muted" style={{ fontSize: 13 }}>{e.note}</div>}
                  <div className="cell-sub">{dateTime(e.atUtc)}</div>
                </li>
              ))}
            </ul>
            {d.canEdit && (
              <form className="row" style={{ marginTop: 8, flexWrap: 'nowrap' }} onSubmit={(e) => { e.preventDefault(); if (comment.trim()) addNote.mutate(); }}>
                <input className="input" placeholder="Add an investigation note…" aria-label="Investigation note" value={comment} onChange={(e) => setComment(e.target.value)} maxLength={1000} />
                <button className="btn" disabled={!comment.trim() || addNote.isPending}><MessageSquarePlus size={16} /> Add</button>
              </form>
            )}
          </Card>
        </div>
      )}
      {target && (
        <Modal title={TRANSITION_LABEL[target] ?? humanize(target)} onClose={() => { setTarget(null); status.reset(); }}
          footer={<><button className="btn" onClick={() => setTarget(null)}>Cancel</button>
            <button className={`btn ${target === 'ConfirmedFraud' ? 'btn-danger' : 'btn-primary'}`} disabled={!note.trim() || status.isPending} onClick={() => status.mutate(target)}>{status.isPending ? 'Saving…' : 'Confirm'}</button></>}>
          {target === 'ConfirmedFraud' && <div className="callout danger" style={{ marginBottom: 14 }}>Confirming fraud is a formal finding. It raises the supplier's risk to at least High and is recorded in the audit log.</div>}
          <Field label="Outcome note (required)">{(fid) => <textarea id={fid} className="textarea" value={note} onChange={(e) => setNote(e.target.value)} maxLength={1000} placeholder="What did the investigation find?" />}</Field>
          {status.isError && <div className="callout danger" role="alert" style={{ marginTop: 12 }}>{errorText(status.error)}</div>}
        </Modal>
      )}
    </>
  );
}
