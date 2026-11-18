import { useEffect, useState, type ReactNode } from 'react';
import { Link, NavLink, Outlet, useLocation } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import {
  Bot, Building2, FileCheck2, LayoutDashboard, LogOut, Menu, Moon, ScrollText, Settings, ShieldAlert, ShieldCheck, Sun, Users,
} from 'lucide-react';
import { api } from '../api/client';
import type { Dashboard, SystemInfo } from '../api/types';
import { useAuth, useTheme } from '../auth';

export function Logo() {
  return (
    <div className="brand">
      <div className="brand-mark" aria-hidden><ShieldCheck size={19} /></div>
      <div className="brand-name">FinGuard <span>AI</span></div>
    </div>
  );
}

function initials(name: string) {
  return name.split(' ').filter(Boolean).slice(0, 2).map((w) => w[0]!.toUpperCase()).join('');
}

export function ThemeToggle() {
  const { theme, toggle } = useTheme();
  return (
    <button className="btn btn-icon btn-ghost" onClick={toggle} aria-label={`Switch to ${theme === 'dark' ? 'light' : 'dark'} theme`} title="Toggle theme">
      {theme === 'dark' ? <Sun size={18} /> : <Moon size={18} />}
    </button>
  );
}

export function Layout() {
  const { user, isAdmin, logout } = useAuth();
  const [open, setOpen] = useState(false);
  const location = useLocation();
  useEffect(() => setOpen(false), [location.pathname]);

  const info = useQuery({ queryKey: ['system-info'], queryFn: () => api.get<SystemInfo>('/api/system/info'), staleTime: 60_000 });
  const dash = useQuery({ queryKey: ['dashboard'], queryFn: () => api.get<Dashboard>('/api/dashboard'), staleTime: 30_000 });
  const openAlerts = dash.data?.kpis.openAlerts;

  const link = (to: string, icon: ReactNode, label: string, extra?: ReactNode) => (
    <NavLink to={to} end={to === '/'} className={({ isActive }) => `nav-link ${isActive ? 'active' : ''}`}>
      {icon}<span>{label}</span>{extra}
    </NavLink>
  );

  return (
    <div className="shell">
      <a className="skip-link" href="#main">Skip to content</a>
      {open && <div className="scrim" onClick={() => setOpen(false)} aria-hidden />}
      <aside className={`sidebar ${open ? 'open' : ''}`} aria-label="Main navigation">
        <Logo />
        <nav style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          <div className="nav-label">Monitor</div>
          {link('/', <LayoutDashboard size={18} />, 'Overview')}
          {link('/alerts', <ShieldAlert size={18} />, 'Fraud alerts', openAlerts ? <span className="count" aria-label={`${openAlerts} open`}>{openAlerts}</span> : null)}
          {link('/assistant', <Bot size={18} />, 'AI assistant')}
          <div className="nav-label">Operate</div>
          {link('/invoices', <FileCheck2 size={18} />, 'Invoices')}
          {link('/suppliers', <Building2 size={18} />, 'Suppliers')}
          {isAdmin && (
            <>
              <div className="nav-label">Administration</div>
              {link('/admin/users', <Users size={18} />, 'Users')}
              {link('/admin/audit', <ScrollText size={18} />, 'Audit log')}
            </>
          )}
        </nav>
        <div className="sidebar-footer">
          {link('/settings', <Settings size={18} />, 'Settings')}
          <div className="user-chip">
            <div className="avatar" aria-hidden>{initials(user?.displayName ?? '?')}</div>
            <div style={{ minWidth: 0, flex: 1 }}>
              <div style={{ fontWeight: 600, fontSize: 13, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{user?.displayName}</div>
              <div className="subtle">{user?.role}</div>
            </div>
            <button className="btn btn-ghost btn-sm btn-icon" onClick={() => void logout()} aria-label="Sign out" title="Sign out"><LogOut size={16} /></button>
          </div>
        </div>
      </aside>

      <div className="main">
        {info.data?.demoData && (
          <div className="demo-banner" role="note">
            <span className="badge b-demo">Demo</span>
            <span>You are exploring <strong>fictional demonstration data</strong>. Supplier names, IBANs and amounts are invented; no real money moves.</span>
          </div>
        )}
        <header className="topbar">
          <button className="btn btn-icon btn-ghost menu-btn" onClick={() => setOpen(true)} aria-label="Open navigation" aria-expanded={open}><Menu size={20} /></button>
          <Link to="/" className="hide-sm subtle">FinGuard AI · Financial controls</Link>
          <div className="grow" />
          <ThemeToggle />
        </header>
        <main id="main" className="content" tabIndex={-1}>
          <Outlet />
        </main>
      </div>
    </div>
  );
}

export function PageHead({ title, subtitle, crumbs, actions }: { title: ReactNode; subtitle?: ReactNode; crumbs?: { to: string; label: string }[]; actions?: ReactNode }) {
  useEffect(() => {
    if (typeof title === 'string') document.title = `${title} · FinGuard AI`;
  }, [title]);
  return (
    <div className="page-head">
      <div>
        {crumbs && (
          <div className="crumbs">
            {crumbs.map((c) => (<span key={c.to} className="row" style={{ gap: 6 }}><Link to={c.to}>{c.label}</Link><span aria-hidden>/</span></span>))}
          </div>
        )}
        <h1>{title}</h1>
        {subtitle && <p>{subtitle}</p>}
      </div>
      {actions && <div className="row">{actions}</div>}
    </div>
  );
}
