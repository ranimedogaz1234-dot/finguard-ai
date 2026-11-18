using System.Security.Claims;
using FinGuard.Api.Data;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FinGuard.Api.Infrastructure;

// ---------- Application exceptions mapped to RFC 7807 problem details ----------
public abstract class AppException(string message, int status, string code) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public sealed class NotFoundException(string what) : AppException($"{what} was not found.", 404, "not_found");
public sealed class ForbiddenException(string message) : AppException(message, 403, "forbidden");
public sealed class ConflictException(string message) : AppException(message, 409, "conflict");
public sealed class BusinessRuleException(string message, string code = "business_rule") : AppException(message, 422, code);

/// <summary>Central error handler: application errors become problem details; anything else is logged and hidden.</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> log, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        ProblemDetails pd;
        if (ex is AppException app)
        {
            pd = new ProblemDetails { Status = app.Status, Title = app.Message, Type = $"https://finguard.dev/errors/{app.Code}" };
            pd.Extensions["code"] = app.Code;
            log.LogInformation("Request failed with {Code}: {Message}", app.Code, app.Message);
        }
        else if (ex is BadHttpRequestException bad)
        {
            pd = new ProblemDetails { Status = bad.StatusCode, Title = "The request was invalid." };
        }
        else
        {
            log.LogError(ex, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
            pd = new ProblemDetails { Status = 500, Title = "An unexpected error occurred. It has been logged." };
        }
        pd.Extensions["traceId"] = ctx.TraceIdentifier;
        ctx.Response.StatusCode = pd.Status ?? 500;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = ctx, ProblemDetails = pd, Exception = ex });
    }
}

// ---------- Current user ----------
public interface ICurrentUser
{
    Guid Id { get; }
    string Email { get; }
    string Name { get; }
    bool IsAdmin { get; }
    bool IsAuthenticated { get; }
    string? IpAddress { get; }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? P => accessor.HttpContext?.User;
    public bool IsAuthenticated => P?.Identity?.IsAuthenticated == true;
    public Guid Id => Guid.TryParse(P?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    public string Email => P?.FindFirstValue(ClaimTypes.Email) ?? "anonymous";
    public string Name => P?.FindFirstValue(ClaimTypes.Name) ?? "Anonymous";
    public bool IsAdmin => P?.IsInRole(Roles.Admin) == true;
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}

// ---------- Audit ----------
public interface IAuditLog
{
    void Record(string action, string entityType, string? entityId, string? details = null);
}

/// <summary>Adds audit rows to the current unit of work, so they commit atomically with the change they describe.</summary>
public sealed class AuditLog(AppDbContext db, ICurrentUser user) : IAuditLog
{
    public void Record(string action, string entityType, string? entityId, string? details = null) =>
        db.AuditLog.Add(new AuditEntry
        {
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details is { Length: > 1000 } ? details[..1000] : details,
            UserId = user.IsAuthenticated ? user.Id : null,
            UserEmail = user.IsAuthenticated ? user.Email : "anonymous",
            IpAddress = user.IpAddress,
        });
}

// ---------- Paging ----------
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public static class Paging
{
    public static (int page, int size) Normalize(int? page, int? size) =>
        (Math.Max(1, page ?? 1), Math.Clamp(size ?? 20, 1, 100));

    /// <summary>Lower-cased, trimmed search term capped at 100 characters (bounds the cost of LIKE queries).</summary>
    public static string Term(string q)
    {
        var t = q.Trim().ToLowerInvariant();
        return t.Length > 100 ? t[..100] : t;
    }
}

// ---------- Security headers ----------
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    // style-src allows inline styles because the charting library sets style attributes; scripts are 'self' only.
    private const string Csp =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    public Task Invoke(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        // The API docs UI needs its own scripts; everything else gets the strict policy.
        if (!ctx.Request.Path.StartsWithSegments("/scalar"))
            h["Content-Security-Policy"] = Csp;
        if (ctx.Request.Path.StartsWithSegments("/api"))
            h["Cache-Control"] = "no-store";
        return next(ctx);
    }
}

// ---------- CSRF ----------
/// <summary>
/// Requires a valid antiforgery token (X-XSRF-TOKEN header + antiforgery cookie) on every unsafe request.
/// MVC's built-in AutoValidateAntiforgeryToken filter depends on the Razor view services, which an API-only
/// app does not register, so this filter calls <see cref="Microsoft.AspNetCore.Antiforgery.IAntiforgery"/> directly.
/// Actions marked [IgnoreAntiforgeryToken] are exempt.
/// </summary>
public sealed class ApiAntiforgeryFilter(Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, ILogger<ApiAntiforgeryFilter> log)
    : Microsoft.AspNetCore.Mvc.Filters.IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(Microsoft.AspNetCore.Mvc.Filters.AuthorizationFilterContext context)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method)) return;
        if (context.ActionDescriptor.EndpointMetadata.OfType<IgnoreAntiforgeryTokenAttribute>().Any()) return;
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException ex)
        {
            log.LogInformation("Antiforgery validation failed for {Method} {Path}: {Reason}", method, context.HttpContext.Request.Path, ex.Message);
            var pd = new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "The security token for this request is missing or expired. Refresh and try again.",
                Type = "https://finguard.dev/errors/csrf_invalid",
            };
            pd.Extensions["code"] = "csrf_invalid";
            pd.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
            context.Result = new ObjectResult(pd) { StatusCode = pd.Status };
        }
    }
}

// ---------- Request size ----------
public static class RequestLimits
{
    /// <summary>PDF upload endpoints (5 MB file + multipart overhead).</summary>
    public const long UploadBytes = 6 * 1024 * 1024;
    /// <summary>Every other request body (JSON). The largest legitimate payload, a 100-line invoice, is far below this.</summary>
    public const long JsonBytes = 256 * 1024;
}

/// <summary>
/// Rejects oversized bodies with 413 before model binding. Upload endpoints (multipart) get the upload limit;
/// everything else the JSON limit. Kestrel's MaxRequestBodySize is the backstop for bodies without Content-Length.
/// </summary>
public sealed class RequestSizeLimitMiddleware(RequestDelegate next)
{
    public Task Invoke(HttpContext ctx)
    {
        var isUpload = ctx.Request.HasFormContentType;
        var limit = isUpload ? RequestLimits.UploadBytes : RequestLimits.JsonBytes;
        if (ctx.Request.ContentLength is { } len && len > limit)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return Task.CompletedTask;
        }
        if (!isUpload && ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f)
            f.MaxRequestBodySize = RequestLimits.JsonBytes;
        return next(ctx);
    }
}
