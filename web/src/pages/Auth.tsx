import { useState, type ChangeEvent, type FormEvent, type ReactNode } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { BadgeCheck, Fingerprint, LineChart, Lock, ScanSearch } from 'lucide-react';
import { api, ApiError, qs } from '../api/client';
import type { SystemInfo } from '../api/types';
import { useAuth } from '../auth';
import { Logo, ThemeToggle } from '../components/Layout';
import { Field } from '../components/ui';

function AuthShell({ children }: { children: ReactNode }) {
  return (
    <div className="auth">
      <aside className="auth-hero">
        <Logo />
        <div>
          <h1>Stop invoice fraud <em>before</em> the payment leaves.</h1>
          <div className="hero-points">
            <div className="hero-point"><ScanSearch size={18} /><span>Rule-based invoice verification against purchase orders, duplicates and arithmetic, with every finding explained.</span></div>
            <div className="hero-point"><Fingerprint size={18} /><span>Detects payment-redirection, split invoicing and unusual amounts, and keeps suspicious activity separate from confirmed fraud.</span></div>
            <div className="hero-point"><LineChart size={18} /><span>Transparent supplier risk scores from 0 to 100 that show how many points each factor adds.</span></div>
            <div className="hero-point"><Lock size={18} /><span>Role-based access, segregation of duties and a full audit trail of sensitive actions.</span></div>
          </div>
        </div>
        <p className="subtle">Portfolio project. Demo data is fictional.</p>
      </aside>
      <div className="auth-panel">
        <div style={{ position: 'absolute', top: 16, right: 16 }}><ThemeToggle /></div>
        {children}
      </div>
    </div>
  );
}

export function LoginPage() {
  const { login } = useAuth();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const info = useQuery({ queryKey: ['system-info'], queryFn: () => api.get<SystemInfo>('/api/system/info'), retry: false });

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await login(email, password); } catch (err) { setError(err instanceof ApiError ? err.message : 'Unable to sign in.'); } finally { setBusy(false); }
  }

  return (
    <AuthShell>
      <form className="card auth-card" onSubmit={submit} noValidate aria-describedby={error ? 'login-error' : undefined}>
        <h2>Sign in</h2>
        <p className="muted" style={{ margin: '4px 0 22px' }}>Welcome back to FinGuard AI.</p>
        <div className="stack" style={{ gap: 14 }}>
          <Field label="Email">{(id) => <input id={id} className="input" type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} required />}</Field>
          <Field label="Password">{(id) => <input id={id} className="input" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />}</Field>
          {error && <div className="callout danger" role="alert" id="login-error">{error}</div>}
          <button className="btn btn-primary" type="submit" disabled={busy || !email || !password}>{busy ? 'Signing in…' : 'Sign in'}</button>
          <div className="row" style={{ justifyContent: 'space-between', fontSize: 13 }}>
            <Link to="/forgot-password" className="muted">Forgot password?</Link>
            <Link to="/register" style={{ color: 'var(--cyan)' }}>Create an account</Link>
          </div>
        </div>
        {info.data?.demoData && info.data.environment === 'Development' && (
          <div className="demo-creds">
            <strong>Demo accounts</strong> (fictional, local development only). The password is in <span className="mono">appsettings.Development.json</span> and the README.
            <div className="row" style={{ marginTop: 6 }}>
              <button type="button" onClick={() => setEmail('admin@finguard.demo')}>admin@finguard.demo</button>
              <span className="subtle">·</span>
              <button type="button" onClick={() => setEmail('analyst@finguard.demo')}>analyst@finguard.demo</button>
            </div>
          </div>
        )}
      </form>
    </AuthShell>
  );
}

export function RegisterPage() {
  const { register } = useAuth();
  const [form, setForm] = useState({ email: '', displayName: '', password: '', confirm: '' });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const mismatch = form.confirm.length > 0 && form.confirm !== form.password;
  const tooShort = form.password.length > 0 && form.password.length < 12;

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (mismatch || tooShort) return;
    setBusy(true); setError(null);
    try { await register(form.email, form.displayName, form.password); } catch (err) { setError(err instanceof ApiError ? err.message : 'Registration failed.'); } finally { setBusy(false); }
  }
  const set = (k: keyof typeof form) => (e: ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: e.target.value });

  return (
    <AuthShell>
      <form className="card auth-card" onSubmit={submit} noValidate>
        <h2>Create an account</h2>
        <p className="muted" style={{ margin: '4px 0 22px' }}>New accounts get the Analyst role. An administrator can promote them.</p>
        <div className="stack" style={{ gap: 14 }}>
          <Field label="Full name">{(id) => <input id={id} className="input" autoComplete="name" value={form.displayName} onChange={set('displayName')} required />}</Field>
          <Field label="Work email">{(id) => <input id={id} className="input" type="email" autoComplete="email" value={form.email} onChange={set('email')} required />}</Field>
          <Field label="Password" hint="At least 12 characters. A passphrase works well." error={tooShort ? 'Use at least 12 characters.' : undefined}>
            {(id, d) => <input id={id} className="input" type="password" autoComplete="new-password" value={form.password} onChange={set('password')} aria-invalid={tooShort} aria-describedby={d} required />}
          </Field>
          <Field label="Confirm password" error={mismatch ? 'Passwords do not match.' : undefined}>
            {(id, d) => <input id={id} className="input" type="password" autoComplete="new-password" value={form.confirm} onChange={set('confirm')} aria-invalid={mismatch} aria-describedby={d} required />}
          </Field>
          {error && <div className="callout danger" role="alert">{error}</div>}
          <button className="btn btn-primary" disabled={busy || !form.email || !form.displayName || !form.password || mismatch || tooShort}>{busy ? 'Creating…' : 'Create account'}</button>
          <p className="subtle" style={{ textAlign: 'center', margin: 0 }}>Already registered? <Link to="/login" style={{ color: 'var(--cyan)' }}>Sign in</Link></p>
        </div>
      </form>
    </AuthShell>
  );
}

export function ForgotPasswordPage() {
  const [email, setEmail] = useState('');
  const [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [outbox, setOutbox] = useState<{ subject: string; body: string }[] | null>(null);
  const info = useQuery({ queryKey: ['system-info'], queryFn: () => api.get<SystemInfo>('/api/system/info'), retry: false });

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      await api.post('/api/auth/forgot-password', { email });
      setSent(true);
      if (info.data?.devOutbox) setOutbox(await api.get(`/api/system/dev-outbox${qs({ email })}`));
    } catch (err) { setError(err instanceof ApiError ? err.message : 'Request failed.'); } finally { setBusy(false); }
  }
  const link = outbox?.[0]?.body.match(/https?:\/\/\S+/)?.[0];

  return (
    <AuthShell>
      <form className="card auth-card" onSubmit={submit} noValidate>
        <h2>Reset your password</h2>
        <p className="muted" style={{ margin: '4px 0 22px' }}>We'll email you a link that is valid for 30 minutes.</p>
        {sent ? (
          <div className="stack" style={{ gap: 14 }}>
            <div className="callout ok" role="status"><BadgeCheck size={18} /> If an account exists for {email}, a reset link is on its way.</div>
            {info.data?.devOutbox && (
              <div className="callout info">
                <div>
                  <strong>Development mail outbox.</strong> No email provider is configured, so messages are stored locally.
                  {link ? <div style={{ marginTop: 8 }}><a href={new URL(link).pathname + new URL(link).search} style={{ color: 'var(--cyan)', fontWeight: 600 }}>Open the reset link →</a></div> : <div className="subtle">No message for this address.</div>}
                </div>
              </div>
            )}
            <Link to="/login" className="btn">Back to sign in</Link>
          </div>
        ) : (
          <div className="stack" style={{ gap: 14 }}>
            <Field label="Email">{(id) => <input id={id} className="input" type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />}</Field>
            {error && <div className="callout danger" role="alert">{error}</div>}
            <button className="btn btn-primary" disabled={busy || !email}>{busy ? 'Sending…' : 'Send reset link'}</button>
            <Link to="/login" className="subtle" style={{ textAlign: 'center' }}>Back to sign in</Link>
          </div>
        )}
      </form>
    </AuthShell>
  );
}

export function ResetPasswordPage() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const token = params.get('token') ?? '';
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (password !== confirm) { setError('Passwords do not match.'); return; }
    setBusy(true); setError(null);
    try { await api.post('/api/auth/reset-password', { token, newPassword: password }); setDone(true); setTimeout(() => navigate('/login'), 1800); }
    catch (err) { setError(err instanceof ApiError ? err.message : 'Reset failed.'); } finally { setBusy(false); }
  }

  return (
    <AuthShell>
      <form className="card auth-card" onSubmit={submit} noValidate>
        <h2>Choose a new password</h2>
        <p className="muted" style={{ margin: '4px 0 22px' }}>Resetting signs you out of every other session.</p>
        {!token ? <div className="callout danger">This reset link is missing its token. Request a new one.</div> : done ? (
          <div className="callout ok" role="status"><BadgeCheck size={18} /> Password updated. Redirecting to sign in…</div>
        ) : (
          <div className="stack" style={{ gap: 14 }}>
            <Field label="New password" hint="At least 12 characters.">{(id, d) => <input id={id} className="input" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} aria-describedby={d} />}</Field>
            <Field label="Confirm new password">{(id) => <input id={id} className="input" type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} />}</Field>
            {error && <div className="callout danger" role="alert">{error}</div>}
            <button className="btn btn-primary" disabled={busy || password.length < 12}>{busy ? 'Saving…' : 'Update password'}</button>
          </div>
        )}
      </form>
    </AuthShell>
  );
}
