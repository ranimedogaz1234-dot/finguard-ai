import { useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Moon, Sun, Trash2 } from 'lucide-react';
import { api } from '../api/client';
import type { SystemInfo, User } from '../api/types';
import { useAuth, useTheme } from '../auth';
import { PageHead } from '../components/Layout';
import { Card, Field, errorText, useToast } from '../components/ui';
import { date, money } from '../lib/format';

export function SettingsPage() {
  const { user, setUser, isAdmin } = useAuth();
  const { theme, toggle } = useTheme();
  const toast = useToast();
  const qc = useQueryClient();
  const [name, setName] = useState(user?.displayName ?? '');
  const [pw, setPw] = useState({ current: '', next: '', confirm: '' });
  const info = useQuery({ queryKey: ['system-info'], queryFn: () => api.get<SystemInfo>('/api/system/info') });

  const profile = useMutation({
    mutationFn: () => api.put<User>('/api/account/profile', { displayName: name }),
    onSuccess: (u) => { setUser(u); toast('success', 'Profile updated'); },
    onError: (e) => toast('error', errorText(e)),
  });
  const password = useMutation({
    mutationFn: () => api.post('/api/account/change-password', { currentPassword: pw.current, newPassword: pw.next }),
    onSuccess: () => { setPw({ current: '', next: '', confirm: '' }); toast('success', 'Password changed. Other sessions were signed out.'); },
  });
  const purge = useMutation({
    mutationFn: () => api.post<{ suppliers: number; invoices: number; alerts: number }>('/api/system/demo/purge'),
    onSuccess: (r) => { qc.invalidateQueries(); toast('success', `Removed ${r.suppliers} demo suppliers, ${r.invoices} invoices and ${r.alerts} alerts`); },
    onError: (e) => toast('error', errorText(e)),
  });

  const pwMismatch = pw.confirm.length > 0 && pw.confirm !== pw.next;
  return (
    <>
      <PageHead title="Settings" subtitle="Your profile, security and display preferences." />
      <div className="grid grid-2" style={{ alignItems: 'start' }}>
        <div className="stack">
          <Card title="Profile">
            <form className="stack" style={{ gap: 14 }} onSubmit={(e: FormEvent) => { e.preventDefault(); profile.mutate(); }}>
              <Field label="Display name">{(id) => <input id={id} className="input" value={name} onChange={(e) => setName(e.target.value)} minLength={2} maxLength={100} />}</Field>
              <dl className="kv"><dt>Email</dt><dd>{user?.email}</dd><dt>Role</dt><dd>{user?.role}</dd><dt>Member since</dt><dd>{date(user?.createdAtUtc)}</dd></dl>
              <div><button className="btn btn-primary" disabled={profile.isPending || name.trim().length < 2 || name === user?.displayName}>Save profile</button></div>
            </form>
          </Card>
          <Card title="Appearance">
            <div className="row">
              <span className="muted">Theme</span><span className="spacer" />
              <button className="btn" onClick={toggle} aria-pressed={theme === 'light'}>{theme === 'dark' ? <><Sun size={16} /> Switch to light</> : <><Moon size={16} /> Switch to dark</>}</button>
            </div>
          </Card>
        </div>
        <div className="stack">
          <Card title="Change password">
            <form className="stack" style={{ gap: 14 }} onSubmit={(e: FormEvent) => { e.preventDefault(); if (!pwMismatch) password.mutate(); }}>
              <Field label="Current password">{(id) => <input id={id} className="input" type="password" autoComplete="current-password" value={pw.current} onChange={(e) => setPw({ ...pw, current: e.target.value })} />}</Field>
              <Field label="New password" hint="At least 12 characters.">{(id, d) => <input id={id} className="input" type="password" autoComplete="new-password" aria-describedby={d} value={pw.next} onChange={(e) => setPw({ ...pw, next: e.target.value })} />}</Field>
              <Field label="Confirm new password" error={pwMismatch ? 'Passwords do not match.' : undefined}>{(id, d) => <input id={id} className="input" type="password" autoComplete="new-password" aria-describedby={d} aria-invalid={pwMismatch} value={pw.confirm} onChange={(e) => setPw({ ...pw, confirm: e.target.value })} />}</Field>
              {password.isError && <div className="callout danger" role="alert">{errorText(password.error)}</div>}
              <div><button className="btn btn-primary" disabled={password.isPending || !pw.current || pw.next.length < 12 || pwMismatch}>Change password</button></div>
            </form>
          </Card>
          {info.data && (
            <Card title="Platform">
              <dl className="kv">
                <dt>Environment</dt><dd>{info.data.environment}</dd>
                <dt>Approval threshold</dt><dd>{money(info.data.approvalThreshold, info.data.baseCurrency)}: admin approval required</dd>
                <dt>Demo data</dt><dd>{info.data.demoData ? 'Loaded (fictional)' : 'Not present'}</dd>
              </dl>
              <p className="subtle" style={{ marginTop: 12 }}>API reference: <Link to="/scalar" reloadDocument style={{ color: 'var(--cyan)' }}>/scalar</Link> (development only).</p>
              {isAdmin && info.data.demoData && (
                <div className="callout danger" style={{ marginTop: 14 }}>
                  <div>
                    <strong>Remove demo data</strong>
                    <div className="muted" style={{ margin: '4px 0 10px' }}>Deletes every fictional supplier with its invoices, alerts and payments, so you can start with real data. Demo user accounts stay.</div>
                    <button className="btn btn-danger btn-sm" disabled={purge.isPending} onClick={() => confirm('Permanently delete all demo business data?') && purge.mutate()}><Trash2 size={14} /> Remove demo data</button>
                  </div>
                </div>
              )}
            </Card>
          )}
        </div>
      </div>
    </>
  );
}
