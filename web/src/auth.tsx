import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { api, ApiError, refreshCsrf, setUnauthorizedHandler } from './api/client';
import type { User } from './api/types';

interface AuthState {
  user: User | null;
  loading: boolean;
  isAdmin: boolean;
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, displayName: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  setUser: (u: User) => void;
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);
  const qc = useQueryClient();

  useEffect(() => {
    setUnauthorizedHandler(() => { setUser(null); qc.clear(); });
    api.get<User>('/api/auth/me')
      .then(setUser)
      .catch((e) => { if (!(e instanceof ApiError && e.status === 401)) console.warn('Session check failed', e); })
      .finally(() => setLoading(false));
  }, [qc]);

  const login = useCallback(async (email: string, password: string) => {
    const u = await api.post<User>('/api/auth/login', { email, password });
    await refreshCsrf(); // token is bound to the new identity
    qc.clear();
    setUser(u);
  }, [qc]);

  const register = useCallback(async (email: string, displayName: string, password: string) => {
    const u = await api.post<User>('/api/auth/register', { email, displayName, password });
    await refreshCsrf();
    qc.clear();
    setUser(u);
  }, [qc]);

  const logout = useCallback(async () => {
    try { await api.post('/api/auth/logout'); } finally {
      await refreshCsrf().catch(() => undefined);
      qc.clear();
      setUser(null);
    }
  }, [qc]);

  const value = useMemo(() => ({ user, loading, isAdmin: user?.role === 'Admin', login, register, logout, setUser }), [user, loading, login, register, logout]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}

// ---------- Theme ----------
type Theme = 'dark' | 'light';
const ThemeContext = createContext<{ theme: Theme; toggle: () => void }>({ theme: 'dark', toggle: () => {} });

function initialTheme(): Theme {
  try {
    const saved = localStorage.getItem('finguard.theme');
    if (saved === 'dark' || saved === 'light') return saved;
  } catch { /* storage unavailable */ }
  return window.matchMedia?.('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setTheme] = useState<Theme>(initialTheme);
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    try { localStorage.setItem('finguard.theme', theme); } catch { /* ignore */ }
  }, [theme]);
  const toggle = useCallback(() => setTheme((t) => (t === 'dark' ? 'light' : 'dark')), []);
  return <ThemeContext.Provider value={{ theme, toggle }}>{children}</ThemeContext.Provider>;
}

export const useTheme = () => useContext(ThemeContext);
