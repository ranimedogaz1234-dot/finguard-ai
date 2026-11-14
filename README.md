# FinGuard AI

Financial-controls platform: AI-assisted invoice verification, supplier risk scoring, and a simulated corporate expense-card module. All data is fictional; no real payments or card credentials.

## Status (honest)
| Module | State |
|---|---|
| Invoice verification rule engine (`FinGuard.Domain`) | Written, **not compiled or run** (no .NET SDK in build environment) |
| Supplier risk scorer | Written, **not compiled or run** |
| Unit tests (xUnit, 6 cases) | Written, **never executed** |
| CI workflow | Written, **never run** |
| ASP.NET API, Identity/JWT, EF Core + SQL Server, React UI, expense cards, LLM assistant, PDF extraction | **Not started** |

## Design
Domain logic is pure and framework-free so it is unit-testable; the API layer will only orchestrate it. Verification is rule-based and explainable: every decision lists discrepancies with severity. An optional LLM layer would only *phrase* explanations, never decide.

## Security principles
Secrets via user-secrets/env only (`.env.example` provided), parameterized EF queries, DTO validation, role-based authorization, OWASP ASVS as checklist.
