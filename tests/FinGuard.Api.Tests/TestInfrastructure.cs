using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinGuard.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FinGuard.Api.Tests;

public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<(string To, string Subject, string Body)> Sent { get; } = new();
    public Task SendAsync(string to, string subject, string body, CancellationToken ct) { Sent.Enqueue((to, subject, body)); return Task.CompletedTask; }
}

/// <summary>Runs the real application against a throwaway SQLite database seeded with the demo dataset.</summary>
public sealed class FinGuardFactory : WebApplicationFactory<Program>
{
    public const string DemoPassword = "Test-Demo-Password-2026";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "finguard-tests-" + Guid.NewGuid().ToString("N"));
    public CapturingEmailSender Email { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dir);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(_dir, "test.db")}",
            ["Storage:UploadPath"] = Path.Combine(_dir, "uploads"),
            ["Demo:Seed"] = "true",
            ["Demo:Password"] = DemoPassword,
            ["RateLimits:AuthPerMinute"] = "10000",
            ["RateLimits:GlobalPerMinute"] = "100000",
            ["RateLimits:AssistantPerMinute"] = "10000",
            ["RateLimits:UploadsPerMinute"] = "10000",
            ["Assistant:Provider"] = "None",
        }));
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IEmailSender>();
            s.AddSingleton<IEmailSender>(Email);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A cookie-aware client that handles the antiforgery token like the SPA does.</summary>
    public async Task<ApiClient> ClientAsync(string? email = null, string? password = null)
    {
        var c = new ApiClient(CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false }));
        await c.RefreshCsrfAsync();
        if (email is not null)
        {
            var res = await c.PostAsync("/api/auth/login", new { email, password = password ?? DemoPassword });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            await c.RefreshCsrfAsync();
        }
        return c;
    }

    public Task<ApiClient> AdminAsync() => ClientAsync("admin@finguard.demo");
    public Task<ApiClient> AnalystAsync() => ClientAsync("analyst@finguard.demo");
    public Task<ApiClient> Analyst2Async() => ClientAsync("lina.analyst@finguard.demo");
}

public sealed class ApiClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public HttpClient Http { get; } = http;
    private string? _token;

    public async Task RefreshCsrfAsync()
    {
        var doc = await Http.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        _token = doc.GetProperty("token").GetString();
    }

    private HttpRequestMessage Req(HttpMethod m, string url, HttpContent? content, bool csrf = true)
    {
        var r = new HttpRequestMessage(m, url) { Content = content };
        if (csrf && _token is not null) r.Headers.Add("X-XSRF-TOKEN", _token);
        return r;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => Http.GetAsync(url);
    public Task<HttpResponseMessage> PostAsync(string url, object? body = null, bool csrf = true) =>
        Http.SendAsync(Req(HttpMethod.Post, url, JsonContent.Create(body ?? new { }), csrf));
    public Task<HttpResponseMessage> PutAsync(string url, object body) => Http.SendAsync(Req(HttpMethod.Put, url, JsonContent.Create(body)));
    public Task<HttpResponseMessage> PatchAsync(string url, object body) => Http.SendAsync(Req(HttpMethod.Patch, url, JsonContent.Create(body)));
    public Task<HttpResponseMessage> PostContentAsync(string url, HttpContent content) => Http.SendAsync(Req(HttpMethod.Post, url, content));

    public async Task<JsonElement> GetJsonAsync(string url)
    {
        var res = await Http.GetAsync(url);
        Assert.True(res.IsSuccessStatusCode, $"GET {url} returned {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>(Json);
}

/// <summary>All API tests share one seeded app instance (seeding takes a few seconds).</summary>
[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<FinGuardFactory>;
