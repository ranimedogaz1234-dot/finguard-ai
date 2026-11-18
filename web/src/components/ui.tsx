import { createContext, useCallback, useContext, useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { AlertTriangle, CheckCircle2, ChevronLeft, ChevronRight, Inbox, RefreshCw, X } from 'lucide-react';
import { ApiError } from '../api/client';
import { humanize } from '../lib/format';

export function Card({ title, actions, children, className = '', pad = false }: { title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string; pad?: boolean }) {
  return (
    <section className={`card ${pad ? 'card-pad' : ''} ${className}`}>
      {(title || actions) && (
        <div className="card-head">
          {typeof title === 'string' ? <h2>{title}</h2> : title}
          {actions && <div className="row">{actions}</div>}
        </div>
      )}
      {title || actions ? <div className="card-body">{children}</div> : children}
    </section>
  );
}

const LEVEL_CLASS: Record<string, string> = { Low: 'b-low', Medium: 'b-medium', High: 'b-high', Critical: 'b-critical' };

export function Badge({ value, kind, label }: { value: string; kind?: 'level' | 'status'; label?: string }) {
  const cls = kind === 'level' ? LEVEL_CLASS[value] ?? 'b-neutral' : `b-${value}`;
  return (
    <span className={`badge ${cls}`}>
      <span className="dot" aria-hidden />
      {label ?? humanize(value)}
    </span>
  );
}

export const DemoBadge = () => <span className="badge b-demo" title="Fictional demonstration data">Demo</span>;

export function ScoreBar({ score }: { score: number }) {
  const color = score >= 70 ? 'var(--red)' : score >= 45 ? 'var(--orange)' : score >= 20 ? 'var(--amber)' : 'var(--green)';
  return (
    <div className="row" style={{ gap: 8, flexWrap: 'nowrap' }}>
      <div className="score-bar" role="meter" aria-valuemin={0} aria-valuemax={100} aria-valuenow={score} aria-label="Risk score">
        <span style={{ width: `${Math.max(score, 3)}%`, background: color }} />
      </div>
      <span className="num" style={{ fontWeight: 650, minWidth: 22 }}>{score}</span>
    </div>
  );
}

export function Skeleton({ h = 16, w = '100%', r }: { h?: number; w?: number | string; r?: number }) {
  return <div className="skeleton" style={{ height: h, width: w, borderRadius: r }} aria-hidden />;
}

export function SkeletonRows({ rows = 6, cols = 5 }: { rows?: number; cols?: number }) {
  return (
    <div style={{ padding: 16, display: 'grid', gap: 14 }} aria-busy="true" aria-label="Loading">
      {Array.from({ length: rows }).map((_, i) => (
        <div key={i} style={{ display: 'grid', gridTemplateColumns: `2fr repeat(${cols - 1}, 1fr)`, gap: 16 }}>
          {Array.from({ length: cols }).map((__, j) => <Skeleton key={j} h={14} w={j === 0 ? '80%' : '60%'} />)}
        </div>
      ))}
    </div>
  );
}

export function Empty({ title, children, icon }: { title: string; children?: ReactNode; icon?: ReactNode }) {
  return (
    <div className="empty">
      <div className="icon">{icon ?? <Inbox size={22} />}</div>
      <h3>{title}</h3>
      {children && <div className="muted" style={{ maxWidth: 420 }}>{children}</div>}
    </div>
  );
}

export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const msg = error instanceof ApiError ? error.message : 'Something went wrong while loading this data.';
  return (
    <div className="empty" role="alert">
      <div className="icon" style={{ color: 'var(--red)' }}><AlertTriangle size={22} /></div>
      <h3>Couldn't load this</h3>
      <div className="muted">{msg}</div>
      {onRetry && <button className="btn btn-sm" onClick={onRetry} style={{ marginTop: 8 }}><RefreshCw size={14} /> Try again</button>}
    </div>
  );
}

export function Field({ label, error, hint, children, className = '' }: { label: string; error?: string; hint?: string; children: (id: string, describedBy?: string) => ReactNode; className?: string }) {
  const id = useId();
  const describedBy = error ? `${id}-err` : hint ? `${id}-hint` : undefined;
  return (
    <div className={`field ${className}`}>
      <label htmlFor={id}>{label}</label>
      {children(id, describedBy)}
      {error ? <span className="error" id={`${id}-err`}>{error}</span> : hint ? <span className="hint" id={`${id}-hint`}>{hint}</span> : null}
    </div>
  );
}

export function Pagination({ page, totalPages, total, onPage, label = 'results' }: { page: number; totalPages: number; total: number; onPage: (p: number) => void; label?: string }) {
  return (
    <nav className="pagination" aria-label="Pagination">
      <span>{total.toLocaleString('en-GB')} {label}</span>
      <div className="row">
        <button className="btn btn-sm btn-icon" onClick={() => onPage(page - 1)} disabled={page <= 1} aria-label="Previous page"><ChevronLeft size={16} /></button>
        <span className="num">Page {page} of {Math.max(totalPages, 1)}</span>
        <button className="btn btn-sm btn-icon" onClick={() => onPage(page + 1)} disabled={page >= totalPages} aria-label="Next page"><ChevronRight size={16} /></button>
      </div>
    </nav>
  );
}

export function Modal({ title, onClose, children, footer }: { title: string; onClose: () => void; children: ReactNode; footer?: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  const titleId = useId();
  useEffect(() => {
    const prev = document.activeElement as HTMLElement | null;
    const el = ref.current;
    el?.querySelector<HTMLElement>('input, select, textarea, button:not([data-close])')?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
      if (e.key === 'Tab' && el) {
        // Focus trap
        const f = Array.from(el.querySelectorAll<HTMLElement>('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])')).filter((x) => !x.hasAttribute('disabled'));
        if (f.length === 0) return;
        if (e.shiftKey && document.activeElement === f[0]) { e.preventDefault(); f[f.length - 1].focus(); }
        else if (!e.shiftKey && document.activeElement === f[f.length - 1]) { e.preventDefault(); f[0].focus(); }
      }
    };
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('keydown', onKey); prev?.focus(); };
  }, [onClose]);
  return (
    <div className="overlay" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal" role="dialog" aria-modal="true" aria-labelledby={titleId} ref={ref}>
        <div className="modal-head">
          <h2 id={titleId}>{title}</h2>
          <button className="btn btn-ghost btn-sm btn-icon" onClick={onClose} aria-label="Close" data-close><X size={16} /></button>
        </div>
        <div className="modal-body">{children}</div>
        {footer && <div className="modal-foot">{footer}</div>}
      </div>
    </div>
  );
}

// ---------- Toasts ----------
type Toast = { id: number; kind: 'success' | 'error'; text: string };
const ToastCtx = createContext<(kind: Toast['kind'], text: string) => void>(() => {});
export const useToast = () => useContext(ToastCtx);

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<Toast[]>([]);
  const push = useCallback((kind: Toast['kind'], text: string) => {
    const id = Date.now() + Math.random();
    setItems((t) => [...t, { id, kind, text }]);
    setTimeout(() => setItems((t) => t.filter((x) => x.id !== id)), 4500);
  }, []);
  return (
    <ToastCtx.Provider value={push}>
      {children}
      <div className="toasts" role="status" aria-live="polite">
        {items.map((t) => (
          <div key={t.id} className={`toast ${t.kind}`}>
            {t.kind === 'success' ? <CheckCircle2 size={18} /> : <AlertTriangle size={18} />}
            <span>{t.text}</span>
          </div>
        ))}
      </div>
    </ToastCtx.Provider>
  );
}

export function errorText(e: unknown): string {
  return e instanceof ApiError ? e.message : 'Unexpected error. Please try again.';
}

/** Minimal, safe renderer for the assistant's **bold**, _italic_, bullet and numbered lists (no HTML injection). */
export function RichText({ text }: { text: string }) {
  const blocks: ReactNode[] = [];
  const lines = text.split('\n');
  let list: { ordered: boolean; items: string[] } | null = null;
  const flush = () => {
    if (!list) return;
    const items = list.items.map((it, i) => <li key={i}>{inline(it)}</li>);
    blocks.push(list.ordered ? <ol key={blocks.length}>{items}</ol> : <ul key={blocks.length}>{items}</ul>);
    list = null;
  };
  for (const raw of lines) {
    const line = raw.trimEnd();
    const bullet = /^\s*[-•]\s+(.*)$/.exec(line);
    const ordered = /^\s*\d+\.\s+(.*)$/.exec(line);
    if (bullet || ordered) {
      const isOrdered = !!ordered;
      if (!list || list.ordered !== isOrdered) { flush(); list = { ordered: isOrdered, items: [] }; }
      list.items.push((bullet ?? ordered)![1]);
    } else {
      flush();
      if (line.trim()) blocks.push(<p key={blocks.length}>{inline(line)}</p>);
    }
  }
  flush();
  return <>{blocks}</>;
}

function inline(s: string): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /(\*\*[^*]+\*\*|_[^_]+_)/g;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(s))) {
    if (m.index > last) out.push(s.slice(last, m.index));
    const tok = m[0];
    out.push(tok.startsWith('**') ? <strong key={m.index}>{tok.slice(2, -2)}</strong> : <em key={m.index}>{tok.slice(1, -1)}</em>);
    last = m.index + tok.length;
  }
  if (last < s.length) out.push(s.slice(last));
  return out;
}
