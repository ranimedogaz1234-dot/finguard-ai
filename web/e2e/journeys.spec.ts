import { expect, test, type Page } from '@playwright/test';

const PASSWORD = 'FinGuard-Demo-2026!';

async function login(page: Page, email: string) {
  await page.goto('/login');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { name: 'Financial overview' })).toBeVisible();
}

async function openNav(page: Page) {
  const menu = page.getByRole('button', { name: 'Open navigation' });
  if (await menu.isVisible()) await menu.click();
}

test('unauthenticated users are redirected to sign in', async ({ page }) => {
  await page.goto('/suppliers');
  await expect(page).toHaveURL(/\/login\?next=%2Fsuppliers/);
});

test('wrong password shows a generic error', async ({ page }) => {
  await page.goto('/login');
  await page.getByLabel('Email').fill('analyst@finguard.demo');
  await page.getByLabel('Password').fill('not-the-password');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('alert')).toContainText('Invalid email or password');
});

test('dashboard shows computed metrics and demo labelling', async ({ page }) => {
  await login(page, 'analyst@finguard.demo');
  await expect(page.getByText('fictional demonstration data', { exact: true })).toBeVisible();
  const processed = page.getByRole('link', { name: 'Invoices processed' });
  await expect(processed).toBeVisible();
  await expect(processed.locator('.kpi-value')).toHaveText(/^\d[\d,]*$/);
  await expect(page.getByRole('heading', { name: 'Investigation priorities' })).toBeVisible();
});

test('supplier search, detail and explainable risk score', async ({ page }) => {
  await login(page, 'analyst@finguard.demo');
  await openNav(page);
  await page.getByRole('link', { name: 'Suppliers', exact: true }).click(); // KPI link 'High-risk suppliers' also contains the word
  await page.getByRole('searchbox', { name: /Search name/ }).fill('Crestline');
  await page.getByRole('link', { name: 'Crestline Trading LLC' }).click();
  await expect(page.getByRole('heading', { name: /Crestline Trading LLC/ })).toBeVisible();
  await expect(page.getByText('Confirmed fraud cases')).toBeVisible();
  await expect(page.getByRole('img', { name: /Risk score \d+ of 100/ })).toBeVisible();
});

test('submitting a duplicate invoice is rejected by the rules and raises an alert', async ({ page }) => {
  await login(page, 'analyst@finguard.demo');
  const number = `E2E-${Date.now()}`;
  for (let i = 0; i < 2; i++) {
    await page.goto('/invoices/new');
    const supplier = page.getByLabel('Supplier', { exact: true });
    const value = await supplier.locator('option', { hasText: 'Northwind Office Supplies' }).getAttribute('value');
    await supplier.selectOption(value!);
    await page.getByLabel('Invoice number').fill(number);
    await page.getByLabel('Line 1 description').fill('Toner cartridge');
    await page.getByLabel('Line 1 quantity').fill('3');
    await page.getByLabel('Line 1 unit price').fill('92');
    await page.getByRole('button', { name: 'Use as declared total' }).click();
    await page.getByRole('button', { name: 'Submit & verify' }).click();
    await expect(page.getByRole('heading', { name: `Invoice ${number}` })).toBeVisible();
  }
  await expect(page.getByText('DUPLICATE', { exact: true })).toBeVisible();
  await expect(page.getByText(/Duplicate invoice number/)).toBeVisible();
  // Segregation of duties: the submitter cannot approve.
  await expect(page.getByRole('button', { name: 'Approve' })).toBeDisabled();
});

test('analyst investigates an alert', async ({ page }) => {
  await login(page, 'analyst@finguard.demo');
  await page.goto('/alerts');
  // Pick an alert that is still Open (seeded Investigating alerts may belong to another analyst).
  await page.locator('tbody tr').filter({ has: page.locator('.badge', { hasText: /^Open$/ }) }).first().click();
  await expect(page.getByText('Suspicious, not confirmed.')).toBeVisible();
  await page.getByRole('button', { name: 'Start investigation' }).click();
  await expect(page.getByText('Alert moved to Investigating')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Confirm fraud' })).toHaveCount(0); // admins only
});

test('assistant answers in labelled local mode with grounded data', async ({ page }) => {
  await login(page, 'analyst@finguard.demo');
  await page.goto('/assistant');
  await expect(page.getByText('Local demonstration mode').first()).toBeVisible();
  await page.getByRole('button', { name: 'What should I investigate first?' }).click();
  await expect(page.getByText(/Recommended investigation order/)).toBeVisible();
});

test('role-based navigation: analysts do not see administration', async ({ page }) => {
  await login(page, 'analyst@finguard.demo');
  await openNav(page);
  await expect(page.getByRole('link', { name: 'Users', exact: true })).toHaveCount(0);
  await page.goto('/admin/users');
  await expect(page.getByText('You don’t have access to this page')).toBeVisible();
});

test('admin can manage users and read the audit log', async ({ page }) => {
  await login(page, 'admin@finguard.demo');
  await page.goto('/admin/users');
  await expect(page.getByText('analyst@finguard.demo', { exact: true })).toBeVisible(); // lina.analyst@… also contains it
  await page.goto('/admin/audit');
  await expect(page.getByText('auth.login').first()).toBeVisible();
});

test('theme toggle switches between dark and light', async ({ page }) => {
  await login(page, 'analyst@finguard.demo');
  const html = page.locator('html');
  const before = await html.getAttribute('data-theme');
  await page.getByRole('button', { name: /Switch to (light|dark) theme/ }).click();
  await expect(html).not.toHaveAttribute('data-theme', before!);
});

test('every main page fits the viewport without horizontal scrolling', async ({ page }) => {
  await login(page, 'analyst@finguard.demo');
  const first = async (url: string) => ((await (await page.request.get(url)).json()) as { items: { id: string }[] }).items[0].id;
  const paths = [
    '/', '/suppliers', '/invoices', '/invoices/new', '/alerts', '/assistant', '/settings',
    `/suppliers/${await first('/api/suppliers?pageSize=1')}`,
    `/invoices/${await first('/api/invoices?pageSize=1&decision=flagged')}`,
    `/alerts/${await first('/api/alerts?pageSize=1&status=all')}`,
  ];
  for (const path of paths) {
    await page.goto(path);
    await page.waitForLoadState('networkidle');
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow, `horizontal overflow on ${path}`).toBeLessThanOrEqual(1);
  }
});

test('the navigation menu button only appears on small screens', async ({ page }, info) => {
  await login(page, 'analyst@finguard.demo');
  const menu = page.getByRole('button', { name: 'Open navigation' });
  if (info.project.name === 'mobile') await expect(menu).toBeVisible();
  else await expect(menu).toBeHidden();
});
