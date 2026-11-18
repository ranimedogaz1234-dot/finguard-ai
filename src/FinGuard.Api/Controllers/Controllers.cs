using FinGuard.Api.Contracts;
using FinGuard.Api.Data;
using FinGuard.Api.Infrastructure;
using FinGuard.Api.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace FinGuard.Api.Controllers;

// All controllers require an authenticated user by default (fallback policy in Program.cs).
// List filters bind from the query string without a prefix. Their parameters must NOT be named after a filter key
// (e.g. "q"): MVC would then treat "?q=" as a prefix, look for "q.Q", "q.Sort"… and silently drop every filter.
// Unsafe methods are protected by a global antiforgery filter (X-XSRF-TOKEN header).

[ApiController, Route("api/auth")]
public class AuthController(AuthService auth, IAntiforgery antiforgery, ICurrentUser me, AppDbContext db) : ControllerBase
{
    /// <summary>Issues an antiforgery token bound to the current session. Call again after login/logout.</summary>
    [HttpGet("csrf"), AllowAnonymous, IgnoreAntiforgeryToken]
    public CsrfDto Csrf() => new(antiforgery.GetAndStoreTokens(HttpContext).RequestToken!);

    [HttpPost("register"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<UserDto> Register(RegisterRequest r, CancellationToken ct) => await auth.RegisterAsync(r, ct);

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<UserDto> Login(LoginRequest r, CancellationToken ct) => await auth.LoginAsync(r, ct);

    [HttpPost("logout")]
    public async Task<IActionResult> Logout() { await auth.LogoutAsync(); return NoContent(); }

    [HttpPost("forgot-password"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<IActionResult> Forgot(ForgotPasswordRequest r, CancellationToken ct)
    {
        await auth.ForgotPasswordAsync(r.Email, ct);
        return Accepted(new { message = "If an account exists for this email, a reset link has been sent." });
    }

    [HttpPost("reset-password"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<IActionResult> Reset(ResetPasswordRequest r, CancellationToken ct) { await auth.ResetPasswordAsync(r, ct); return NoContent(); }

    [HttpGet("me")]
    public async Task<UserDto> Me(CancellationToken ct) =>
        AuthService.ToDto(await db.Users.AsNoTracking().FirstAsync(u => u.Id == me.Id, ct));
}

[ApiController, Route("api/account")]
public class AccountController(AuthService auth, UserService users, ICurrentUser me, AppDbContext db) : ControllerBase
{
    [HttpPut("profile")]
    public async Task<UserDto> Profile(UpdateProfileRequest r, CancellationToken ct)
    {
        var dto = await users.UpdateProfileAsync(r, ct);
        await auth.SignInAsync(await db.Users.FirstAsync(u => u.Id == me.Id, ct)); // refresh name claim
        return dto;
    }

    [HttpPost("change-password"), EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest r, CancellationToken ct) { await auth.ChangePasswordAsync(me.Id, r, ct); return NoContent(); }
}

[ApiController, Route("api/users")]
public class UsersController(UserService users) : ControllerBase
{
    [HttpGet, Authorize(Roles = Roles.Admin)]
    public Task<IReadOnlyList<UserDto>> List(CancellationToken ct) => users.ListAsync(ct);

    [HttpGet("assignable")]
    public Task<IReadOnlyList<object>> Assignable(CancellationToken ct) => users.AssignableAsync(ct);

    [HttpPatch("{id:guid}"), Authorize(Roles = Roles.Admin)]
    public Task<UserDto> Update(Guid id, UpdateUserRequest r, CancellationToken ct) => users.UpdateAsync(id, r, ct);
}

[ApiController, Route("api/suppliers")]
public class SuppliersController(SupplierService suppliers) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<SupplierListItem>> List([FromQuery] SupplierQuery filter, CancellationToken ct) => suppliers.ListAsync(filter, ct);

    [HttpGet("facets")]
    public Task<object> Facets(CancellationToken ct) => suppliers.FacetsAsync(ct);

    [HttpGet("compare")]
    public Task<IReadOnlyList<SupplierDetail>> Compare([FromQuery] List<Guid> ids, CancellationToken ct) => suppliers.CompareAsync(ids, ct);

    [HttpGet("{id:guid}")]
    public Task<SupplierDetail> Get(Guid id, CancellationToken ct) => suppliers.GetAsync(id, ct);

    [HttpPost]
    public async Task<IActionResult> Create(SupplierUpsertRequest r, CancellationToken ct)
    {
        var id = await suppliers.CreateAsync(r, ct);
        return CreatedAtAction(nameof(Get), new { id }, new { id });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, SupplierUpsertRequest r, CancellationToken ct) { await suppliers.UpdateAsync(id, r, ct); return NoContent(); }

    [HttpPost("{id:guid}/archive"), Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct) { await suppliers.SetArchivedAsync(id, true, ct); return NoContent(); }

    [HttpPost("{id:guid}/restore"), Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct) { await suppliers.SetArchivedAsync(id, false, ct); return NoContent(); }

    [HttpPost("{id:guid}/reassess")]
    public Task<RiskAssessmentDto> Reassess(Guid id, CancellationToken ct) => suppliers.ReassessAsync(id, ct);

    [HttpGet("{id:guid}/risk-history")]
    public Task<IReadOnlyList<RiskHistoryPoint>> RiskHistory(Guid id, CancellationToken ct) => suppliers.RiskHistoryAsync(id, ct);

    [HttpGet("{id:guid}/bank-changes")]
    public Task<IReadOnlyList<BankChangeDto>> BankChanges(Guid id, CancellationToken ct) => suppliers.BankChangesAsync(id, ct);
}

[ApiController, Route("api/invoices")]
public class InvoicesController(InvoiceService invoices, PdfExtractionService extraction, ICurrentUser me) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<InvoiceListItem>> List([FromQuery] InvoiceQuery filter, CancellationToken ct) => invoices.ListAsync(filter, ct);

    [HttpGet("{id:guid}")]
    public Task<InvoiceDetail> Get(Guid id, CancellationToken ct) => invoices.GetAsync(id, ct);

    [HttpPost]
    public async Task<IActionResult> Create(InvoiceCreateRequest r, CancellationToken ct)
    {
        var id = await invoices.CreateAsync(r, ct);
        return CreatedAtAction(nameof(Get), new { id }, new { id });
    }

    [HttpPost("{id:guid}/verify")]
    public async Task<InvoiceDetail> Reverify(Guid id, CancellationToken ct) { await invoices.ReverifyAsync(id, ct); return await invoices.GetAsync(id, ct); }

    [HttpPost("{id:guid}/approve")]
    public async Task<InvoiceDetail> Approve(Guid id, ReviewRequest r, CancellationToken ct) { await invoices.ApproveAsync(id, r.Note, ct); return await invoices.GetAsync(id, ct); }

    [HttpPost("{id:guid}/reject")]
    public async Task<InvoiceDetail> Reject(Guid id, ReviewRequest r, CancellationToken ct) { await invoices.RejectAsync(id, r.Note, ct); return await invoices.GetAsync(id, ct); }

    [HttpPost("{id:guid}/mark-paid"), Authorize(Roles = Roles.Admin)]
    public async Task<InvoiceDetail> MarkPaid(Guid id, CancellationToken ct) { await invoices.MarkPaidAsync(id, ct); return await invoices.GetAsync(id, ct); }

    [HttpPost("extract"), EnableRateLimiting("upload"), RequestSizeLimit(6 * 1024 * 1024)]
    public Task<ExtractionResult> Extract(IFormFile file, CancellationToken ct) => extraction.ExtractAsync(file, ct);

    [HttpPost("{id:guid}/pdf"), EnableRateLimiting("upload"), RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> UploadPdf(Guid id, IFormFile file, CancellationToken ct) { await invoices.AttachPdfAsync(id, file, ct); return NoContent(); }

    [HttpGet("{id:guid}/pdf")]
    public async Task<IActionResult> DownloadPdf(Guid id, CancellationToken ct)
    {
        var (stream, name) = await invoices.OpenPdfAsync(id, ct);
        return File(stream, "application/pdf", name);
    }

    [HttpGet("{id:guid}/report")]
    public async Task<IActionResult> Report(Guid id, CancellationToken ct)
    {
        var detail = await invoices.GetAsync(id, ct);
        var html = ReportBuilder.Build(detail, DateTime.UtcNow, me.Email);
        // Served as a download (attachment) with the strict CSP from the security headers middleware.
        return File(System.Text.Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", $"verification-{SafeName(detail.Number)}.html");
    }

    private static string SafeName(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
}

[ApiController, Route("api/alerts")]
public class AlertsController(AlertService alerts) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AlertListItem>> List([FromQuery] AlertQuery filter, CancellationToken ct) => alerts.ListAsync(filter, ct);

    [HttpGet("{id:guid}")]
    public Task<AlertDetail> Get(Guid id, CancellationToken ct) => alerts.GetAsync(id, ct);

    [HttpPost("{id:guid}/status")]
    public async Task<AlertDetail> Status(Guid id, AlertStatusRequest r, CancellationToken ct) { await alerts.ChangeStatusAsync(id, r, ct); return await alerts.GetAsync(id, ct); }

    [HttpPost("{id:guid}/assign")]
    public async Task<AlertDetail> Assign(Guid id, AlertAssignRequest r, CancellationToken ct) { await alerts.AssignAsync(id, r.UserId, ct); return await alerts.GetAsync(id, ct); }

    [HttpPost("{id:guid}/notes")]
    public async Task<AlertDetail> Note(Guid id, AlertNoteRequest r, CancellationToken ct) { await alerts.AddNoteAsync(id, r.Note, ct); return await alerts.GetAsync(id, ct); }
}

[ApiController, Route("api/dashboard")]
public class DashboardController(DashboardService dashboard) : ControllerBase
{
    [HttpGet]
    public Task<DashboardDto> Get(CancellationToken ct) => dashboard.GetAsync(ct);
}

[ApiController, Route("api/assistant")]
public class AssistantController(AssistantService assistant, AssistantContextBuilder context) : ControllerBase
{
    [HttpGet("status")]
    public AssistantStatus Status() => assistant.Status();

    [HttpGet("priorities")]
    public Task<IReadOnlyList<PriorityItem>> Priorities(CancellationToken ct) => context.PrioritiesAsync(ct);

    [HttpPost("chat"), EnableRateLimiting("assistant")]
    public Task<AssistantResponse> Chat(AssistantRequest r, CancellationToken ct) => assistant.AskAsync(r, ct);
}

[ApiController, Route("api/audit"), Authorize(Roles = Roles.Admin)]
public class AuditController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<PagedResult<ActivityItem>> List([FromQuery] string? q, [FromQuery] string? action, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.AuditLog.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action.StartsWith(action));
        if (!string.IsNullOrWhiteSpace(q)) { var t = q.ToLower(); query = query.Where(a => a.UserEmail.ToLower().Contains(t) || (a.Details != null && a.Details.ToLower().Contains(t))); }
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(a => a.Id).Skip((p - 1) * size).Take(size)
            .Select(a => new ActivityItem(a.AtUtc, a.UserEmail, a.Action, a.EntityType, a.EntityId, a.Details)).ToListAsync(ct);
        return new(items, p, size, total);
    }
}

[ApiController, Route("api/system")]
public class SystemController(DemoDataService demo, AppDbContext db, IWebHostEnvironment env, Microsoft.Extensions.Options.IOptions<FinanceOptions> finance) : ControllerBase
{
    [HttpGet("info"), AllowAnonymous]
    public async Task<object> Info(CancellationToken ct) => new
    {
        name = "FinGuard AI",
        environment = env.EnvironmentName,
        demoData = await db.Suppliers.AnyAsync(s => s.IsDemo, ct),
        devOutbox = env.IsDevelopment(),
        approvalThreshold = finance.Value.ApprovalThreshold,
        baseCurrency = finance.Value.BaseCurrency,
    };

    [HttpPost("demo/purge"), Authorize(Roles = Roles.Admin)]
    public Task<object> Purge(CancellationToken ct) => demo.PurgeAsync(ct);

    /// <summary>Development-only stand-in for an email inbox, so the password-reset flow can be exercised locally.</summary>
    [HttpGet("dev-outbox"), AllowAnonymous]
    public async Task<IActionResult> Outbox([FromQuery] string email, CancellationToken ct)
    {
        if (!env.IsDevelopment()) return NotFound();
        var to = email.Trim();
        var mails = await db.Outbox.AsNoTracking().Where(m => m.To == to).OrderByDescending(m => m.CreatedAtUtc).Take(5)
            .Select(m => new { m.Subject, m.Body, m.CreatedAtUtc }).ToListAsync(ct);
        return Ok(mails);
    }
}
