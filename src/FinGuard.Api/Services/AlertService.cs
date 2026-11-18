using FinGuard.Api.Contracts;
using FinGuard.Api.Data;
using FinGuard.Api.Infrastructure;
using FinGuard.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinGuard.Api.Services;

public record AlertQuery(string? Q, string? Status, string? Severity, string? Code, Guid? SupplierId, bool? Mine,
    string? Sort, string? Dir, int? Page, int? PageSize);

public class AlertService(AppDbContext db, RiskService risk, IAuditLog audit, ICurrentUser user, TimeProvider clock)
{
    public static IQueryable<AlertListItem> ProjectList(IQueryable<FraudAlert> q) =>
        q.Select(a => new AlertListItem(a.Id, a.Number, a.Code, a.Severity.ToString(), a.Status.ToString(), a.Title,
            a.SupplierId, a.Supplier.Name, a.InvoiceId, a.Invoice != null ? a.Invoice.Number : null,
            a.AssignedTo != null ? a.AssignedTo.DisplayName : null, Cents.To(a.ExposureCents), a.IsDemo, a.CreatedAtUtc, a.UpdatedAtUtc));

    public async Task<PagedResult<AlertListItem>> ListAsync(AlertQuery q, CancellationToken ct)
    {
        var (page, size) = Paging.Normalize(q.Page, q.PageSize);
        var query = db.Alerts.AsNoTracking().AsQueryable();

        var status = (q.Status ?? "open").ToLowerInvariant();
        if (status == "open") query = query.Where(AlertQueries.IsOpen);
        else if (status == "closed") query = query.Where(a => a.Status == AlertStatus.Dismissed || a.Status == AlertStatus.Resolved || a.Status == AlertStatus.ConfirmedFraud);
        else if (Enum.TryParse<AlertStatus>(q.Status, true, out var st)) query = query.Where(a => a.Status == st);

        if (Enum.TryParse<AlertSeverity>(q.Severity, true, out var sev)) query = query.Where(a => a.Severity == sev);
        if (!string.IsNullOrWhiteSpace(q.Code)) query = query.Where(a => a.Code == q.Code);
        if (q.SupplierId is { } sid) query = query.Where(a => a.SupplierId == sid);
        if (q.Mine == true) { var me = user.Id; query = query.Where(a => a.AssignedToId == me); }
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = Paging.Term(q.Q);
            query = int.TryParse(term, out var n)
                ? query.Where(a => a.Number == n)
                : query.Where(a => a.Title.ToLower().Contains(term) || a.Supplier.Name.ToLower().Contains(term)
                                   || a.Code.ToLower().Contains(term) || (a.Invoice != null && a.Invoice.Number.ToLower().Contains(term)));
        }

        var desc = !string.Equals(q.Dir, "asc", StringComparison.OrdinalIgnoreCase);
        query = (q.Sort ?? "severity").ToLowerInvariant() switch
        {
            "created" => desc ? query.OrderByDescending(a => a.CreatedAtUtc) : query.OrderBy(a => a.CreatedAtUtc),
            "exposure" => desc ? query.OrderByDescending(a => a.ExposureCents) : query.OrderBy(a => a.ExposureCents),
            "number" => desc ? query.OrderByDescending(a => a.Number) : query.OrderBy(a => a.Number),
            _ => desc ? query.OrderByDescending(AlertQueries.SeverityRank).ThenByDescending(a => a.CreatedAtUtc)
                      : query.OrderBy(AlertQueries.SeverityRank).ThenByDescending(a => a.CreatedAtUtc),
        };
        var total = await query.CountAsync(ct);
        var items = await ProjectList(query.Skip((page - 1) * size).Take(size)).ToListAsync(ct);
        return new(items, page, size, total);
    }

    public async Task<AlertDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var a = await db.Alerts.AsNoTracking().Include(x => x.Events).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Alert");
        var item = await ProjectList(db.Alerts.AsNoTracking().Where(x => x.Id == id)).FirstAsync(ct);
        var canEdit = CanEdit(a);
        var allowed = canEdit
            ? Enum.GetValues<AlertStatus>().Where(t => AlertWorkflow.CanTransition(a.Status, t, user.IsAdmin)).Select(t => t.ToString()).ToList()
            : [];
        return new AlertDetail(item, a.Explanation, Json.Deserialize<Dictionary<string, string>>(a.EvidenceJson), a.AssignedToId,
            a.ResolutionNote, a.Events.OrderBy(e => e.AtUtc).Select(e => new AlertEventDto(e.Kind, e.FromStatus, e.ToStatus, e.Note, e.ActorName, e.AtUtc)).ToList(),
            allowed, canEdit);
    }

    /// <summary>Object-level rule: an alert assigned to someone can only be worked on by that person or an administrator.</summary>
    private bool CanEdit(FraudAlert a) => user.IsAdmin || a.AssignedToId is null || a.AssignedToId == user.Id;

    private async Task<FraudAlert> LoadForEditAsync(Guid id, CancellationToken ct)
    {
        var a = await db.Alerts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Alert");
        if (!CanEdit(a)) throw new ForbiddenException("This alert is assigned to another analyst. Ask an administrator to reassign it.");
        return a;
    }

    public async Task ChangeStatusAsync(Guid id, AlertStatusRequest r, CancellationToken ct)
    {
        var a = await LoadForEditAsync(id, ct);
        if (!AlertWorkflow.CanTransition(a.Status, r.Status, user.IsAdmin))
            throw new ForbiddenException(r.Status == AlertStatus.ConfirmedFraud && !user.IsAdmin
                ? "Only administrators can confirm fraud. Escalate the alert instead."
                : $"Cannot move an alert from {a.Status} to {r.Status}.");
        if (AlertWorkflow.IsTerminal(r.Status) && string.IsNullOrWhiteSpace(r.Note))
            throw new BusinessRuleException("A note explaining the outcome is required to close an alert.", "note_required");

        var now = clock.GetUtcNow().UtcDateTime;
        db.AlertEvents.Add(new AlertEvent { AlertId = a.Id, Kind = "status", FromStatus = a.Status.ToString(), ToStatus = r.Status.ToString(),
            Note = r.Note?.Trim(), ActorId = user.Id, ActorName = user.Name, AtUtc = now });
        var from = a.Status;
        a.Status = r.Status;
        a.UpdatedAtUtc = now;
        if (AlertWorkflow.IsTerminal(r.Status)) a.ResolutionNote = r.Note?.Trim();
        if (a.AssignedToId is null && r.Status == AlertStatus.Investigating) a.AssignedToId = user.Id; // starting work takes ownership
        audit.Record("alert.status_changed", "Alert", a.Id.ToString(), $"#{a.Number} {from} → {r.Status}");
        await db.SaveChangesAsync(ct);
        await risk.AssessAsync(a.SupplierId, $"Alert #{a.Number} moved to {r.Status}", ct);
    }

    public async Task AssignAsync(Guid id, Guid? assigneeId, CancellationToken ct)
    {
        var a = await LoadForEditAsync(id, ct);
        if (!user.IsAdmin && assigneeId is { } target && target != user.Id)
            throw new ForbiddenException("Analysts can only assign alerts to themselves.");
        string? name = null;
        if (assigneeId is { } aid)
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == aid && x.IsActive, ct) ?? throw new NotFoundException("User");
            name = u.DisplayName;
        }
        a.AssignedToId = assigneeId;
        a.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        db.AlertEvents.Add(new AlertEvent { AlertId = a.Id, Kind = "assigned", Note = name is null ? "Unassigned" : $"Assigned to {name}",
            ActorId = user.Id, ActorName = user.Name, AtUtc = a.UpdatedAtUtc });
        audit.Record("alert.assigned", "Alert", a.Id.ToString(), $"#{a.Number} → {name ?? "unassigned"}");
        await db.SaveChangesAsync(ct);
    }

    public async Task AddNoteAsync(Guid id, string note, CancellationToken ct)
    {
        var a = await LoadForEditAsync(id, ct);
        a.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        db.AlertEvents.Add(new AlertEvent { AlertId = a.Id, Kind = "note", Note = note.Trim(), ActorId = user.Id, ActorName = user.Name, AtUtc = a.UpdatedAtUtc });
        audit.Record("alert.note_added", "Alert", a.Id.ToString(), $"#{a.Number}");
        await db.SaveChangesAsync(ct);
    }
}
