import {
  Area, AreaChart, Bar, BarChart, CartesianGrid, Cell, Legend, Line, LineChart, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts';
import type { NamedCount, RiskHistoryPoint } from '../api/types';
import { money } from '../lib/format';

export const LEVEL_COLORS: Record<string, string> = { Low: '#10b981', Medium: '#f59e0b', High: '#f97316', Critical: '#ef4444' };
const AXIS = { stroke: 'transparent', tickLine: false } as const;

type TooltipRow = { name?: string | number; value?: number | string; color?: string; dataKey?: string | number };
function ChartTooltip({ active, payload, label, format }: { active?: boolean; payload?: TooltipRow[]; label?: string | number; format?: (v: number, key?: string) => string }) {
  if (!active || !payload?.length) return null;
  return (
    <div className="chart-tooltip">
      {label !== undefined && <div style={{ fontWeight: 650, marginBottom: 4 }}>{label}</div>}
      {payload.map((p) => (
        <div key={String(p.dataKey ?? p.name)} className="row" style={{ gap: 8 }}>
          <span style={{ width: 8, height: 8, borderRadius: 2, background: p.color }} />
          <span className="muted">{p.name}</span>
          <span className="num" style={{ marginLeft: 'auto', fontWeight: 600 }}>{format ? format(Number(p.value), String(p.dataKey)) : p.value}</span>
        </div>
      ))}
    </div>
  );
}

export function TrendChart({ data, currency }: { data: { month: string; amount: number; flaggedAmount: number; invoices: number; flagged: number }[]; currency: string }) {
  const rows = data.map((d) => ({ ...d, label: new Date(`${d.month}-01T00:00:00`).toLocaleDateString('en-GB', { month: 'short' }), clean: d.amount - d.flaggedAmount }));
  return (
    <div style={{ height: 280 }} role="img" aria-label="Invoice volume over the last 12 months: total and flagged amounts">
      <ResponsiveContainer>
        <AreaChart data={rows} margin={{ top: 8, right: 8, left: 0, bottom: 0 }}>
          <defs>
            <linearGradient id="gClean" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor="#3b82f6" stopOpacity={0.45} /><stop offset="1" stopColor="#3b82f6" stopOpacity={0} /></linearGradient>
            <linearGradient id="gFlag" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor="#f97316" stopOpacity={0.5} /><stop offset="1" stopColor="#f97316" stopOpacity={0} /></linearGradient>
          </defs>
          <CartesianGrid vertical={false} />
          <XAxis dataKey="label" {...AXIS} />
          <YAxis {...AXIS} width={64} tickFormatter={(v: number) => money(v, currency, true)} />
          <Tooltip content={<ChartTooltip format={(v) => money(v, currency)} />} />
          <Legend iconType="circle" wrapperStyle={{ fontSize: 12 }} />
          <Area type="monotone" dataKey="clean" name="Passed rules" stackId="1" stroke="#3b82f6" strokeWidth={2} fill="url(#gClean)" />
          <Area type="monotone" dataKey="flaggedAmount" name="Flagged" stackId="1" stroke="#f97316" strokeWidth={2} fill="url(#gFlag)" />
        </AreaChart>
      </ResponsiveContainer>
    </div>
  );
}

export function DonutChart({ data, colors, label }: { data: NamedCount[]; colors: Record<string, string>; label: string }) {
  const total = data.reduce((s, d) => s + d.count, 0);
  return (
    <div className="row" style={{ flexWrap: 'nowrap', gap: 18 }}>
      <div style={{ width: 150, height: 150, position: 'relative', flexShrink: 0 }} role="img" aria-label={`${label}: ${data.map((d) => `${d.name} ${d.count}`).join(', ')}`}>
        <ResponsiveContainer>
          <PieChart>
            <Pie data={total ? data : [{ name: 'none', count: 1 }]} dataKey="count" nameKey="name" innerRadius={50} outerRadius={70} paddingAngle={total ? 3 : 0} stroke="none">
              {(total ? data : [{ name: 'none', count: 1 }]).map((d) => <Cell key={d.name} fill={total ? colors[d.name] ?? '#64748b' : 'var(--surface-2)'} />)}
            </Pie>
          </PieChart>
        </ResponsiveContainer>
        <div className="gauge-label"><strong style={{ fontSize: 24 }}>{total}</strong><span className="subtle">{label}</span></div>
      </div>
      <ul style={{ listStyle: 'none', padding: 0, margin: 0, display: 'grid', gap: 8, flex: 1 }}>
        {data.map((d) => (
          <li key={d.name} className="row" style={{ gap: 8, fontSize: 13 }}>
            <span style={{ width: 10, height: 10, borderRadius: 3, background: colors[d.name] ?? '#64748b' }} />
            <span className="muted">{d.name}</span>
            <span className="num" style={{ marginLeft: 'auto', fontWeight: 650 }}>{d.count}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

export function HBarChart({ data, color = '#8b5cf6', format, height = 240 }: { data: NamedCount[]; color?: string; format?: (v: number) => string; height?: number }) {
  return (
    <div style={{ height }} role="img" aria-label={data.map((d) => `${d.name}: ${d.count}`).join(', ')}>
      <ResponsiveContainer>
        <BarChart data={data} layout="vertical" margin={{ top: 0, right: 12, left: 0, bottom: 0 }}>
          <CartesianGrid horizontal={false} />
          <XAxis type="number" {...AXIS} tickFormatter={format} />
          <YAxis type="category" dataKey="name" width={150} {...AXIS} />
          <Tooltip cursor={{ fill: 'var(--surface-hover)' }} content={<ChartTooltip format={format} />} />
          <Bar dataKey="count" name="Value" fill={color} radius={[0, 6, 6, 0]} barSize={14} />
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

export function RiskHistoryChart({ data }: { data: RiskHistoryPoint[] }) {
  const rows = data.map((d) => ({ ...d, label: new Date(d.assessedAtUtc).toLocaleDateString('en-GB', { day: '2-digit', month: 'short' }) }));
  return (
    <div style={{ height: 230 }} role="img" aria-label={`Risk score history: ${data.map((d) => d.score).join(', ')}`}>
      <ResponsiveContainer>
        <LineChart data={rows} margin={{ top: 10, right: 10, left: -10, bottom: 0 }}>
          <CartesianGrid vertical={false} />
          <XAxis dataKey="label" {...AXIS} minTickGap={24} />
          <YAxis domain={[0, 100]} {...AXIS} ticks={[0, 20, 45, 70, 100]} />
          <Tooltip content={<ChartTooltip />} />
          <Line type="monotone" dataKey="score" name="Risk score" stroke="#22d3ee" strokeWidth={2.5} dot={{ r: 2.5 }} activeDot={{ r: 5 }} />
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}

/** Semi-circular gauge for a 0–100 risk score. */
export function RiskGauge({ score, level }: { score: number; level: string }) {
  const r = 70, c = Math.PI * r, pctVal = Math.min(Math.max(score, 0), 100) / 100;
  const color = LEVEL_COLORS[level] ?? '#3b82f6';
  return (
    <div className="gauge-wrap" style={{ width: 190, height: 120 }}>
      <svg width="190" height="110" viewBox="0 0 190 110" role="img" aria-label={`Risk score ${score} of 100, ${level}`}>
        <path d="M 25 95 A 70 70 0 0 1 165 95" fill="none" stroke="var(--surface-2)" strokeWidth="14" strokeLinecap="round" />
        <path d="M 25 95 A 70 70 0 0 1 165 95" fill="none" stroke={color} strokeWidth="14" strokeLinecap="round"
          strokeDasharray={`${c * pctVal} ${c}`} style={{ transition: 'stroke-dasharray .8s cubic-bezier(.2,.7,.2,1)', filter: `drop-shadow(0 0 8px ${color}66)` }} />
      </svg>
      <div className="gauge-label" style={{ paddingTop: 34 }}>
        <strong className="num">{score}</strong>
        <span className="subtle">of 100 · {level}</span>
      </div>
    </div>
  );
}
