using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FinGuard.Api.Data;
using FinGuard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace FinGuard.Api.Tests;

[Collection("api")]
public class SecurityTests(FinGuardFactory f)
{
    [Theory]
    [InlineData("/api/dashboard")]
    [InlineData("/api/suppliers")]
    [InlineData("/api/invoices")]
    [InlineData("/api/alerts")]
    [InlineData("/api/auth/me")]
    public async Task ProtectedEndpoints_RequireAuthentication(string url)
    {
        var c = await f.ClientAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task SecurityHeaders_ArePresent()
    {
        var c = await f.ClientAsync();
        var res = await c.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("script-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task UnsafeRequest_WithoutCsrfToken_IsRejected()
    {
        var c = await f.ClientAsync();
        var res = await c.PostAsync("/api/auth/login", new { email = "admin@finguard.demo", password = FinGuardFactory.DemoPassword }, csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("csrf_invalid", (await ApiClient.ReadAsync(res)).GetProperty("code").GetString());
        Assert.False(res.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(h => h.StartsWith("finguard.session")));
    }

    [Fact]
    public async Task UnsafeRequest_WithTokenForAnotherSession_IsRejected()
    {
        var other = await f.ClientAsync();
        var stolen = (await other.GetJsonAsync("/api/auth/csrf")).GetProperty("token").GetString()!;
        var c = await f.ClientAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        { Content = System.Net.Http.Json.JsonContent.Create(new { email = "admin@finguard.demo", password = FinGuardFactory.DemoPassword }) };
        req.Headers.Add("X-XSRF-TOKEN", stolen); // token does not match this client's antiforgery cookie
        var res = await c.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SafeRequests_DoNotRequireCsrfToken()
    {
        var c = await f.AdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await c.Http.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task SessionCookie_IsHttpOnlyAndSameSiteStrict()
    {
        var c = await f.ClientAsync();
        var res = await c.PostAsync("/api/auth/login", new { email = "admin@finguard.demo", password = FinGuardFactory.DemoPassword });
        var cookie = res.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("finguard.session"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownApiRoute_Returns404_NotTheSpa()
    {
        var c = await f.AdminAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task Errors_DoNotLeakInternals()
    {
        var c = await f.AdminAsync();
        var res = await c.GetAsync($"/api/invoices/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("traceId", body);
        Assert.DoesNotContain("   at ", body); // no stack trace
    }

    [Fact]
    public async Task InjectionLikeSearch_IsHandledAsData()
    {
        var c = await f.AdminAsync();
        var res = await c.GetJsonAsync("/api/suppliers?q=" + Uri.EscapeDataString("'; DROP TABLE Suppliers;--"));
        Assert.Equal(0, res.GetProperty("total").GetInt32());
        Assert.True((await c.GetJsonAsync("/api/suppliers")).GetProperty("total").GetInt32() > 10);
    }
}

[Collection("api")]
public class AuthTests(FinGuardFactory f)
{
    private static string NewEmail() => $"user{Guid.NewGuid():N}@example.test";

    [Fact]
    public async Task Register_ValidatesPassword_AndRejectsDuplicates()
    {
        var c = await f.ClientAsync();
        var email = NewEmail();
        var weak = await c.PostAsync("/api/auth/register", new { email, displayName = "Weak User", password = "password1234" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.StatusCode);

        var ok = await c.PostAsync("/api/auth/register", new { email, displayName = "New Analyst", password = "correct horse battery staple" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Analyst", (await ApiClient.ReadAsync(ok)).GetProperty("role").GetString()); // never self-assigned admin

        var c2 = await f.ClientAsync();
        var dup = await c2.PostAsync("/api/auth/register", new { email = email.ToUpperInvariant(), displayName = "Dup", password = "another long passphrase" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongPassword_IsGeneric_AndLocksOutAfterFiveFailures()
    {
        var email = NewEmail();
        var reg = await f.ClientAsync();
        await reg.PostAsync("/api/auth/register", new { email, displayName = "Lockout Test", password = "a perfectly fine passphrase" });

        var c = await f.ClientAsync();
        var unknown = await ApiClient.ReadAsync(await c.PostAsync("/api/auth/login", new { email = NewEmail(), password = "whatever-password" }));
        for (var i = 0; i < 5; i++)
        {
            var res = await c.PostAsync("/api/auth/login", new { email, password = "wrong password here" });
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            if (i == 0) Assert.Equal(unknown.GetProperty("title").GetString(), (await ApiClient.ReadAsync(res)).GetProperty("title").GetString());
        }
        var locked = await c.PostAsync("/api/auth/login", new { email, password = "a perfectly fine passphrase" });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal("locked_out", (await ApiClient.ReadAsync(locked)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Logout_EndsSession()
    {
        var c = await f.AnalystAsync();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task PasswordReset_WorksOnce_AndInvalidatesExistingSessions()
    {
        var email = NewEmail();
        var original = await f.ClientAsync();
        await original.PostAsync("/api/auth/register", new { email, displayName = "Reset Test", password = "the original passphrase" });
        await original.RefreshCsrfAsync();
        Assert.Equal(HttpStatusCode.OK, (await original.GetAsync("/api/auth/me")).StatusCode);

        var anon = await f.ClientAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsync("/api/auth/forgot-password", new { email })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsync("/api/auth/forgot-password", new { email = NewEmail() })).StatusCode); // same answer
        var mail = f.Email.Sent.Last(m => m.To == email);
        var token = Uri.UnescapeDataString(System.Text.RegularExpressions.Regex.Match(mail.Body, "token=([^\\s]+)").Groups[1].Value);

        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsync("/api/auth/reset-password", new { token, newPassword = "a brand new passphrase" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await anon.PostAsync("/api/auth/reset-password", new { token, newPassword = "yet another passphrase" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await original.GetAsync("/api/auth/me")).StatusCode); // old session revoked
        var fresh = await f.ClientAsync(email, "a brand new passphrase");
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/auth/me")).StatusCode);
    }
}

[Collection("api")]
public class AuthorizationTests(FinGuardFactory f)
{
    [Theory]
    [InlineData("/api/users")]
    [InlineData("/api/audit")]
    public async Task AdminOnlyReads_AreForbiddenForAnalysts(string url)
    {
        var analyst = await f.AnalystAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.GetAsync(url)).StatusCode);
        var admin = await f.AdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task ArchivingSuppliers_RequiresAdmin()
    {
        var admin = await f.AdminAsync();
        var created = await admin.PostAsync("/api/suppliers", NewSupplier("Archive Target " + Guid.NewGuid().ToString("N")[..6]));
        var id = (await ApiClient.ReadAsync(created)).GetProperty("id").GetGuid();

        var analyst = await f.AnalystAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsync($"/api/suppliers/{id}/archive")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/suppliers/{id}/archive")).StatusCode);
        Assert.Equal("Archived", (await admin.GetJsonAsync($"/api/suppliers/{id}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnalystCannotConfirmFraud_AdminCan()
    {
        var admin = await f.AdminAsync();
        var analyst = await f.AnalystAsync();
        var alert = await NewAlertAsync(f);
        Assert.Equal(HttpStatusCode.OK, (await analyst.PostAsync($"/api/alerts/{alert}/status", new { status = "Investigating", note = "Looking" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsync($"/api/alerts/{alert}/status", new { status = "ConfirmedFraud", note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/alerts/{alert}/status", new { status = "ConfirmedFraud", note = "Confirmed in test" })).StatusCode);
    }

    [Fact]
    public async Task AlertAssignedToAnotherAnalyst_CannotBeChanged()
    {
        var analyst = await f.AnalystAsync();
        var analyst2 = await f.Analyst2Async();
        var alert = await NewAlertAsync(f);
        Assert.Equal(HttpStatusCode.OK, (await analyst.PostAsync($"/api/alerts/{alert}/status", new { status = "Investigating" })).StatusCode); // takes ownership
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst2.PostAsync($"/api/alerts/{alert}/notes", new { note = "Not mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst2.PostAsync($"/api/alerts/{alert}/assign", new { userId = (Guid?)null })).StatusCode);
        var detail = await analyst2.GetJsonAsync($"/api/alerts/{alert}");
        Assert.False(detail.GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task ClosingAlert_RequiresNote()
    {
        var analyst = await f.AnalystAsync();
        var alert = await NewAlertAsync(f);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await analyst.PostAsync($"/api/alerts/{alert}/status", new { status = "Dismissed" })).StatusCode);
    }

    internal static object NewSupplier(string name, string? iban = null) => new
    {
        name, country = "FR", category = "Consulting", taxId = (string?)null, contactEmail = "ap@example.test", contactPhone = (string?)null,
        address = (string?)null, iban = iban ?? "FR7630006000011234567890189", currency = "EUR", annualSpend = 12000, paymentTermsDays = 30, notes = (string?)null,
    };

    /// <summary>Creates a fresh supplier and a duplicate invoice, which raises a new open alert.</summary>
    internal static async Task<Guid> NewAlertAsync(FinGuardFactory f)
    {
        var admin = await f.AdminAsync();
        var sid = (await ApiClient.ReadAsync(await admin.PostAsync("/api/suppliers", NewSupplier("Alert Source " + Guid.NewGuid().ToString("N")[..8])))).GetProperty("id").GetGuid();
        var inv = InvoiceTests.NewInvoice(sid, "DUP-1", 100);
        await admin.PostAsync("/api/invoices", inv);
        var second = await ApiClient.ReadAsync(await admin.PostAsync("/api/invoices", InvoiceTests.NewInvoice(sid, "DUP-1", 100)));
        var detail = await admin.GetJsonAsync($"/api/invoices/{second.GetProperty("id").GetGuid()}");
        return detail.GetProperty("alerts").EnumerateArray().First(a => a.GetProperty("code").GetString() == "DUPLICATE_INVOICE").GetProperty("id").GetGuid();
    }
}

[Collection("api")]
public class InvoiceTests(FinGuardFactory f)
{
    internal static object NewInvoice(Guid supplierId, string number, decimal unitPrice, decimal? declared = null, string? iban = null) => new
    {
        supplierId, number, poNumber = (string?)null, invoiceDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
        dueDate = (string?)null, currency = "EUR", declaredTotal = declared ?? unitPrice * 2, iban,
        lines = new[] { new { description = "Advisory day", quantity = 2, unitPrice } },
    };

    private async Task<Guid> NewSupplierAsync(ApiClient c) =>
        (await ApiClient.ReadAsync(await c.PostAsync("/api/suppliers", AuthorizationTests.NewSupplier("Invoice Test " + Guid.NewGuid().ToString("N")[..8])))).GetProperty("id").GetGuid();

    [Fact]
    public async Task DuplicateInvoice_IsRejectedByRules_AndRaisesCriticalAlert()
    {
        var c = await f.AnalystAsync();
        var sid = await NewSupplierAsync(c);
        var first = await ApiClient.ReadAsync(await c.PostAsync("/api/invoices", NewInvoice(sid, "INV-0077", 500)));
        var dup = await ApiClient.ReadAsync(await c.PostAsync("/api/invoices", NewInvoice(sid, "inv 77", 500)));

        var d1 = await c.GetJsonAsync($"/api/invoices/{first.GetProperty("id").GetGuid()}");
        var d2 = await c.GetJsonAsync($"/api/invoices/{dup.GetProperty("id").GetGuid()}");
        Assert.DoesNotContain(d1.GetProperty("discrepancies").EnumerateArray(), d => d.GetProperty("code").GetString() == "DUPLICATE");
        Assert.Equal("Reject", d2.GetProperty("decision").GetString());
        var alert = d2.GetProperty("alerts").EnumerateArray().Single(a => a.GetProperty("code").GetString() == "DUPLICATE_INVOICE");
        Assert.Equal("Critical", alert.GetProperty("severity").GetString());
        Assert.Equal("Open", alert.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TotalMismatch_IsDetected()
    {
        var c = await f.AnalystAsync();
        var sid = await NewSupplierAsync(c);
        var id = (await ApiClient.ReadAsync(await c.PostAsync("/api/invoices", NewInvoice(sid, "TM-1", 100, declared: 260)))).GetProperty("id").GetGuid();
        var d = await c.GetJsonAsync($"/api/invoices/{id}");
        Assert.Contains(d.GetProperty("discrepancies").EnumerateArray(), x => x.GetProperty("code").GetString() == "TOTAL_MISMATCH");
    }

    [Fact]
    public async Task PaymentToUnknownAccount_RaisesPaymentDetailsMismatch()
    {
        var c = await f.AnalystAsync();
        var sid = await NewSupplierAsync(c);
        var id = (await ApiClient.ReadAsync(await c.PostAsync("/api/invoices", NewInvoice(sid, "PD-1", 100, iban: "DE89370400440532013000")))).GetProperty("id").GetGuid();
        var d = await c.GetJsonAsync($"/api/invoices/{id}");
        Assert.Contains(d.GetProperty("alerts").EnumerateArray(), a => a.GetProperty("code").GetString() == "PAYMENT_DETAILS_MISMATCH");
        Assert.DoesNotContain("370400440532013000", d.GetRawText()); // IBAN masked in responses
    }

    [Fact]
    public async Task SegregationOfDuties_SubmitterCannotApprove()
    {
        var analyst = await f.AnalystAsync();
        var sid = await NewSupplierAsync(analyst);
        var id = (await ApiClient.ReadAsync(await analyst.PostAsync("/api/invoices", NewInvoice(sid, "SOD-1", 100)))).GetProperty("id").GetGuid();
        var d = await analyst.GetJsonAsync($"/api/invoices/{id}");
        Assert.False(d.GetProperty("permissions").GetProperty("canApprove").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsync($"/api/invoices/{id}/approve", new { note = "self" })).StatusCode);

        var analyst2 = await f.Analyst2Async();
        Assert.Equal(HttpStatusCode.OK, (await analyst2.PostAsync($"/api/invoices/{id}/approve", new { note = "Checked against contract" })).StatusCode);
    }

    [Fact]
    public async Task HighValueInvoice_NeedsAdmin_AndOnlyAdminRecordsPayment()
    {
        var analyst = await f.AnalystAsync();
        var admin = await f.AdminAsync();
        var analyst2 = await f.Analyst2Async();
        var sid = await NewSupplierAsync(admin);
        var id = (await ApiClient.ReadAsync(await analyst.PostAsync("/api/invoices", NewInvoice(sid, "BIG-1", 6000)))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst2.PostAsync($"/api/invoices/{id}/approve", new { note = "ok" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/invoices/{id}/approve", new { note = "Board-approved spend" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst2.PostAsync($"/api/invoices/{id}/mark-paid")).StatusCode);
        var paid = await ApiClient.ReadAsync(await admin.PostAsync($"/api/invoices/{id}/mark-paid"));
        Assert.Equal("Paid", paid.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Rejection_RequiresReason()
    {
        var c = await f.AnalystAsync();
        var sid = await NewSupplierAsync(c);
        var id = (await ApiClient.ReadAsync(await c.PostAsync("/api/invoices", NewInvoice(sid, "RJ-1", 100)))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsync($"/api/invoices/{id}/reject", new { note = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/invoices/{id}/reject", new { note = "Wrong entity" })).StatusCode);
    }

    [Fact]
    public async Task InvalidPayload_Returns400WithFieldErrors()
    {
        var c = await f.AnalystAsync();
        var res = await c.PostAsync("/api/invoices", new { supplierId = Guid.NewGuid(), number = "", currency = "EURO", lines = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True((await ApiClient.ReadAsync(res)).TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Upload_RejectsNonPdf_AcceptsPdf_AndDownloads()
    {
        var c = await f.AnalystAsync();
        var sid = await NewSupplierAsync(c);
        var id = (await ApiClient.ReadAsync(await c.PostAsync("/api/invoices", NewInvoice(sid, "UP-1", 100)))).GetProperty("id").GetGuid();

        var fake = new MultipartFormDataContent();
        var html = new ByteArrayContent("<html><script>alert(1)</script></html>"u8.ToArray());
        html.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        fake.Add(html, "file", "invoice.pdf");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostContentAsync($"/api/invoices/{id}/pdf", fake)).StatusCode);

        var exe = new MultipartFormDataContent();
        var exeBytes = new ByteArrayContent("%PDF-1.4 really"u8.ToArray());
        exeBytes.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        exe.Add(exeBytes, "file", "invoice.exe");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostContentAsync($"/api/invoices/{id}/pdf", exe)).StatusCode);

        var real = new MultipartFormDataContent();
        var pdf = new ByteArrayContent(TestPdf.Build("Invoice No: UP-1"));
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        real.Add(pdf, "file", "../../etc/passwd.pdf");
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostContentAsync($"/api/invoices/{id}/pdf", real)).StatusCode);

        var download = await c.GetAsync($"/api/invoices/{id}/pdf");
        Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);
        var detail = await c.GetJsonAsync($"/api/invoices/{id}");
        Assert.DoesNotContain("/", detail.GetProperty("pdfOriginalName").GetString());
    }

    [Fact]
    public async Task Extraction_ReadsTextPdf_AndMatchesSupplierByIban()
    {
        var c = await f.AnalystAsync();
        await NewSupplierAsync(c);
        var content = new MultipartFormDataContent();
        var pdf = new ByteArrayContent(TestPdf.Build(
            "Invoice No: EXT-2026-001", "Invoice Date: 2026-03-14", "Due Date: 13/04/2026", "PO Number: PO-1234",
            "Total due: EUR 4,250.50", "IBAN: FR76 3000 6000 0112 3456 7890 189"));
        pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(pdf, "file", "scan.pdf");
        var res = await c.PostContentAsync("/api/invoices/extract", content);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var r = await ApiClient.ReadAsync(res);
        Assert.True(r.GetProperty("textFound").GetBoolean());
        Assert.Equal("EXT-2026-001", r.GetProperty("number").GetString());
        Assert.Equal("2026-03-14", r.GetProperty("invoiceDate").GetString());
        Assert.Equal(4250.50m, r.GetProperty("total").GetDecimal());
        Assert.Equal("PO-1234", r.GetProperty("poNumber").GetString());
        Assert.Equal("FR7630006000011234567890189", r.GetProperty("iban").GetString());
        Assert.Equal(JsonValueKind.String, r.GetProperty("matchedSupplierId").ValueKind);
    }

    [Fact]
    public async Task Report_EscapesUserContent()
    {
        var admin = await f.AdminAsync();
        var sid = (await ApiClient.ReadAsync(await admin.PostAsync("/api/suppliers",
            AuthorizationTests.NewSupplier("<script>alert('x')</script> " + Guid.NewGuid().ToString("N")[..6])))).GetProperty("id").GetGuid();
        var id = (await ApiClient.ReadAsync(await admin.PostAsync("/api/invoices", NewInvoice(sid, "XSS-1", 100)))).GetProperty("id").GetGuid();
        var res = await admin.GetAsync($"/api/invoices/{id}/report");
        var html = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition?.DispositionType);
    }
}

[Collection("api")]
public class SupplierAndDashboardTests(FinGuardFactory f)
{
    [Fact]
    public async Task InvalidIban_IsRejected()
    {
        var c = await f.AnalystAsync();
        var res = await c.PostAsync("/api/suppliers", AuthorizationTests.NewSupplier("Bad Iban " + Guid.NewGuid().ToString("N")[..6], "FR7630006000011234567890188"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task BankDetailChange_IsRecorded_AndRaisesRisk()
    {
        // Bank-detail changes are admin-only (the analyst path is covered in PaymentAndBankDetailTests).
        var c = await f.AdminAsync();
        var name = "Bank Change " + Guid.NewGuid().ToString("N")[..6];
        var id = (await ApiClient.ReadAsync(await c.PostAsync("/api/suppliers", AuthorizationTests.NewSupplier(name)))).GetProperty("id").GetGuid();
        var before = (await c.GetJsonAsync($"/api/suppliers/{id}")).GetProperty("risk").GetProperty("score").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsync($"/api/suppliers/{id}", AuthorizationTests.NewSupplier(name, "DE89370400440532013000"))).StatusCode);

        var changes = await c.GetJsonAsync($"/api/suppliers/{id}/bank-changes");
        Assert.Equal(1, changes.GetArrayLength());
        var after = await c.GetJsonAsync($"/api/suppliers/{id}");
        Assert.True(after.GetProperty("risk").GetProperty("score").GetInt32() > before);
        Assert.Contains(after.GetProperty("risk").GetProperty("factors").EnumerateArray(), x => x.GetProperty("key").GetString() == "bank_changes" && x.GetProperty("points").GetInt32() > 0);
        Assert.True((await c.GetJsonAsync($"/api/suppliers/{id}/risk-history")).GetArrayLength() >= 2);
    }

    [Fact]
    public async Task SupplierList_SupportsSearchSortAndPaging()
    {
        var c = await f.AnalystAsync();
        var page = await c.GetJsonAsync("/api/suppliers?sort=name&dir=asc&page=1&pageSize=5");
        Assert.Equal(5, page.GetProperty("items").GetArrayLength());
        var names = page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
        var search = await c.GetJsonAsync("/api/suppliers?q=northwind");
        Assert.Equal("Northwind Office Supplies", search.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task SupplierSearch_CombinesWithOtherFilters()
    {
        var c = await f.AnalystAsync();
        var crest = await c.GetJsonAsync("/api/suppliers?q=crestline&status=all&sort=name&dir=asc");
        Assert.Equal(1, crest.GetProperty("total").GetInt32());
        var level = crest.GetProperty("items")[0].GetProperty("riskLevel").GetString();
        Assert.Contains(level, new[] { "High", "Critical" });
        Assert.Equal(0, (await c.GetJsonAsync("/api/suppliers?q=crestline&level=Low")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await c.GetJsonAsync($"/api/suppliers?q=crestline&level={level}")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task InvoiceSearch_CombinesWithOtherFilters()
    {
        var c = await f.AnalystAsync();
        var sid = (await ApiClient.ReadAsync(await c.PostAsync("/api/suppliers", AuthorizationTests.NewSupplier("Search Filter " + Guid.NewGuid().ToString("N")[..6])))).GetProperty("id").GetGuid();
        var number = "SRCH-" + Guid.NewGuid().ToString("N")[..8];
        await c.PostAsync("/api/invoices", InvoiceTests.NewInvoice(sid, number, 100));   // no PO -> ManualReview, Pending

        var hit = await c.GetJsonAsync($"/api/invoices?q={number}&status=Pending&decision=flagged");
        Assert.Equal(1, hit.GetProperty("total").GetInt32());
        Assert.Equal(number, hit.GetProperty("items")[0].GetProperty("number").GetString());
        Assert.Equal(0, (await c.GetJsonAsync($"/api/invoices?q={number}&status=Paid")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await c.GetJsonAsync($"/api/invoices?q={number}&decision=AutoApprove")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await c.GetJsonAsync($"/api/invoices?q={number}&supplierId={sid}")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task AlertSearch_CombinesWithOtherFilters()
    {
        var c = await f.AnalystAsync();
        var alertId = await AuthorizationTests.NewAlertAsync(f);
        var detail = await c.GetJsonAsync($"/api/alerts/{alertId}");
        var number = detail.GetProperty("alert").GetProperty("number").GetInt32();

        var hit = await c.GetJsonAsync($"/api/alerts?q={number}&severity=Critical&status=open");
        Assert.Equal(1, hit.GetProperty("total").GetInt32());
        Assert.Equal(alertId, hit.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(0, (await c.GetJsonAsync($"/api/alerts?q={number}&severity=Low")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await c.GetJsonAsync($"/api/alerts?q={number}&status=closed")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Compare_ReturnsEachSupplier()
    {
        var c = await f.AnalystAsync();
        var ids = (await c.GetJsonAsync("/api/suppliers?pageSize=3")).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
        var r = await c.GetJsonAsync("/api/suppliers/compare?" + string.Join("&", ids.Select(i => $"ids={i}")));
        Assert.Equal(3, r.GetArrayLength());
    }

    [Fact]
    public async Task Dashboard_MatchesDatabase()
    {
        var c = await f.AdminAsync();
        var d = await c.GetJsonAsync("/api/dashboard");
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var k = d.GetProperty("kpis");
        Assert.Equal(await db.Invoices.CountAsync(), k.GetProperty("totalInvoices").GetInt32());
        Assert.Equal(await db.Invoices.CountAsync(i => i.Decision != FinGuard.Domain.InvoiceDecision.AutoApprove), k.GetProperty("flaggedInvoices").GetInt32());
        Assert.Equal(k.GetProperty("totalInvoices").GetInt32(), k.GetProperty("verifiedInvoices").GetInt32() + k.GetProperty("flaggedInvoices").GetInt32());
        Assert.Equal(12, d.GetProperty("trend").GetArrayLength());
        Assert.True(k.GetProperty("openAlerts").GetInt32() > 0);
        Assert.True(k.GetProperty("confirmedFraud").GetInt32() >= 1);
    }

    [Fact]
    public async Task DemoData_IsFlagged_AndSeedProducesRiskHistory()
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Suppliers.Where(s => s.Name == "Northwind Office Supplies").AllAsync(s => s.IsDemo));
        var crest = await db.Suppliers.SingleAsync(s => s.Name == "Crestline Trading LLC");
        Assert.Contains(crest.RiskLevel, new[] { "High", "Critical" });
        Assert.True(await db.RiskAssessments.CountAsync(r => r.Trigger == "Monthly portfolio review") > 50);
    }

    [Fact]
    public async Task Assistant_RunsInLabelledLocalMode_AndGroundsOnData()
    {
        var c = await f.AnalystAsync();
        Assert.Equal("local", (await c.GetJsonAsync("/api/assistant/status")).GetProperty("mode").GetString());
        var res = await ApiClient.ReadAsync(await c.PostAsync("/api/assistant/chat", new { messages = new[] { new { role = "user", content = "Why is Crestline Trading LLC risky?" } } }));
        Assert.Equal("local", res.GetProperty("mode").GetString());
        Assert.Contains("Crestline", res.GetProperty("reply").GetString());
        Assert.Contains("Local demonstration mode", res.GetProperty("reply").GetString());
        Assert.Equal("supplier", res.GetProperty("references")[0].GetProperty("type").GetString());

        var prio = await ApiClient.ReadAsync(await c.PostAsync("/api/assistant/chat", new { messages = new[] { new { role = "user", content = "What should I investigate first?" } } }));
        Assert.Contains("investigation order", prio.GetProperty("reply").GetString());
    }
}

internal static class TestPdf
{
    public static byte[] Build(params string[] lines)
    {
        var b = new PdfDocumentBuilder();
        var page = b.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        var y = 780.0;
        foreach (var l in lines) { page.AddText(l, 11, new PdfPoint(40, y), font); y -= 20; }
        return b.Build();
    }
}
