import { expect, test } from '@playwright/test';

/** Captures the README screenshots from the running app (fictional demo data). */
const OUT = '../docs/screenshots';
const PASSWORD = 'FinGuard-Demo-2026!';

// The app follows the OS theme on first visit; screenshots start from a known (dark) preference.
test.use({ colorScheme: 'dark' });

test.beforeEach(async ({ page }) => {
  await page.goto('/login');
  await page.getByLabel('Email').fill('admin@finguard.demo');
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { name: 'Financial overview' })).toBeVisible();
  await page.waitForLoadState('networkidle');
});

test('desktop screenshots', async ({ page }, info) => {
  test.skip(info.project.name !== 'desktop', 'desktop only');
  await page.waitForTimeout(800); // let chart animations settle
  await page.screenshot({ path: `${OUT}/dashboard-dark.png` });

  await page.getByRole('button', { name: /Switch to light theme/ }).click();
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${OUT}/dashboard-light.png` });
  await page.getByRole('button', { name: /Switch to dark theme/ }).click();

  await page.goto('/suppliers?q=crestline&status=all');
  await page.getByRole('link', { name: 'Crestline Trading LLC' }).click();
  await expect(page.getByRole('img', { name: /^Risk score \d+ of 100/ })).toBeVisible(); // the gauge, not the history chart
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${OUT}/supplier.png` });

  // Seeded scenarios only (never records created by other E2E tests): a resubmitted duplicate invoice…
  await page.goto('/invoices?q=AOF%202026%20458');
  await page.getByRole('link', { name: 'AOF 2026 458' }).click();
  await expect(page.getByRole('heading', { name: /Invoice AOF 2026 458/ })).toBeVisible();
  await page.waitForTimeout(400);
  await page.screenshot({ path: `${OUT}/invoice.png` });

  // …and the payment-redirection alert raised for Baltic Freight Partners.
  await page.goto('/alerts?q=Baltic&severity=Critical&status=all');
  await page.getByRole('link', { name: /Payment details differ from supplier record/ }).first().click();
  await expect(page.getByText('Why this was flagged')).toBeVisible();
  await page.waitForTimeout(400);
  await page.screenshot({ path: `${OUT}/alert.png` });

  // The assistant answering a grounded question about a seeded supplier (labelled local mode).
  await page.goto('/assistant');
  await page.getByRole('button', { name: 'Why is Crestline Trading LLC high risk?' }).click();
  await expect(page.getByText('Local demonstration mode: this answer', { exact: false })).toBeVisible();
  await page.evaluate(() => window.scrollTo(0, 0)); // clicking the chip scrolled the page; capture from the top
  await page.waitForTimeout(400);
  await page.screenshot({ path: `${OUT}/assistant.png` });
});

test('mobile screenshot', async ({ page }, info) => {
  test.skip(info.project.name !== 'mobile', 'mobile only');
  await page.waitForTimeout(800);
  await page.screenshot({ path: `${OUT}/mobile.png` });
});
