import { Link } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { AlertOctagon, Banknote, Building2, FileCheck2, FileWarning, ShieldAlert } from 'lucide-react';
import type { ReactNode } from 'react';
import { api } from '../api/client';
import type { Dashboard, Priority } from '../api/types';
import { DonutChart, HBarChart, LEVEL_COLORS, TrendChart } from '../components/charts';
import { PageHead } from '../components/Layout';
import { Badge, Card, Empty, ErrorState, ScoreBar, Skeleton } from '../components/ui';
import { ACTION_LABELS, humanize, money, num, pct, relative } from '../lib/format';

function Kpi({ label, value, foot, icon, color, glow, to }: { label: string; value: ReactNode; foot?: ReactNode; icon: ReactNode; color: string; glow: string; to?: string }) {
  const body = (
    <div className="card kpi" style={{ ['--kpi-color' as string]: color, ['--kpi-glow' as string]: glow }}>
      <div className="kpi-top"><span>{label}</span><span className="kpi-icon">{icon}</span></div>
      <div className="kpi-value">{value}</div>
      {foot && <div className="kpi-foot">{foot}</div>}
    </div>
  );
  return to ? <Link to={to} aria-label={label}>{body}</Link> : body;
}

const SEVERITY_COLORS = { Critical: '#ef4444', High: '#f97316', Medium: '#f59e0b', Low: '#3b82f6' };

export function DashboardPage() {
  const q = useQuery({ queryKey: ['dashboard'], queryFn: () => api.get<Dashboard>('/api/dashboard') });
  const prio = useQuery({ queryKey: ['priorities'], queryFn: () => api.get<Priority[]>('/api/assistant/priorities') });

  if (q.isError) return <><PageHead title="Financial overview" /><Card><ErrorState error={q.error} onRetry={() => q.refetch()} /></Card></>;
  const d = q.data;
  const ccy = d?.kpis.baseCurrency ?? 'EUR';

  return (
    <>
      <PageHead
        title="Financial overview"
        subtitle={d ? `Computed from stored platform data · updated ${relative(d.generatedAtUtc)}` : 'Loading live metrics…'}
        actions={<Link to="/invoices/new" className="btn btn-primary"><FileCheck2 size={16} /> Verify an invoice</Link>}
      />
      <div className="stack">
        <div className="grid grid-kpi six">
          {!d ? Array.from({ length: 6 }).map((_, i) => <div key={i} className="card kpi"><Skeleton h={14} w={120} /><div style={{ height: 14 }} /><Skeleton h={28} w={140} /><div style={{ height: 8 }} /><Skeleton h={12} w={90} /></div>) : (
            <>
              <Kpi label="Invoices processed" value={num(d.kpis.totalInvoices)} foot={`${money(d.kpis.totalProcessed, ccy, true)} total value`} icon={<FileCheck2 size={17} />} color="var(--cyan)" glow="rgba(34,211,238,.18)" to="/invoices" />
              <Kpi label="Passed all rules" value={num(d.kpis.verifiedInvoices)} foot={`${pct(d.kpis.verifiedInvoices, d.kpis.totalInvoices)} of invoices`} icon={<FileCheck2 size={17} />} color="var(--green)" glow="rgba(16,185,129,.16)" to="/invoices?decision=AutoApprove" />
              <Kpi label="Flagged for review" value={num(d.kpis.flaggedInvoices)} foot={`${d.kpis.rejectedBySystem} recommended for rejection · ${d.kpis.pendingReview} pending`} icon={<FileWarning size={17} />} color="var(--amber)" glow="rgba(245,158,11,.16)" to="/invoices?decision=flagged" />
              <Kpi label="Financial exposure" value={money(d.kpis.financialExposure, ccy, true)} foot="Flagged invoices not yet paid" icon={<Banknote size={17} />} color="var(--violet)" glow="rgba(139,92,246,.2)" to="/invoices?decision=flagged&status=Pending" />
              <Kpi label="Open fraud alerts" value={num(d.kpis.openAlerts)} foot={`${d.kpis.criticalAlerts} critical · ${d.kpis.confirmedFraud} confirmed fraud`} icon={<ShieldAlert size={17} />} color="var(--red)" glow="rgba(239,68,68,.18)" to="/alerts" />
              <Kpi label="High-risk suppliers" value={`${d.kpis.highRiskSuppliers}`} foot={`of ${d.kpis.activeSuppliers} active suppliers`} icon={<Building2 size={17} />} color="var(--orange)" glow="rgba(249,115,22,.18)" to="/suppliers?sort=risk" />
            </>
          )}
        </div>

        <div className="grid grid-3">
          <Card title="Invoice value · last 12 months" actions={<span className="subtle">{ccy}{d?.kpis.otherCurrencies.length ? ` · excludes ${d.kpis.otherCurrencies.join(', ')}` : ''}</span>}>
            {d ? <TrendChart data={d.trend} currency={ccy} /> : <Skeleton h={280} />}
          </Card>
          <Card title="Supplier risk distribution">
            {d ? <DonutChart data={d.riskDistribution} colors={LEVEL_COLORS} label="suppliers" /> : <Skeleton h={150} />}
            <p className="subtle" style={{ marginTop: 14 }}>Scores are internal rule-based heuristics (see the Suppliers page for the factors behind each score), not credit ratings.</p>
          </Card>
        </div>

        <div className="grid grid-3">
          <Card title="Investigation priorities" actions={<Link to="/alerts" className="btn btn-sm">All alerts</Link>}>
            {prio.isLoading ? <Skeleton h={180} /> : prio.isError ? <ErrorState error={prio.error} /> : !prio.data?.length ? <Empty title="No open alerts">Nothing needs investigation right now.</Empty> : (
              <div className="table-wrap">
                <table className="data">
                  <thead><tr><th>Alert</th><th className="hide-sm">Supplier</th><th>Severity</th><th className="right">Exposure</th></tr></thead>
                  <tbody>
                    {prio.data.slice(0, 6).map((p) => (
                      <tr key={p.alertId}>
                        <td><Link to={`/alerts/${p.alertId}`} className="cell-title">#{p.alertNumber} {p.title}</Link><div className="cell-sub">{humanize(p.status)} · priority {p.priorityScore.toFixed(1)}</div></td>
                        <td className="hide-sm">{p.supplier}</td>
                        <td><Badge value={p.severity} kind="level" /></td>
                        <td className="right num">{money(p.exposure, ccy)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Card>
          <Card title="Open alerts by severity">
            {d ? <DonutChart data={d.alertsBySeverity} colors={SEVERITY_COLORS} label="open" /> : <Skeleton h={150} />}
            {d && d.kpis.openAlertExposure > 0 && <div className="callout warn" style={{ marginTop: 16 }}><AlertOctagon size={18} /><span>{money(d.kpis.openAlertExposure, ccy)} is tied to open alerts. These are <strong>suspicious</strong> patterns, not confirmed fraud.</span></div>}
          </Card>
        </div>

        <div className="grid grid-3b">
          <Card title="Highest-risk suppliers">
            {!d ? <Skeleton h={200} /> : d.topRiskSuppliers.map((s) => (
              <Link key={s.id} to={`/suppliers/${s.id}`} className="list-item">
                <div style={{ flex: 1, minWidth: 0 }}><div className="cell-title" style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{s.name}</div><div className="cell-sub">{s.openAlerts} open alert(s)</div></div>
                <ScoreBar score={s.score} />
              </Link>
            ))}
          </Card>
          <Card title="Most frequent findings">
            {!d ? <Skeleton h={200} /> : d.topDiscrepancies.length === 0 ? <Empty title="No discrepancies yet" /> : <HBarChart data={d.topDiscrepancies.map((x) => ({ ...x, name: humanize(x.name) }))} />}
          </Card>
          <Card title="Recent activity">
            {!d ? <Skeleton h={200} /> : d.recentActivity.length === 0 ? <Empty title="No activity yet" /> : (
              <ul className="timeline">
                {d.recentActivity.slice(0, 7).map((a, i) => (
                  <li key={i}>
                    <div style={{ fontSize: 13 }}><strong>{a.actor === 'system' || a.actor === 'anonymous' ? 'System' : a.actor.split('@')[0]}</strong> {ACTION_LABELS[a.action] ?? humanize(a.action.replace('.', '_'))}</div>
                    <div className="cell-sub">{a.details ? `${a.details} · ` : ''}{relative(a.atUtc)}</div>
                  </li>
                ))}
              </ul>
            )}
          </Card>
        </div>

        <Card title={`Spend by category · ${ccy}`}>
          {d ? <HBarChart data={d.spendByCategory} color="#3b82f6" format={(v) => money(v, ccy, true)} height={260} /> : <Skeleton h={260} />}
        </Card>
      </div>
    </>
  );
}
