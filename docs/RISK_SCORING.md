# Supplier risk scoring

FinGuard's supplier risk score is a **transparent, rule-based heuristic** computed only from data stored in the platform.
It is **not** a credit rating, a sanctions screen or a verified external assessment, and the UI says so.

Implementation: [`SupplierRiskScorer`](../src/FinGuard.Domain/SupplierRiskScorer.cs). Inputs are gathered by
[`RiskService.ComputeStatsAsync`](../src/FinGuard.Api/Services/RiskAndVerification.cs).

## Formula

`score = min(100, Σ factor points)`. Each factor is capped, so every point can be attributed.

| Factor | Points | Cap | Source data |
|---|---|---|---|
| Invoice rejection rate | rejection rate × 100 | 25 | invoices rejected by a reviewer, or recommended for rejection and still pending |
| Duplicate invoice flags | 8 per flag | 20 | `DUPLICATE_INVOICE` / `POSSIBLE_DUPLICATE` alerts, excluding those dismissed as false positives |
| Open high-severity alerts | 6 per alert | 15 | High/Critical alerts not yet closed |
| Confirmed fraud cases | 40 per case | 40 | alerts an **administrator** marked `ConfirmedFraud` |
| Recent bank-detail changes | 10 per change | 15 | IBAN changes in the last 90 days |
| New supplier | 10 if < 90 days | 10 | onboarding date |
| Price variance above PO | 1.5 × average % above PO price | 15 | invoice lines vs. matching purchase-order lines (decreases count as 0) |

Any confirmed fraud case forces a score of at least 45 (High).

## Levels and recommendations

| Score | Level | Recommendation |
|---|---|---|
| 0–19 | Low | Standard processing |
| 20–44 | Medium | Monitor closely, spot-check invoices against POs |
| 45–69 | High | Administrator approval for every invoice, verify bank details by phone |
| 70–100 | Critical | Suspend payments, full manual vetting |

Analysts cannot approve invoices from High/Critical suppliers; an administrator must.

## Confidence

Small samples are flagged rather than hidden: **High** confidence with ≥ 10 invoices, **Medium** with 4–9, **Low** below 4.

## When scores are recomputed

After every invoice verification, rejection, bank-detail change and alert status change, plus on demand
("Reassess"). Each recomputation is stored as a `RiskAssessmentRecord` with its trigger and factor breakdown,
which powers the risk-history chart. The demo seed adds monthly portfolio-review snapshots.
