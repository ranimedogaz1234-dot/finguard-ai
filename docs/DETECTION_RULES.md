# Invoice verification & fraud detection rules

All rules are deterministic and covered by unit tests. Every finding carries a stable code and a plain-language
explanation that the UI and reports show verbatim. **Detections indicate suspicious patterns. They are never
presented as confirmed fraud** unless an administrator moves the alert to `ConfirmedFraud` after investigation.

## 1. Verification rules: [`InvoiceVerifier`](../src/FinGuard.Domain/InvoiceVerifier.cs)

| Code | Severity | Rule |
|---|---|---|
| `DUPLICATE` | Critical | Same supplier already submitted the same invoice number. Numbers are normalised group by group (see below) |
| `POSSIBLE_DUPLICATE` | Warning | Same supplier, same total and same date, different number |
| `NO_LINES` / `INVALID_LINE` | Critical | No line items, or a non-positive quantity or negative price |
| `TOTAL_MISMATCH` | Critical | Declared total ≠ sum of line totals (±0.01) |
| `NO_PO` | Warning | No purchase order referenced, or the referenced PO does not exist |
| `SUPPLIER_MISMATCH` | Critical | PO belongs to a different supplier |
| `CURRENCY_MISMATCH` | Critical | Invoice currency ≠ PO currency |
| `OVER_PO` | Critical | Total exceeds PO total by more than 5% |
| `SLIGHT_OVER_PO` | Warning | Total above PO total, within 5% |
| `UNORDERED_ITEM` | Warning | Line not on the PO |
| `PRICE_INCREASE` | Warning | Unit price above PO unit price |
| `QUANTITY_EXCEEDS_PO` | Warning | Quantity above ordered quantity |

**Invoice-number normalisation.** A number is split into letter and digit groups at separators, whitespace and
letter/digit boundaries. Letters are upper-cased and leading zeros are removed from **every** digit group, while
group boundaries are kept:

| Same invoice | Different invoices |
|---|---|
| `AOF-2026-0458` = `aof 2026 458` = `AOF2026/00458` | `INV-12-3` ≠ `INV-1-23` |
| `INV-0042` = `inv 42` = `INV42` | `INV-100` ≠ `INV-10` |
| `FAC.2026.000123` = `fac 2026 123` | `AOF-2026-0458` ≠ `AOF-2026-0459` |

**Decision:** `Reject` if any of `DUPLICATE`, `TOTAL_MISMATCH`, `NO_LINES` or `INVALID_LINE` fires; otherwise
`ManualReview` if any finding exists; otherwise `AutoApprove`. Verification points: Critical 40, Warning 15, capped
at 100. The decision is a **recommendation**: a human approves or rejects.

Only invoices submitted *before* the one being verified count as history, so the original of a duplicate stays clean.

## 2. Fraud rules: [`FraudDetector`](../src/FinGuard.Domain/FraudDetector.cs)

| Code | Severity | Rule |
|---|---|---|
| `PAYMENT_DETAILS_MISMATCH` | Critical | The IBAN on the invoice differs from the verified IBAN on the supplier record (invoice-redirection pattern) |
| `INVALID_IBAN` | Medium | The invoice IBAN fails the ISO 13616 mod-97 checksum |
| `BANK_DETAILS_RECENTLY_CHANGED` | High | The supplier's bank details changed 0–30 days before the invoice date |
| `AMOUNT_OUTLIER` | Medium (≥2×) / High (≥3×) | Amount vs. median of ≥ 4 previous same-currency invoices from the supplier |
| `JUST_BELOW_THRESHOLD` | Medium | Amount within 5% below the administrator-approval threshold |
| `SPLIT_INVOICING` | High | ≥ 2 sub-threshold invoices within 7 days whose sum reaches the threshold |
| `ROUND_AMOUNT` | Low | ≥ 5,000 and an exact multiple of 1,000 (weak signal on its own) |
| `FUTURE_DATED` | Medium | Invoice date after today |
| `DUPLICATE_PAYMENT_RISK` | High | A payment of the same amount was made to this supplier within 60 days for a different invoice |

Verification findings `DUPLICATE`, `POSSIBLE_DUPLICATE` and `OVER_PO` also raise alerts (`DUPLICATE_INVOICE`,
`POSSIBLE_DUPLICATE`, `OVER_PO`). The same rule never raises two alerts for one invoice.

IBANs are masked (`FR76 •••• 0189`) in every explanation, API response and report.

## 3. Alert workflow: [`AlertWorkflow`](../src/FinGuard.Domain/FraudDetector.cs)

```
Open ──► Investigating ──► Escalated ──► (admin) ConfirmedFraud
  │            │    ▲           │
  │            │    └───────────┘
  └──► Dismissed ◄──┘──► Resolved          (admin can reopen any closed alert)
```

* Analysts: Open→Investigating/Dismissed, Investigating→Escalated/Dismissed/Resolved, Escalated→Investigating.
* Administrators additionally: Investigating/Escalated→ConfirmedFraud, Escalated→Dismissed/Resolved, closed→Investigating.
* Closing an alert (Dismissed, Resolved, ConfirmedFraud) requires a note.
* Object-level rule: an alert assigned to someone can only be changed by that person or an administrator.
  Starting an investigation on an unassigned alert assigns it to you.

## 4. Investigation priority (assistant & dashboard)

`priority = severity weight (Critical 4, High 3, Medium 2, Low 1) × log10(exposure + 10) + supplier risk / 25`

Deliberately simple so analysts can reason about the order.
