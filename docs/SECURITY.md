# Security design

Controls are mapped to the OWASP Top 10 (2021). The **Verified by** column names the automated tests that cover
each control; anything not listed there has not been tested automatically.

| Risk | Control | Where | Verified by |
|---|---|---|---|
| A01 Broken access control | Deny-by-default fallback policy; role checks (`Admin`, `Analyst`) on the server | `Program.cs`, controllers | `ProtectedEndpoints_RequireAuthentication`, `AdminOnlyReads_AreForbiddenForAnalysts`, `ArchivingSuppliers_RequiresAdmin` |
| A01 Object-level access | Alerts assigned to another analyst cannot be modified; users cannot change their own role | `AlertService.CanEdit`, `UserService` | `AlertAssignedToAnotherAnalyst_CannotBeChanged` |
| A01 Business-logic abuse | Segregation of duties (submitter ≠ approver); admin approval ≥ threshold, for High/Critical suppliers and to override a Reject; only admins confirm fraud or record payments | `InvoiceService.Permissions`, `AlertWorkflow` | `SegregationOfDuties_SubmitterCannotApprove`, `HighValueInvoice_NeedsAdmin...`, `AnalystCannotConfirmFraud_AdminCan` |
| A01 CSRF | Antiforgery token required on every unsafe request (`X-XSRF-TOKEN`), plus `SameSite=Strict` cookies | `ApiAntiforgeryFilter` | `UnsafeRequest_WithoutCsrfToken_IsRejected`, `UnsafeRequest_WithTokenForAnotherSession_IsRejected`, frontend `LoginPage` test |
| A02 Cryptographic failures | PBKDF2 password hashing (ASP.NET Core Identity hasher, automatic rehash); reset tokens: 256-bit random, stored as SHA-256, single-use, 30-minute expiry; HSTS + HTTPS redirect outside Development | `AuthService` | `PasswordReset_WorksOnce_AndInvalidatesExistingSessions` |
| A03 Injection | EF Core parameterised queries only (no raw SQL); DTO validation attributes; all report output HTML-encoded; assistant output rendered without `innerHTML` | services, `ReportBuilder`, `RichText` | `InjectionLikeSearch_IsHandledAsData`, `Report_EscapesUserContent`, `RichText` component test |
| A04 Insecure design | Rule decisions are recommendations; humans approve; alerts are "suspicious" until an admin confirms | domain | domain tests |
| A05 Misconfiguration | CSP (`script-src 'self'`, `frame-ancestors 'none'`), `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy`, COOP, `Cache-Control: no-store` on the API; CORS restricted to configured origins; OpenAPI only in Development unless enabled | `SecurityHeadersMiddleware`, `Program.cs` | `SecurityHeaders_ArePresent` |
| A05 Error handling | Central `IExceptionHandler` returns RFC 7807 problem details with a trace ID; stack traces never sent | `GlobalExceptionHandler` | `Errors_DoNotLeakInternals` |
| A07 Authentication failures | Generic login errors; equal-time hashing for unknown accounts; lockout after 5 failures for 15 min; rate limits on auth endpoints (10/min/IP), assistant, uploads, and a global limit; 12+ character passwords with deny-list; session invalidated on password change or reset, role change and deactivation (security stamp checked on every request) | `AuthService`, rate limiter | `Login_WithWrongPassword_IsGeneric_AndLocksOut...`, `Logout_EndsSession`, `PasswordReset_...` |
| A07 Session management | `HttpOnly`, `SameSite=Strict`, `Secure` (production) cookie; 8 h sliding expiry | `Program.cs` | `SessionCookie_IsHttpOnlyAndSameSiteStrict` |
| A08 File upload | PDF only: extension, declared content type **and** `%PDF-` signature checked; 5 MB limit; random server-side file names outside the web root; display names sanitised; downloads served as attachments | `FileStorage` | `Upload_RejectsNonPdf_AcceptsPdf_AndDownloads` |
| A09 Logging & monitoring | Audit log for sign-ins, failures, lockouts, approvals/rejections/overrides, payments, bank-detail changes, alert decisions, role changes, PDF access, assistant use; structured JSON logs outside Development; secrets and reset links never logged | `AuditLog`, `Program.cs` | dashboard/audit tests |
| Secrets | No secrets in the repository; API keys via user-secrets or environment variables; AI key used only server-side | `.env.example` | review |
| Data exposure | IBANs masked in every response and report | DTO mapping | `PaymentToUnknownAccount_RaisesPaymentDetailsMismatch` |
| Open redirect | Post-login `next` parameter restricted to same-site paths | `App.tsx` | review |

## Known limitations

* The development mail outbox (`/api/system/dev-outbox`) exposes reset links to anyone who can reach a
  **Development** instance. It is disabled in every other environment.
* No multi-factor authentication yet.
* Rate limiting is in-memory (single instance).
* The demo-account password is committed in `appsettings.Development.json`. It protects only fictional
  local demo data and is never used outside Development.
* Uploaded PDFs are not scanned for malware. Production use should add a scanning service.
