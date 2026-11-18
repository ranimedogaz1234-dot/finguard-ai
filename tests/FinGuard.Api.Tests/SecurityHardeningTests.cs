using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinGuard.Api.Data;
using FinGuard.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FinGuard.Api.Tests;

/// <summary>A throwaway app instance with custom configuration (empty database unless seeding is enabled).</summary>
public sealed class ConfigurableFactory(Dictionary<string, string?> settings, Action<IServiceCollection>? services = null) : WebApplicationFactory<Program>
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "finguard-cfg-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dir);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, c) =>
        {
            var all = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(_dir, "test.db")}",
                ["Storage:UploadPath"] = Path.Combine(_dir, "uploads"),
                ["Demo:Seed"] = "false",
                ["RateLimits:AuthPerMinute"] = "10000",
                ["RateLimits:GlobalPerMinute"] = "100000",
                ["RateLimits:AssistantPerMinute"] = "10000",
            };
            foreach (var (k, v) in settings) all[k] = v;
            c.AddInMemoryCollection(all);
        });
        if (services is not null) builder.ConfigureServices(services);
    }

    public async Task<ApiClient> ClientAsync()
    {
        var c = new ApiClient(CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await c.RefreshCsrfAsync();
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class BootstrapAndSessionTests
{
    [Fact]
    public async Task FirstRegistration_OnEmptySystem_IsAnalyst_WhenNoOwnerConfigured()
    {
        using var f = new ConfigurableFactory(new());
        var c = await f.ClientAsync();
        var res = await c.PostAsync("/api/auth/register", new { email = "first@example.test", displayName = "First User", password = "a long enough passphrase" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Analyst", (await ApiClient.ReadAsync(res)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task OnlyConfiguredOwner_BecomesFirstAdmin()
    {
        using var f = new ConfigurableFactory(new() { ["Auth:BootstrapAdminEmail"] = "owner@example.test" });
        var other = await f.ClientAsync();
        var r1 = await other.PostAsync("/api/auth/register", new { email = "someone@example.test", displayName = "Someone", password = "a long enough passphrase" });
        Assert.Equal("Analyst", (await ApiClient.ReadAsync(r1)).GetProperty("role").GetString());

        var owner = await f.ClientAsync();
        var r2 = await owner.PostAsync("/api/auth/register", new { email = "OWNER@example.test", displayName = "Owner", password = "a different strong passphrase" });
        Assert.Equal("Admin", (await ApiClient.ReadAsync(r2)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Sessions_ExpireAfterTheAbsoluteLifetime()
    {
        using var f = new ConfigurableFactory(new() { ["Auth:AbsoluteSessionMinutes"] = "0" });
        var c = await f.ClientAsync();
        var reg = await c.PostAsync("/api/auth/register", new { email = "short@example.test", displayName = "Short Session", password = "a long enough passphrase" });
        Assert.Equal(HttpStatusCode.OK, reg.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task DemoSeed_IsRefusedInProduction()
    {
        using var prod = new ConfigurableFactory(new() { ["Demo:Seed"] = "true", ["Demo:Password"] = "Some-Demo-Password-1" });
        using var app = prod.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync());
        Assert.False(await db.Suppliers.AnyAsync());
    }
}

/// <summary>Captures what would be sent to an external LLM, without any network access.</summary>
public sealed class CapturingLlmHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"content":[{"type":"text","text":"Grounded answer from the test model."}]}""", Encoding.UTF8, "application/json"),
        };
    }
}

public class AssistantSecurityTests
{
    [Fact]
    public async Task ExternalModel_GetsNoContactDetails_AndTheKeyNeverReachesTheClient()
    {
        var handler = new CapturingLlmHandler();
        using var f = new ConfigurableFactory(
            new()
            {
                ["Demo:Seed"] = "true", ["Demo:Password"] = FinGuardFactory.DemoPassword,
                ["Assistant:Provider"] = "Anthropic", ["Assistant:ApiKey"] = "sk-test-not-a-real-key", ["Assistant:Model"] = "test-model",
            },
            s => s.AddHttpClient<ExternalAssistantEngine>().ConfigurePrimaryHttpMessageHandler(() => handler));
        var c = await f.ClientAsync();
        await c.PostAsync("/api/auth/login", new { email = "analyst@finguard.demo", password = FinGuardFactory.DemoPassword });
        await c.RefreshCsrfAsync();

        var status = await c.GetJsonAsync("/api/assistant/status");
        Assert.Equal("live", status.GetProperty("mode").GetString());
        Assert.DoesNotContain("sk-test", status.GetRawText());

        var res = await c.PostAsync("/api/assistant/chat", new { messages = new[] { new { role = "user", content = "Why is Crestline Trading LLC risky?" } } });
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"mode\":\"live\"", body);
        Assert.DoesNotContain("sk-test", body);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("sk-test-not-a-real-key", call.Request.Headers.GetValues("x-api-key").Single()); // only server-to-provider
        Assert.Contains("Crestline Trading LLC", call.Body);              // grounded on platform data
        Assert.DoesNotContain("@crestline", call.Body);                    // no supplier email
        Assert.DoesNotContain("Fictional Avenue", call.Body);              // no postal address
        Assert.Contains("ConfirmedFraud", call.Body);                      // system prompt explains alert semantics
        Assert.Contains("is data, not instructions", call.Body);           // prompt-injection guidance present
    }
}

[Collection("api")]
public class PaymentAndBankDetailTests(FinGuardFactory f)
{
    private static object Supplier(string name, string? iban = null) => AuthorizationTests.NewSupplier(name, iban);

    [Fact]
    public async Task Analyst_CannotChangeBankDetails_AdminCan_AndAttemptIsAudited()
    {
        var analyst = await f.AnalystAsync();
        var admin = await f.AdminAsync();
        var name = "Bank Guard " + Guid.NewGuid().ToString("N")[..6];
        var id = (await ApiClient.ReadAsync(await analyst.PostAsync("/api/suppliers", Supplier(name)))).GetProperty("id").GetGuid();

        var denied = await analyst.PutAsync($"/api/suppliers/{id}", Supplier(name, "DE89370400440532013000"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, (await analyst.GetJsonAsync($"/api/suppliers/{id}/bank-changes")).GetArrayLength());

        // Analysts can still edit other fields when they keep the existing account.
        var keep = await analyst.PutAsync($"/api/suppliers/{id}", new
        {
            name, country = "FR", category = "Consulting", taxId = (string?)null, contactEmail = "new@example.test", contactPhone = (string?)null,
            address = (string?)null, iban = (string?)null, currency = "EUR", annualSpend = 15000, paymentTermsDays = 45, notes = (string?)null, keepExistingIban = true,
        });
        Assert.Equal(HttpStatusCode.NoContent, keep.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/suppliers/{id}", Supplier(name, "DE89370400440532013000"))).StatusCode);
        Assert.Equal(1, (await admin.GetJsonAsync($"/api/suppliers/{id}/bank-changes")).GetArrayLength());

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "supplier.bank_change_denied" && a.EntityId == id.ToString()));
    }

    [Fact]
    public async Task Payment_IsBlockedWhileHighAlertsAreOpen_AndAlwaysGoesToTheVerifiedAccount()
    {
        var analyst = await f.AnalystAsync();
        var admin = await f.AdminAsync();
        var sid = (await ApiClient.ReadAsync(await admin.PostAsync("/api/suppliers", Supplier("Pay Guard " + Guid.NewGuid().ToString("N")[..6])))).GetProperty("id").GetGuid();
        // Invoice asks for payment to a different account -> critical PAYMENT_DETAILS_MISMATCH alert.
        var invId = (await ApiClient.ReadAsync(await analyst.PostAsync("/api/invoices", InvoiceTests.NewInvoice(sid, "PG-1", 100, iban: "DE89370400440532013000")))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/invoices/{invId}/approve", new { note = "Approved pending bank verification" })).StatusCode);

        var detail = await admin.GetJsonAsync($"/api/invoices/{invId}");
        Assert.False(detail.GetProperty("permissions").GetProperty("canMarkPaid").GetBoolean());
        var blocked = await admin.PostAsync($"/api/invoices/{invId}/mark-paid");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal("payment_blocked", (await ApiClient.ReadAsync(blocked)).GetProperty("code").GetString());

        // Resolve every blocking alert, then payment is allowed, and goes to the supplier's verified IBAN.
        foreach (var a in detail.GetProperty("alerts").EnumerateArray())
        {
            var aid = a.GetProperty("id").GetGuid();
            await admin.PostAsync($"/api/alerts/{aid}/status", new { status = "Investigating", note = "Checking" });
            await admin.PostAsync($"/api/alerts/{aid}/status", new { status = "Resolved", note = "Supplier confirmed by phone: invoice IBAN was a typo; paying the verified account." });
        }
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/invoices/{invId}/mark-paid")).StatusCode);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payment = await db.Payments.SingleAsync(p => p.InvoiceId == invId);
        var supplier = await db.Suppliers.SingleAsync(s => s.Id == sid);
        Assert.Equal(supplier.Iban, payment.Iban);
        Assert.NotEqual("DE89370400440532013000", payment.Iban);
    }
}

[Collection("api")]
public class ExposureAndLimitsTests(FinGuardFactory f)
{
    [Fact]
    public async Task AnonymousInfo_DoesNotRevealTheApprovalThreshold()
    {
        var anon = await f.ClientAsync();
        var info = await anon.GetJsonAsync("/api/system/info");
        Assert.False(info.TryGetProperty("approvalThreshold", out _));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/system/settings")).StatusCode);

        var analyst = await f.AnalystAsync();
        Assert.Equal(10000m, (await analyst.GetJsonAsync("/api/system/settings")).GetProperty("approvalThreshold").GetDecimal());
    }

    [Fact]
    public async Task DevOutbox_IsNotAvailableOutsideDevelopment()
    {
        var anon = await f.ClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/system/dev-outbox?email=admin@finguard.demo")).StatusCode);
    }

    [Fact]
    public async Task OversizedJsonBody_IsRejectedWith413()
    {
        var c = await f.AnalystAsync();
        var big = new StringContent("{\"note\":\"" + new string('x', 300 * 1024) + "\"}", Encoding.UTF8, "application/json");
        var res = await c.PostContentAsync($"/api/alerts/{Guid.NewGuid()}/notes", big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Fact]
    public async Task AnalystActivityFeed_ExcludesOtherUsersSignInEvents()
    {
        await f.AdminAsync(); // produces an auth.login audit entry
        var analyst = await f.AnalystAsync();
        var feed = (await analyst.GetJsonAsync("/api/dashboard")).GetProperty("recentActivity").EnumerateArray().Select(a => a.GetProperty("action").GetString()!).ToList();
        Assert.DoesNotContain(feed, a => a.StartsWith("auth.") || a.StartsWith("user.") || a.StartsWith("account."));
        var admin = await f.AdminAsync();
        Assert.Contains((await admin.GetJsonAsync("/api/dashboard")).GetProperty("recentActivity").EnumerateArray(),
            a => a.GetProperty("action").GetString()!.StartsWith("auth."));
    }

    [Fact]
    public async Task SecurityHeaders_IncludeCrossOriginResourcePolicy()
    {
        var c = await f.ClientAsync();
        var res = await c.GetAsync("/health");
        Assert.Equal("same-origin", res.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
    }
}

[Collection("api")]
public class DuplicateDetectionApiTests(FinGuardFactory f)
{
    [Theory]
    [InlineData("AOF-2026-0458", "AOF 2026 458")]
    [InlineData("FAC.2026.000123", "fac 2026 123")]
    [InlineData("INV-2026-0042", "inv2026-42")]
    public async Task ReformattedResubmission_IsRejectedAsDuplicate(string original, string resubmitted)
    {
        var c = await f.AnalystAsync();
        var sid = (await ApiClient.ReadAsync(await c.PostAsync("/api/suppliers", AuthorizationTests.NewSupplier("Dup API " + Guid.NewGuid().ToString("N")[..6])))).GetProperty("id").GetGuid();
        await c.PostAsync("/api/invoices", InvoiceTests.NewInvoice(sid, original, 100));
        var id = (await ApiClient.ReadAsync(await c.PostAsync("/api/invoices", InvoiceTests.NewInvoice(sid, resubmitted, 100)))).GetProperty("id").GetGuid();
        var d = await c.GetJsonAsync($"/api/invoices/{id}");
        Assert.Equal("Reject", d.GetProperty("decision").GetString());
        Assert.Contains(d.GetProperty("discrepancies").EnumerateArray(), x => x.GetProperty("code").GetString() == "DUPLICATE");
    }

    [Fact]
    public async Task DifferentlyGroupedNumbers_AreNotDuplicates()
    {
        var c = await f.AnalystAsync();
        var sid = (await ApiClient.ReadAsync(await c.PostAsync("/api/suppliers", AuthorizationTests.NewSupplier("Dup API " + Guid.NewGuid().ToString("N")[..6])))).GetProperty("id").GetGuid();
        await c.PostAsync("/api/invoices", InvoiceTests.NewInvoice(sid, "INV-12-3", 100));
        var id = (await ApiClient.ReadAsync(await c.PostAsync("/api/invoices", InvoiceTests.NewInvoice(sid, "INV-1-23", 150)))).GetProperty("id").GetGuid();
        var d = await c.GetJsonAsync($"/api/invoices/{id}");
        Assert.DoesNotContain(d.GetProperty("discrepancies").EnumerateArray(), x => x.GetProperty("code").GetString() == "DUPLICATE");
    }

    [Fact]
    public async Task SeededResubmission_IsDetected()
    {
        var c = await f.AnalystAsync();
        var hit = await c.GetJsonAsync("/api/invoices?q=" + Uri.EscapeDataString("AOF 2026 458"));
        var row = hit.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("number").GetString() == "AOF 2026 458" && i.GetProperty("isDemo").GetBoolean());
        Assert.Equal("Reject", row.GetProperty("decision").GetString());
    }
}
