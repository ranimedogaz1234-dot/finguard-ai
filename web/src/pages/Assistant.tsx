import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { Link } from 'react-router';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Bot, Cpu, Send, Sparkles } from 'lucide-react';
import { api } from '../api/client';
import type { AssistantReply, AssistantStatus, ChatMessage } from '../api/types';
import { PageHead } from '../components/Layout';
import { Card, RichText, errorText } from '../components/ui';

const SUGGESTIONS = [
  'Give me a financial summary',
  'What should I investigate first?',
  'Why is Crestline Trading LLC high risk?',
  'Compare Helios Data Systems and Northwind Office Supplies',
  'Which suppliers are the riskiest?',
];

type Turn = ChatMessage & { refs?: AssistantReply['references']; mode?: AssistantReply['mode'] };
const ROUTES: Record<string, string> = { supplier: '/suppliers/', invoice: '/invoices/', alert: '/alerts/' };

export function AssistantPage() {
  const status = useQuery({ queryKey: ['assistant-status'], queryFn: () => api.get<AssistantStatus>('/api/assistant/status') });
  const [turns, setTurns] = useState<Turn[]>([]);
  const [input, setInput] = useState('');
  const logRef = useRef<HTMLDivElement>(null);

  const ask = useMutation({
    mutationFn: (messages: ChatMessage[]) => api.post<AssistantReply>('/api/assistant/chat', { messages }),
    onSuccess: (r) => setTurns((t) => [...t, { role: 'assistant', content: r.reply, refs: r.references, mode: r.mode }]),
    onError: (e) => setTurns((t) => [...t, { role: 'assistant', content: `Sorry, I couldn't answer that: ${errorText(e)}` }]),
  });

  useEffect(() => { logRef.current?.scrollTo({ top: logRef.current.scrollHeight, behavior: 'smooth' }); }, [turns, ask.isPending]);

  function send(text: string) {
    const content = text.trim();
    if (!content || ask.isPending) return;
    const next: Turn[] = [...turns, { role: 'user', content }];
    setTurns(next);
    setInput('');
    ask.mutate(next.slice(-10).map(({ role, content: c }) => ({ role, content: c.slice(0, 4000) })));
  }
  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(input); } };
  const live = status.data?.mode === 'live';

  return (
    <>
      <PageHead
        title="AI financial assistant"
        subtitle="Ask about suppliers, invoices (by number), alerts (e.g. #1042) or the overall position. Answers are grounded in live platform data."
        actions={status.data && (
          <span className={`badge ${live ? 'b-live' : 'b-demo'}`} title={status.data.description} style={{ height: 28, padding: '0 12px' }}>
            {live ? <Sparkles size={13} /> : <Cpu size={13} />} {live ? `Live AI · ${status.data.provider}` : 'Local demonstration mode'}
          </span>
        )}
      />
      {status.data && !live && (
        <div className="callout info" style={{ marginBottom: 18 }}>
          <Cpu size={18} />
          <span><strong>No external AI service is configured.</strong> Answers come from FinGuard's built-in rules applied to your real data. They are not produced by a language model. To enable a live model, set <span className="mono">Assistant__Provider</span> and <span className="mono">Assistant__ApiKey</span> on the server (see README). The key never reaches the browser.</span>
        </div>
      )}
      <Card className="chat">
        <div className="chat-log" ref={logRef} aria-live="polite" aria-busy={ask.isPending}>
          {turns.length === 0 && (
            <div className="empty" style={{ padding: '30px 10px' }}>
              <div className="icon"><Bot size={24} /></div>
              <h3>How can I help with your financial controls?</h3>
              <div className="suggestions" style={{ justifyContent: 'center', marginTop: 10 }}>
                {SUGGESTIONS.map((s) => <button key={s} className="chip" onClick={() => send(s)}>{s}</button>)}
              </div>
            </div>
          )}
          {turns.map((t, i) => (
            <div key={i} className={`msg ${t.role}`}>
              {t.role === 'assistant' ? <RichText text={t.content} /> : t.content}
              {t.role === 'assistant' && t.mode === 'live' && (
                <p className="subtle" style={{ marginTop: 8 }}>AI-generated from FinGuard data. Verify against the source records before acting.</p>
              )}
              {t.refs && t.refs.length > 0 && (
                <div className="row" style={{ marginTop: 8, gap: 6 }}>
                  {t.refs.map((r) => <Link key={r.type + r.id} to={ROUTES[r.type] + r.id} className="chip" style={{ padding: '4px 10px' }}>{r.label} →</Link>)}
                </div>
              )}
            </div>
          ))}
          {ask.isPending && <div className="msg assistant typing" aria-label="Assistant is thinking"><span /><span /><span /></div>}
        </div>
        <form className="chat-input" onSubmit={(e: FormEvent) => { e.preventDefault(); send(input); }}>
          <textarea className="textarea" placeholder="Ask about a supplier, invoice number or alert…" aria-label="Message" value={input} onChange={(e) => setInput(e.target.value)} onKeyDown={onKey} maxLength={4000} />
          <button className="btn btn-primary" style={{ height: 44 }} disabled={!input.trim() || ask.isPending} aria-label="Send"><Send size={16} /></button>
        </form>
      </Card>
      <p className="subtle" style={{ marginTop: 10 }}>Decision support only: the assistant never approves, rejects or pays anything. Alerts are suspicious patterns unless marked as confirmed fraud.</p>
    </>
  );
}
