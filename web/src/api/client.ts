/**
 * Thin fetch wrapper for the FinGuard API.
 * - Session: HttpOnly SameSite=Strict cookie (never readable from JS).
 * - CSRF: token from GET /api/auth/csrf, sent as X-XSRF-TOKEN on unsafe methods, refreshed after login/logout
 *   and retried once if the server reports a stale token.
 */
export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
    public code?: string,
    public fieldErrors?: Record<string, string[]>,
  ) {
    super(message);
  }
}

let csrfToken: string | null = null;
let onUnauthorized: (() => void) | null = null;

export function setUnauthorizedHandler(fn: () => void) {
  onUnauthorized = fn;
}

export async function refreshCsrf(): Promise<void> {
  const res = await fetch('/api/auth/csrf', { credentials: 'same-origin' });
  if (!res.ok) throw new ApiError('Could not reach the FinGuard API.', res.status);
  csrfToken = ((await res.json()) as { token: string }).token;
}

async function toError(res: Response): Promise<ApiError> {
  let body: { title?: string; code?: string; errors?: Record<string, string[]> } = {};
  try {
    body = await res.json();
  } catch {
    /* not JSON */
  }
  if (res.status === 429) return new ApiError('Too many requests. Please wait a moment and try again.', 429, 'rate_limited');
  const fieldErrors = body.errors;
  const first = fieldErrors ? Object.values(fieldErrors).flat()[0] : undefined;
  // The API marks antiforgery failures with code "csrf_invalid"; map it to the client's retry signal.
  const code = body.code === 'csrf_invalid' ? 'csrf' : body.code;
  const message = first ?? body.title ?? `Request failed (${res.status}).`;
  return new ApiError(message, res.status, code, fieldErrors);
}

type Method = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

async function request<T>(method: Method, url: string, body?: unknown, retried = false): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  const isForm = body instanceof FormData;
  if (method !== 'GET') {
    if (!csrfToken) await refreshCsrf();
    headers['X-XSRF-TOKEN'] = csrfToken!;
    if (body !== undefined && !isForm) headers['Content-Type'] = 'application/json';
  }
  const res = await fetch(url, {
    method,
    headers,
    credentials: 'same-origin',
    body: body === undefined ? undefined : isForm ? (body as FormData) : JSON.stringify(body),
  });
  if (res.ok) {
    if (res.status === 204 || res.headers.get('content-length') === '0') return undefined as T;
    const type = res.headers.get('content-type') ?? '';
    return (type.includes('json') ? await res.json() : await res.text()) as T;
  }
  const err = await toError(res);
  if (err.code === 'csrf' && !retried && method !== 'GET') {
    await refreshCsrf();
    return request<T>(method, url, body, true);
  }
  if (res.status === 401 && !url.startsWith('/api/auth/')) onUnauthorized?.();
  throw err;
}

export const api = {
  get: <T>(url: string) => request<T>('GET', url),
  post: <T>(url: string, body?: unknown) => request<T>('POST', url, body ?? {}),
  put: <T>(url: string, body: unknown) => request<T>('PUT', url, body),
  patch: <T>(url: string, body: unknown) => request<T>('PATCH', url, body),
  upload: <T>(url: string, file: File) => {
    const fd = new FormData();
    fd.append('file', file);
    return request<T>('POST', url, fd);
  },
};

/** Builds a query string, skipping empty values. */
export function qs(params: Record<string, string | number | boolean | undefined | null | string[]>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === '') continue;
    if (Array.isArray(v)) v.forEach((x) => p.append(k, x));
    else p.set(k, String(v));
  }
  const s = p.toString();
  return s ? `?${s}` : '';
}
