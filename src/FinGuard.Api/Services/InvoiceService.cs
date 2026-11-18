using FinGuard.Api.Contracts;
using FinGuard.Api.Data;
using FinGuard.Api.Infrastructure;
using FinGuard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinGuard.Api.Services;

public record InvoiceQuery(string? Q, string? Status, string? Decision, Guid? SupplierId, DateOnly? From, DateOnly? To,
    decimal? MinAmount, decimal? MaxAmount, string? Sort, string? Dir, int? Page, int? PageSize);

public class InvoiceService(AppDbContext db, VerificationService verifier, RiskService risk, IAuditLog audit,
    ICurrentUser user, IFileStorage files, IOptions<FinanceOptions> finance, TimeProvider clock)
{
    public async Task<PagedResult<InvoiceListItem>> ListAsync(InvoiceQuery q, CancellationToken ct)
    {
        var (page, size) = Paging.Normalize(q.Page, q.PageSize);
        var query = db.Invoices.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim().ToLower();
            query = query.Where(i => i.Number.ToLower().Contains(term) || i.Supplier.Name.ToLower().Contains(term)
                                     || (i.PoNumber != null && i.PoNumber.ToLower().Contains(term)));
        }
        if (Enum.TryParse<InvoiceStatus>(q.Status, true, out var st)) query = query.Where(i => i.Status == st);
        if (string.Equals(q.Decision, "flagged", StringComparison.OrdinalIgnoreCase))
            query = query.Where(i => i.Decision != InvoiceDecision.AutoApprove);
        else if (Enum.TryParse<InvoiceDecision>(q.Decision, true, out var dec)) query = query.Where(i => i.Decision == dec);
        if (q.SupplierId is { } sid) query = query.Where(i => i.SupplierId == sid);
        if (q.From is { } from) query = query.Where(i => i.InvoiceDate >= from);
        if (q.To is { } to) query = query.Where(i => i.InvoiceDate <= to);
        if (q.MinAmount is { } min) { var c = Cents.From(min); query = query.Where(i => i.TotalCents >= c); }
        if (q.MaxAmount is { } max) { var c = Cents.From(max); query = query.Where(i => i.TotalCents <= c); }

        var desc = !string.Equals(q.Dir, "asc", StringComparison.OrdinalIgnoreCase);
        query = (q.Sort ?? "date").ToLowerInvariant() switch
        {
            "amount" => desc ? query.OrderByDescending(i => i.TotalCents) : query.OrderBy(i => i.TotalCents),
            "number" => desc ? query.OrderByDescending(i => i.Number) : query.OrderBy(i => i.Number),
            "supplier" => desc ? query.OrderByDescending(i => i.Supplier.Name) : query.OrderBy(i => i.Supplier.Name),
            "risk" => desc ? query.OrderByDescending(i => i.VerificationPoints) : query.OrderBy(i => i.VerificationPoints),
            _ => desc ? query.OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.CreatedAtUtc) : query.OrderBy(i => i.InvoiceDate).ThenBy(i => i.CreatedAtUtc),
        };

        var total = await query.CountAsync(ct);
        var rows = await query.Skip((page - 1) * size).Take(size)
            .Select(i => new { i.Id, i.Number, i.SupplierId, SupplierName = i.Supplier.Name, i.InvoiceDate, i.Currency, i.TotalCents,
                i.Status, i.Decision, i.VerificationPoints, i.DiscrepanciesJson, HasPdf = i.PdfStoredName != null, i.IsDemo })
            .ToListAsync(ct);
        var items = rows.Select(i => new InvoiceListItem(i.Id, i.Number, i.SupplierId, i.SupplierName, i.InvoiceDate, i.Currency,
            Cents.To(i.TotalCents), i.Status.ToString(), i.Decision.ToString(), i.VerificationPoints,
            Json.Deserialize<List<DiscrepancyDto>>(i.DiscrepanciesJson).Count, i.HasPdf, i.IsDemo)).ToList();
        return new(items, page, size, total);
    }

    public async Task<InvoiceDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var i = await db.Invoices.AsNoTracking().Include(x => x.Lines).Include(x => x.Supplier)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Invoice");

        var userIds = new[] { i.CreatedById, i.ReviewedById }.OfType<Guid>().ToList();
        var names = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var alerts = await AlertService.ProjectList(db.Alerts.AsNoTracking().Where(a => a.InvoiceId == id)).ToListAsync(ct);

        PurchaseOrderDto? poDto = null;
        if (i.PoNumber is not null)
        {
            var po = await db.PurchaseOrders.AsNoTracking().Include(p => p.Lines)
                .Where(p => p.Number == i.PoNumber).OrderByDescending(p => p.SupplierId == i.SupplierId).FirstOrDefaultAsync(ct);
            if (po is not null)
                poDto = new(po.Number, po.Currency, Cents.To(po.TotalCents), po.IssuedOn,
                    po.Lines.Select(l => new InvoiceLineDto(l.Description, l.Quantity, Cents.To(l.UnitPriceCents))).ToList());
        }

        var discrepancies = Json.Deserialize<List<DiscrepancyDto>>(i.DiscrepanciesJson);
        var lines = i.Lines.OrderBy(l => l.Description).Select(l => new InvoiceLineDto(l.Description, l.Quantity, Cents.To(l.UnitPriceCents))).ToList();
        var linesTotal = i.Lines.Sum(l => Math.Round(l.Quantity * Cents.To(l.UnitPriceCents), 2, MidpointRounding.AwayFromZero));
        string Name(Guid? g) => g is { } x && names.TryGetValue(x, out var n) ? n : (g is null ? "System (demo import)" : "Unknown user");

        return new InvoiceDetail(i.Id, i.Number, i.SupplierId, i.Supplier.Name, i.Supplier.RiskLevel, i.PoNumber, i.InvoiceDate, i.DueDate,
            i.Currency, Cents.To(i.TotalCents), linesTotal, i.Iban is null ? null : Iban.Mask(i.Iban), i.Status.ToString(), i.Decision.ToString(),
            i.VerificationPoints, discrepancies, Summary(i.Decision, discrepancies), i.VerifiedAtUtc,
            Name(i.CreatedById), i.ReviewedById is null ? null : Name(i.ReviewedById), i.ReviewNote, i.ReviewedAtUtc,
            i.PdfOriginalName, i.IsDemo, lines, alerts, poDto, Permissions(i));
    }

    public static string Summary(InvoiceDecision d, IReadOnlyList<DiscrepancyDto> list) => d switch
    {
        InvoiceDecision.AutoApprove => "All verification rules passed. The invoice matches its purchase order and shows no duplicate or arithmetic issues.",
        InvoiceDecision.Reject => $"Recommended for rejection: {list.Count(x => x.Severity == "Critical")} critical issue(s), including {string.Join(", ", list.Where(x => x.Severity == "Critical").Select(x => x.Code).Take(3))}.",
        _ => $"Needs manual review: {list.Count} issue(s) found ({list.Count(x => x.Severity == "Critical")} critical, {list.Count(x => x.Severity == "Warning")} warning).",
    };

    /// <summary>Server-side approval rules (also returned to the UI so buttons reflect the real policy).</summary>
    public InvoicePermissions Permissions(InvoiceEntity i)
    {
        if (i.Status != InvoiceStatus.Pending)
            return new(false, false, i.Status == InvoiceStatus.Approved && user.IsAdmin,
                i.Status == InvoiceStatus.Approved ? (user.IsAdmin ? null : "Only administrators can record payments.") : $"Invoice is already {i.Status}.");
        string? reason = null;
        if (i.CreatedById == user.Id) reason = "Segregation of duties: you cannot approve an invoice you submitted.";
        else if (!user.IsAdmin && i.Decision == InvoiceDecision.Reject) reason = "The system recommends rejection; only an administrator can override it.";
        else if (!user.IsAdmin && Cents.To(i.TotalCents) >= finance.Value.ApprovalThreshold)
            reason = $"Invoices of {finance.Value.ApprovalThreshold:N0} or more need administrator approval.";
        else if (!user.IsAdmin && i.Supplier?.RiskLevel is "Critical" or "High")
            reason = $"Supplier risk is {i.Supplier!.RiskLevel}; administrator approval required.";
        return new(reason is null, true, false, reason);
    }

    public async Task<Guid> CreateAsync(InvoiceCreateRequest r, CancellationToken ct)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == r.SupplierId, ct) ?? throw new NotFoundException("Supplier");
        if (supplier.Status == SupplierStatus.Archived) throw new BusinessRuleException("Invoices cannot be submitted for an archived supplier.");
        var iban = string.IsNullOrWhiteSpace(r.Iban) ? null : Iban.Normalize(r.Iban);
        var now = clock.GetUtcNow().UtcDateTime;
        var inv = new InvoiceEntity
        {
            Number = r.Number.Trim(), NormalizedNumber = InvoiceVerifier.NormalizeNumber(r.Number), SupplierId = supplier.Id,
            PoNumber = string.IsNullOrWhiteSpace(r.PoNumber) ? null : r.PoNumber.Trim(), InvoiceDate = r.InvoiceDate, DueDate = r.DueDate,
            Currency = r.Currency.ToUpperInvariant(), TotalCents = Cents.From(r.DeclaredTotal), Iban = iban, CreatedById = user.Id,
            CreatedAtUtc = now, UpdatedAtUtc = now,
            Lines = r.Lines.Select(l => new InvoiceLine { Description = l.Description.Trim(), Quantity = l.Quantity, UnitPriceCents = Cents.From(l.UnitPrice) }).ToList(),
        };
        db.Invoices.Add(inv);
        audit.Record("invoice.created", "Invoice", inv.Id.ToString(), $"{inv.Number} from {supplier.Name}");
        await db.SaveChangesAsync(ct);
        var result = await verifier.VerifyAsync(inv, ct);
        audit.Record("invoice.verified", "Invoice", inv.Id.ToString(), $"{inv.Number}: {result.Decision}, {result.Discrepancies.Count} discrepancies");
        await db.SaveChangesAsync(ct);
        return inv.Id;
    }

    public async Task ReverifyAsync(Guid id, CancellationToken ct)
    {
        var inv = await db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Invoice");
        if (inv.Status != InvoiceStatus.Pending) throw new BusinessRuleException("Only pending invoices can be re-verified.");
        var result = await verifier.VerifyAsync(inv, ct);
        audit.Record("invoice.reverified", "Invoice", inv.Id.ToString(), $"{inv.Number}: {result.Decision}");
        await db.SaveChangesAsync(ct);
    }

    public async Task ApproveAsync(Guid id, string? note, CancellationToken ct)
    {
        var inv = await db.Invoices.Include(i => i.Supplier).FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Invoice");
        var p = Permissions(inv);
        if (!p.CanApprove) throw new ForbiddenException(p.Reason ?? "You cannot approve this invoice.");
        if (inv.Decision != InvoiceDecision.AutoApprove && string.IsNullOrWhiteSpace(note))
            throw new BusinessRuleException("A review note is required when approving an invoice that was flagged.", "note_required");
        Review(inv, InvoiceStatus.Approved, note);
        audit.Record(inv.Decision == InvoiceDecision.AutoApprove ? "invoice.approved" : "invoice.approved_override", "Invoice", inv.Id.ToString(), $"{inv.Number}: {note}");
        await db.SaveChangesAsync(ct);
    }

    public async Task RejectAsync(Guid id, string? note, CancellationToken ct)
    {
        var inv = await db.Invoices.Include(i => i.Supplier).FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Invoice");
        if (inv.Status != InvoiceStatus.Pending) throw new BusinessRuleException($"Invoice is already {inv.Status}.");
        if (string.IsNullOrWhiteSpace(note)) throw new BusinessRuleException("A reason is required to reject an invoice.", "note_required");
        Review(inv, InvoiceStatus.Rejected, note);
        audit.Record("invoice.rejected", "Invoice", inv.Id.ToString(), $"{inv.Number}: {note}");
        await db.SaveChangesAsync(ct);
        await risk.AssessAsync(inv.SupplierId, $"Invoice {inv.Number} rejected", ct);
    }

    public async Task MarkPaidAsync(Guid id, CancellationToken ct)
    {
        if (!user.IsAdmin) throw new ForbiddenException("Only administrators can record payments.");
        var inv = await db.Invoices.Include(i => i.Supplier).FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Invoice");
        if (inv.Status != InvoiceStatus.Approved) throw new BusinessRuleException("Only approved invoices can be marked as paid.");
        inv.Status = InvoiceStatus.Paid;
        inv.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        db.Payments.Add(new Payment
        {
            SupplierId = inv.SupplierId, InvoiceId = inv.Id, AmountCents = inv.TotalCents, Currency = inv.Currency,
            Iban = inv.Iban ?? inv.Supplier.Iban, PaidOn = DateOnly.FromDateTime(inv.UpdatedAtUtc), Reference = $"PAY-{inv.Number}",
            RecordedById = user.Id, IsDemo = inv.IsDemo,
        });
        audit.Record("invoice.paid", "Invoice", inv.Id.ToString(), $"Payment recorded for {inv.Number} (simulated, no money moved)");
        await db.SaveChangesAsync(ct);
    }

    private void Review(InvoiceEntity inv, InvoiceStatus status, string? note)
    {
        inv.Status = status;
        inv.ReviewedById = user.Id;
        inv.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        inv.ReviewedAtUtc = clock.GetUtcNow().UtcDateTime;
        inv.UpdatedAtUtc = inv.ReviewedAtUtc.Value;
    }

    public async Task AttachPdfAsync(Guid id, IFormFile file, CancellationToken ct)
    {
        var inv = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Invoice");
        var stored = await files.SavePdfAsync(file, ct);
        if (inv.PdfStoredName is not null) files.Delete(inv.PdfStoredName);
        inv.PdfStoredName = stored;
        inv.PdfOriginalName = FileStorage.SafeDisplayName(file.FileName);
        inv.PdfSizeBytes = file.Length;
        inv.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record("invoice.pdf_attached", "Invoice", inv.Id.ToString(), inv.PdfOriginalName);
        await db.SaveChangesAsync(ct);
    }

    public async Task<(Stream stream, string name)> OpenPdfAsync(Guid id, CancellationToken ct)
    {
        var inv = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Invoice");
        if (inv.PdfStoredName is null) throw new NotFoundException("Invoice PDF");
        audit.Record("invoice.pdf_downloaded", "Invoice", inv.Id.ToString());
        await db.SaveChangesAsync(ct);
        return (files.Open(inv.PdfStoredName), inv.PdfOriginalName ?? "invoice.pdf");
    }
}
