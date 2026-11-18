import { useMutation, useQuery, useQueryClient, keepPreviousData } from '@tanstack/react-query';
import { ScrollText, Users } from 'lucide-react';
import { api, qs } from '../api/client';
import type { Activity, Paged, User } from '../api/types';
import { useAuth } from '../auth';
import { PageHead } from '../components/Layout';
import { Badge, Card, DemoBadge, Empty, ErrorState, Pagination, SkeletonRows, errorText, useToast } from '../components/ui';
import { ACTION_LABELS, dateTime, relative } from '../lib/format';
import { useListState } from '../lib/hooks';
import { SearchBox } from './Suppliers';

export function UsersPage() {
  const { user: me } = useAuth();
  const qc = useQueryClient();
  const toast = useToast();
  const q = useQuery({ queryKey: ['users'], queryFn: () => api.get<User[]>('/api/users') });
  const update = useMutation({
    mutationFn: ({ id, ...body }: { id: string; role?: string; isActive?: boolean; unlock?: boolean }) => api.patch<User>(`/api/users/${id}`, body),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['users'] }); toast('success', 'User updated'); },
    onError: (e) => toast('error', errorText(e)),
  });

  return (
    <>
      <PageHead title="Users & roles" subtitle="Role changes and deactivation take effect immediately: affected sessions are signed out." />
      <Card>
        {q.isLoading ? <SkeletonRows rows={4} /> : q.isError ? <ErrorState error={q.error} onRetry={() => q.refetch()} /> : !q.data?.length ? <Empty title="No users" icon={<Users size={22} />} /> : (
          <div className="table-wrap">
            <table className="data">
              <thead><tr><th>User</th><th>Role</th><th>Status</th><th className="hide-sm">Last sign-in</th><th className="right">Actions</th></tr></thead>
              <tbody>
                {q.data.map((u) => {
                  const self = u.id === me?.id;
                  return (
                    <tr key={u.id}>
                      <td><div className="cell-title row" style={{ gap: 6 }}>{u.displayName}{u.isDemo && <DemoBadge />}{self && <span className="subtle">(you)</span>}</div><div className="cell-sub">{u.email}</div></td>
                      <td>
                        <select className="select" style={{ height: 32, width: 120 }} aria-label={`Role for ${u.displayName}`} value={u.role} disabled={self || update.isPending}
                          onChange={(e) => update.mutate({ id: u.id, role: e.target.value })}>
                          <option>Analyst</option><option>Admin</option>
                        </select>
                      </td>
                      <td className="row" style={{ gap: 6 }}>{u.isActive ? <Badge value="Approved" label="Active" /> : <Badge value="Dismissed" label="Deactivated" />}{u.isLockedOut && <Badge value="Critical" label="Locked" />}</td>
                      <td className="hide-sm subtle">{u.lastLoginAtUtc ? relative(u.lastLoginAtUtc) : 'Never'}</td>
                      <td className="right">
                        <div className="row" style={{ justifyContent: 'flex-end' }}>
                          {u.isLockedOut && <button className="btn btn-sm" onClick={() => update.mutate({ id: u.id, unlock: true })}>Unlock</button>}
                          {!self && <button className={`btn btn-sm ${u.isActive ? 'btn-danger' : ''}`} onClick={() => update.mutate({ id: u.id, isActive: !u.isActive })}>{u.isActive ? 'Deactivate' : 'Activate'}</button>}
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </>
  );
}

export function AuditPage() {
  const { state, set } = useListState({ q: '', action: '', page: '1' });
  const q = useQuery({
    queryKey: ['audit', state],
    queryFn: () => api.get<Paged<Activity>>(`/api/audit${qs({ ...state, pageSize: 25 })}`),
    placeholderData: keepPreviousData,
  });
  return (
    <>
      <PageHead title="Audit log" subtitle="Append-only record of sign-ins, approvals, bank-detail changes, alert decisions and other sensitive actions." />
      <Card>
        <div className="toolbar">
          <SearchBox value={state.q} onChange={(v) => set({ q: v })} placeholder="Search user or details" />
          <select className="select" aria-label="Action type" value={state.action} onChange={(e) => set({ action: e.target.value })}>
            <option value="">All actions</option><option value="auth.">Authentication</option><option value="invoice.">Invoices</option>
            <option value="supplier.">Suppliers</option><option value="alert.">Alerts</option><option value="user.">User admin</option><option value="assistant.">Assistant</option>
          </select>
        </div>
        {q.isLoading ? <SkeletonRows /> : q.isError ? <ErrorState error={q.error} /> : !q.data?.items.length ? <Empty title="No entries" icon={<ScrollText size={22} />} /> : (
          <>
            <div className="table-wrap">
              <table className="data">
                <thead><tr><th>When</th><th>Actor</th><th>Action</th><th className="hide-sm">Details</th></tr></thead>
                <tbody>{q.data.items.map((a, i) => (
                  <tr key={i}>
                    <td className="subtle" style={{ whiteSpace: 'nowrap' }}>{dateTime(a.atUtc)}</td>
                    <td>{a.actor}</td>
                    <td><span className="mono">{a.action}</span><div className="cell-sub">{ACTION_LABELS[a.action] ?? ''}</div></td>
                    <td className="hide-sm" style={{ maxWidth: 420 }}>{a.details}</td>
                  </tr>
                ))}</tbody>
              </table>
            </div>
            <Pagination page={q.data.page} totalPages={q.data.totalPages} total={q.data.total} label="entries" onPage={(p) => set({ page: String(p) }, false)} />
          </>
        )}
      </Card>
    </>
  );
}
