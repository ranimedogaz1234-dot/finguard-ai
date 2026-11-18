# Security design

Controls are mapped to the OWASP Top 10 (2021). The **Verified by** column names the automated tests that cover
each control; anything not listed there has not been tested automatically.

| Risk | Control | Where | Verified by |
|---|---|---|---|
| A01 Broken access control | Deny-by-default fallback policy; role checks (`Admin`, `Analyst`) on the server | `Program.cs`, controllers | `ProtectedEndpoints_RequireAuthentication`, `AdminOnlyReads_AreForbiddenForAnalysts`, `ArchivingSuppliers_RequiresAdmin` |
| A01 Object-level access | Alerts assigned to another analyst cannot be modified; users cannot change their own role | `AlertService.CanEdit`, `UserService` | `AlertAssignedToAnotherAnalyst_CannotBeChanged` |
| A01 Business-logic abuse | Segregation of duties (submitter ≠ approver); admin approval ≥ threshold, for High/Critical suppliers and to override a Reject; only admins confirm fraud or record payments | `InvoiceService.Permissions`, `AlertWorkflow` | `SegregationOfDuties_SubmitterCannotApprove`, `HighValueInvoice_NeedsAdmin...`, `AnalystCannotConfirmFraud_AdminCan` |
| A01 Payment redirection | Only admins can change supplier bank details (denied attempts are audited); payments always go to the supplier's verified IBAN, never the invoice's; payment is blocked while High/Critical alerts on the invoice are open | `SupplierService.UpdateAsync`, `InvoiceService.MarkPaidAsync` | `Analyst_CannotChangeBankDetails_AdminCan_AndAttemptIsAudited`, `Payment_IsBlockedWhileHighAlertsAreOpen_AndAlwaysGoesToTheVerifiedAccount` |
| A01 Privilege escalation | Self-registration always creates Analysts; only `Auth:BootstrapAdminEmail` can become the first admin; users cannot change their own role; last active admin cannot be removed | `AuthService.RegisterAsync`, `UserService` | `FirstRegistration_OnEmptySystem_IsAnalyst...`, `OnlyConfiguredOwner_BecomesFirstAdmin` |
| A01 CSRF | Antiforgery token required on every unsafe request (`X-XSRF-TOKEN`), plus `SameSite=Strict` cookies | `ApiAntiforgeryFilter` | `UnsafeRequest_WithoutCsrfToken_IsRejected`, `UnsafeRequest_WithTokenForAnotherSession_IsRejected`, frontend `LoginPage` test |
| A02 Cryptographic failures | PBKDF2 password hashing (ASP.NET Core Identity hasher, automatic rehash); reset tokens: 256-bit random, stored as SHA-256, single-use, 30-minute expiry; HSTS + HTTPS redirect outside Development | `AuthService` | `PasswordReset_WorksOnce_AndInvalidatesExistingSessions` |
| A03 Injection | EF Core parameterised queries only (no raw SQL); DTO validation attributes; all report output HTML-encoded; assistant output rendered without `innerHTML` | services, `ReportBuilder`, `RichText` | `InjectionLikeSearch_IsHandledAsData`, `Report_EscapesUserContent`, `RichText` component test |
| A04 Insecure design | Rule decisions are recommendations; humans approve; alerts are "suspicious" until an admin confirms | domain | domain tests |
| A05 Misconfiguration | CSP (`script-src 'self'`, `frame-ancestors 'none'`), `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy`, COOP, `Cache-Control: no-store` on the API; CORS restricted to configured origins; OpenAPI only in Development unless enabled | `SecurityHeadersMiddleware`, `Program.cs` | `SecurityHeaders_ArePresent` |
| A05 Error handling | Central `IExceptionHandler` returns RFC 7807 problem details with a trace ID; stack traces never sent | `GlobalExceptionHandler` | `Errors_DoNotLeakInternals` |
| A07 Authentication failures | Generic login errors; equal-time hashing for unknown accounts; lockout after 5 failures for 15 min; rate limits on auth endpoints (10/min/IP), assistant, uploads, and a global limit; 12+ character passwords with deny-list; session invalidated on password change or reset, role change and deactivation (security stamp checked on every request) | `AuthService`, rate limiter | `Login_WithWrongPassword_IsGeneric_AndLocksOut...`, `Logout_EndsSession`, `PasswordReset_...` |
| A07 Session management | `HttpOnly`, `SameSite=Strict`, `Secure` (production) cookie; 8 h sliding expiry plus a 12 h absolute lifetime that claim refreshes cannot extend | `Program.cs`, `AuthService.ValidatePrincipalAsync` | `SessionCookie_IsHttpOnlyAndSameSiteStrict`, `Sessions_ExpireAfterTheAbsoluteLifetime` |
| A04 Resource limits | JSON bodies capped at 256 KB (413), uploads at 6 MB; search terms capped at 100 chars; PDF parsing limited to 10 pages and a 10 s time budget | `RequestSizeLimitMiddleware`, `Paging.Term`, `PdfExtractionService` | `OversizedJsonBody_IsRejectedWith413` |
| A05 Information exposure | Anonymous `/api/system/info` reveals no business settings (approval threshold moved to authenticated `/api/system/settings`); analysts' activity feed excludes sign-in and user-admin events; `Server` header removed; `Cross-Origin-Resource-Policy: same-origin` | `SystemController`, `DashboardService`, `Program.cs` | `AnonymousInfo_DoesNotRevealTheApprovalThreshold`, `AnalystActivityFeed_ExcludesOtherUsersSignInEvents`, `SecurityHeaders_IncludeCrossOriginResourcePolicy` |
| AI / LLM | API key used only server-side; the external model receives financial facts without supplier contact details; facts are delimited and declared as data (prompt-injection guidance); the model has no tools and cannot change data; live answers labelled AI-generated; without a key, a clearly labelled local rules mode | `ExternalAssistantEngine`, `Assistant.tsx` | `ExternalModel_GetsNoContactDetails_AndTheKeyNeverReachesTheClient`, `Assistant_RunsInLabelledLocalMode...` |
| Misconfiguration | Demo seeding refused in Production; the development mail outbox answers only in Development and only to loopback requests | `Program.cs`, `SystemController.Outbox` | `DemoSeed_IsRefusedInProduction`, `DevOutbox_IsNotAvailableOutsideDevelopment` |
| A08 File upload | PDF only: extension, declared content type **and** `%PDF-` signature checked; 5 MB limit; random server-side file names outside the web root; display names sanitised; downloads served as attachments | `FileStorage` | `Upload_RejectsNonPdf_AcceptsPdf_AndDownloads` |
| A09 Logging & monitoring | Audit log for sign-ins, failures, lockouts, approvals/rejections/overrides, payments, bank-detail changes, alert decisions, role changes, PDF access, assistant use; structured JSON logs outside Development; secrets and reset links never logged | `AuditLog`, `Program.cs` | dashboard/audit tests |
| A06 Vulnerable components | `dotnet list package --vulnerable` and `npm audit` (production **and** development dependencies, high severity and above) fail CI. Vitest was upgraded from 3.2 to 5.1 after `npm audit` reported critical `tinypool` advisories (GHSA-5gmw-xhrv-c9v3, GHSA-85c8-ppgw-ccpr) and a moderate `@vitest/mocker` advisory in the test tooling; production dependencies had none | `ci.yml`, `web/package.json` | CI `security` job |
| Secrets | No secrets in the repository; API keys via user-secrets or environment variables; AI key used only server-side | `.env.example` | review |
| Data exposure | IBANs masked in every response and report | DTO mapping | `PaymentToUnknownAccount_RaisesPaymentDetailsMismatch` |
| Open redirect | Post-login `next` parameter restricted to same-site paths | `App.tsx` | review |

## Residual risks and limitations

Automated tests show that these controls work as designed. They do not prove the application is free of vulnerabilities. Known gaps:

* **No multi-factor authentication.** Password plus lockout is the only barrier.
* **Lockout can be abused for denial of service:** five wrong passwords lock any account for 15 minutes.
* **Registration reveals whether an email exists** ("account already exists"). Login and password reset don't.
* **Rate limiting is in-memory**, so it doesn't hold across several instances.
* **Uploaded PDFs are not malware-scanned.** They are stored outside the web root and only served as downloads, but production use should add scanning. PDF parsing runs in-process; the time budget stops the request, not the parsing thread.
* **Prompt injection:** supplier-controlled text (for example line descriptions) reaches a live model. The model has no tools and can't change data, but it could be manipulated into a misleading answer. Answers are labelled and should be checked against the records.
* **Single-tenant authorization:** every signed-in user can read all suppliers, invoices and alerts. That's intended for one finance team, but not for multi-tenant use.
* **Development-only conveniences:** the mail outbox and the demo-account password are only available in Development. Never expose a Development instance publicly.
* **Dependencies:** `npm audit` and `dotnet list package --vulnerable` run in CI, but new advisories appear over time.
* **No independent penetration test** has been performed.
