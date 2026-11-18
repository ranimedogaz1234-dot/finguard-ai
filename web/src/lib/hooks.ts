import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';

/** List state (search, filters, sort, page) kept in the URL so it survives reloads and back/forward. */
export function useListState<T extends Record<string, string>>(defaults: T) {
  const [params, setParams] = useSearchParams();
  const state = useMemo(() => {
    const s = { ...defaults } as Record<string, string>;
    for (const k of Object.keys(defaults)) s[k] = params.get(k) ?? defaults[k];
    return s as T;
  }, [params]);

  const set = useCallback((patch: Partial<T>, resetPage = true) => {
    setParams((prev) => {
      const next = new URLSearchParams(prev);
      for (const [k, v] of Object.entries(patch)) {
        if (v === undefined || v === '' || v === defaults[k]) next.delete(k);
        else next.set(k, String(v));
      }
      if (resetPage && !('page' in patch)) next.delete('page');
      return next;
    }, { replace: true });
  }, [setParams]);

  const toggleSort = useCallback((key: string) => {
    if (state.sort === key) set({ dir: state.dir === 'asc' ? 'desc' : 'asc' } as unknown as Partial<T>);
    else set({ sort: key, dir: 'desc' } as unknown as Partial<T>);
  }, [state.sort, state.dir, set]);

  return { state, set, toggleSort };
}

export function useDebounced<T>(value: T, ms = 300): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}
