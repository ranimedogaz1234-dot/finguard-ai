import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import type { ReactNode } from 'react';
import { AuthProvider, ThemeProvider } from '../auth';
import { ToastProvider } from '../components/ui';
import { LoginPage, RegisterPage } from '../pages/Auth';

type Handler = (url: string, init?: RequestInit) => { status: number; body?: unknown };

function mockFetch(handler: Handler) {
  const fn = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const { status, body } = handler(String(input), init);
    return new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
  });
  vi.stubGlobal('fetch', fn);
  return fn;
}

function wrap(ui: ReactNode) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <ThemeProvider>
      <QueryClientProvider client={qc}>
        <MemoryRouter>
          <AuthProvider><ToastProvider>{ui}</ToastProvider></AuthProvider>
        </MemoryRouter>
      </QueryClientProvider>
    </ThemeProvider>,
  );
}

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', { value: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }) });
});
afterEach(() => vi.unstubAllGlobals());

describe('LoginPage', () => {
  it('sends credentials with the CSRF header and shows the server error', async () => {
    const fetchMock = mockFetch((url, init) => {
      if (url === '/api/auth/me') return { status: 401, body: { title: 'Unauthorized' } };
      if (url === '/api/auth/csrf') return { status: 200, body: { token: 'tok-123' } };
      if (url === '/api/system/info') return { status: 200, body: { demoData: false, environment: 'Production' } };
      if (url === '/api/auth/login' && init?.method === 'POST') return { status: 401, body: { title: 'Invalid email or password.', code: 'invalid_credentials' } };
      return { status: 404 };
    });
    wrap(<LoginPage />);
    await userEvent.type(screen.getByLabelText('Email'), 'someone@example.test');
    await userEvent.type(screen.getByLabelText('Password'), 'wrong-password-123');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid email or password.');
    const loginCall = fetchMock.mock.calls.find(([u]) => String(u) === '/api/auth/login')!;
    const headers = loginCall[1]!.headers as Record<string, string>;
    expect(headers['X-XSRF-TOKEN']).toBe('tok-123');
    expect(JSON.parse(String(loginCall[1]!.body))).toEqual({ email: 'someone@example.test', password: 'wrong-password-123' });
  });

  it('keeps the submit button disabled until both fields are filled', async () => {
    mockFetch((url) => (url === '/api/auth/me' ? { status: 401 } : url === '/api/system/info' ? { status: 200, body: { demoData: false } } : { status: 404 }));
    wrap(<LoginPage />);
    const button = screen.getByRole('button', { name: 'Sign in' });
    expect(button).toBeDisabled();
    await userEvent.type(screen.getByLabelText('Email'), 'a@b.test');
    expect(button).toBeDisabled();
    await userEvent.type(screen.getByLabelText('Password'), 'x');
    expect(button).toBeEnabled();
  });
});

describe('RegisterPage', () => {
  it('validates password length and confirmation on the client', async () => {
    mockFetch((url) => (url === '/api/auth/me' ? { status: 401 } : { status: 404 }));
    wrap(<RegisterPage />);
    await userEvent.type(screen.getByLabelText('Password'), 'short');
    expect(await screen.findByText('Use at least 12 characters.')).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Confirm password'), 'different');
    expect(screen.getByText('Passwords do not match.')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Create account' })).toBeDisabled());
  });
});
