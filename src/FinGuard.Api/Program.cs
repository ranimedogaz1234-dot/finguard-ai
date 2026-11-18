using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using FinGuard.Api.Data;
using FinGuard.Api.Infrastructure;
using FinGuard.Api.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);
var env = builder.Environment;

// ---------- Logging ----------
builder.Logging.ClearProviders();
if (env.IsDevelopment()) builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
else builder.Logging.AddJsonConsole(o => o.IncludeScopes = true); // structured JSON for log shippers

// ---------- Options (bound from the final configuration at resolution time) ----------
builder.Services.AddOptions<AuthOptions>().BindConfiguration("Auth");
builder.Services.AddOptions<FinanceOptions>().BindConfiguration("Finance");
builder.Services.AddOptions<StorageOptions>().BindConfiguration("Storage");
builder.Services.AddOptions<AssistantOptions>().BindConfiguration("Assistant");
builder.Services.AddOptions<DemoOptions>().BindConfiguration("Demo");

// ---------- Database ----------
// Resolved when the DbContext is configured (not here), so configuration added later by hosts such as
// WebApplicationFactory is honoured, and relative paths are anchored to the content root, not the working directory.
builder.Services.AddDbContext<AppDbContext>((sp, o) => o.UseSqlite(SqlitePaths.Resolve(
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Default") ?? SqlitePaths.Default,
    sp.GetRequiredService<IHostEnvironment>().ContentRootPath)));

// ---------- Security ----------
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "finguard.session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = env.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        // APIs answer 401/403 instead of redirecting to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        o.Events.OnValidatePrincipal = AuthService.ValidatePrincipalAsync;
    });
builder.Services.AddAuthorization(o =>
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()); // deny by default

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-XSRF-TOKEN";
    o.Cookie.Name = "finguard.af";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = env.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Key(HttpContext c) => c.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    // Limits are read from the application's configuration when a partition is first created.
    static int Limit(HttpContext c, string key, int fallback) =>
        c.RequestServices.GetRequiredService<IConfiguration>().GetValue($"RateLimits:{key}", fallback);
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = Limit(c, "AuthPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("assistant", c => RateLimitPartition.GetFixedWindowLimiter(Key(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = Limit(c, "AssistantPerMinute", 20), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("upload", c => RateLimitPartition.GetFixedWindowLimiter(Key(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = Limit(c, "UploadsPerMinute", 20), Window = TimeSpan.FromMinutes(1) }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetFixedWindowLimiter(Key(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = Limit(c, "GlobalPerMinute", 600), Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>().Configure<IConfiguration>((o, cfg) =>
{
    var origins = cfg.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowCredentials()
        .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE").WithHeaders("Content-Type", "X-XSRF-TOKEN"));
});

builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = true; });

// ---------- MVC, validation, errors, OpenAPI ----------
builder.Services.AddControllers(o => o.Filters.Add<ApiAntiforgeryFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "FinGuard AI API";
    doc.Info.Description = "Invoice verification, supplier risk intelligence and fraud monitoring. Cookie-authenticated; unsafe methods require the X-XSRF-TOKEN header from GET /api/auth/csrf.";
    return Task.CompletedTask;
}));
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

// ---------- Application services ----------
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<IAuditLog, AuditLog>();
builder.Services.AddScoped<IFileStorage, FileStorage>();
if (env.IsDevelopment()) builder.Services.AddScoped<IEmailSender, DevOutboxEmailSender>();
else builder.Services.AddScoped<IEmailSender, DisabledEmailSender>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<RiskService>();
builder.Services.AddScoped<VerificationService>();
builder.Services.AddScoped<SupplierService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<PdfExtractionService>();
builder.Services.AddScoped<AssistantContextBuilder>();
builder.Services.AddScoped<LocalAssistantEngine>();
builder.Services.AddHttpClient<ExternalAssistantEngine>();
builder.Services.AddScoped<AssistantService>();
builder.Services.AddScoped<DemoSeeder>();
builder.Services.AddScoped<DemoDataService>();

var app = builder.Build();

// ---------- Database migration + optional demo seed ----------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (app.Configuration.GetValue("Database:EnsureCreated", false)) await db.Database.EnsureCreatedAsync();
    else await db.Database.MigrateAsync();

    var demo = app.Configuration.GetSection("Demo").Get<DemoOptions>() ?? new DemoOptions();
    if (demo.Seed) await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(demo);
}

// ---------- Pipeline ----------
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) { app.UseHsts(); app.UseHttpsRedirection(); }
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();
if (app.Environment.IsDevelopment() || app.Configuration.GetValue("OpenApi:Expose", false))
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference(o => o.WithTitle("FinGuard AI API")).AllowAnonymous();
}
// Unknown /api routes are real 404s; everything else is the single-page app.
app.Map("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

/// <summary>Exposed for WebApplicationFactory integration tests.</summary>
public partial class Program;
