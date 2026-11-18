import type { ReactNode } from 'react';
import { Navigate, Route, Routes, useLocation, useSearchParams } from 'react-router';
import { useAuth } from './auth';
import { Layout } from './components/Layout';
import { Skeleton } from './components/ui';
import { ForgotPasswordPage, LoginPage, RegisterPage, ResetPasswordPage } from './pages/Auth';
import { DashboardPage } from './pages/Dashboard';
import { SupplierComparePage, SupplierDetailPage, SuppliersPage } from './pages/Suppliers';
import { InvoiceDetailPage, InvoiceNewPage, InvoicesPage } from './pages/Invoices';
import { AlertDetailPage, AlertsPage } from './pages/Alerts';
import { AssistantPage } from './pages/Assistant';
import { AuditPage, UsersPage } from './pages/Admin';
import { SettingsPage } from './pages/Settings';
import { NotFoundPage } from './pages/NotFound';

function Protected({ children, admin }: { children: ReactNode; admin?: boolean }) {
  const { user, loading, isAdmin } = useAuth();
  const location = useLocation();
  if (loading)
    return (
      <div style={{ padding: 40, display: 'grid', gap: 16, maxWidth: 900, margin: '0 auto' }} aria-busy="true">
        <Skeleton h={28} w={240} /><Skeleton h={120} /><Skeleton h={260} />
      </div>
    );
  if (!user) return <Navigate to={`/login?next=${encodeURIComponent(location.pathname + location.search)}`} replace />;
  if (admin && !isAdmin) return <NotFoundPage forbidden />;
  return <>{children}</>;
}

function GuestOnly({ children }: { children: ReactNode }) {
  const { user, loading } = useAuth();
  const [params] = useSearchParams();
  if (loading) return null;
  if (user) {
    const next = params.get('next');
    // Only allow internal redirects (open-redirect protection).
    return <Navigate to={next && next.startsWith('/') && !next.startsWith('//') ? next : '/'} replace />;
  }
  return <>{children}</>;
}

export function App() {
  return (
    <Routes>
      <Route path="/login" element={<GuestOnly><LoginPage /></GuestOnly>} />
      <Route path="/register" element={<GuestOnly><RegisterPage /></GuestOnly>} />
      <Route path="/forgot-password" element={<GuestOnly><ForgotPasswordPage /></GuestOnly>} />
      <Route path="/reset-password" element={<ResetPasswordPage />} />
      <Route element={<Protected><Layout /></Protected>}>
        <Route index element={<DashboardPage />} />
        <Route path="suppliers" element={<SuppliersPage />} />
        <Route path="suppliers/compare" element={<SupplierComparePage />} />
        <Route path="suppliers/:id" element={<SupplierDetailPage />} />
        <Route path="invoices" element={<InvoicesPage />} />
        <Route path="invoices/new" element={<InvoiceNewPage />} />
        <Route path="invoices/:id" element={<InvoiceDetailPage />} />
        <Route path="alerts" element={<AlertsPage />} />
        <Route path="alerts/:id" element={<AlertDetailPage />} />
        <Route path="assistant" element={<AssistantPage />} />
        <Route path="settings" element={<SettingsPage />} />
        <Route path="admin/users" element={<Protected admin><UsersPage /></Protected>} />
        <Route path="admin/audit" element={<Protected admin><AuditPage /></Protected>} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
