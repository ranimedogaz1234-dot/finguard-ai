using FinGuard.Api.Contracts;
using FinGuard.Api.Data;
using FinGuard.Api.Infrastructure;
using FinGuard.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinGuard.Api.Services;

public record SupplierQuery(string? Q, string? Status, string? Level, string? Category, string? Country,
    string? Sort, string? Dir, int? Page, int? PageSize);

public class SupplierService(AppDbContext db, RiskService risk, IAuditLog audit, ICurrentUser user, TimeProvider clock)
{
    public async Task<PagedResult<SupplierListItem>> ListAsync(SupplierQuery q, CancellationToken ct)
    {
        var (page, size) = Paging.Normalize(q.Page, q.PageSize);
        var query = db.Suppliers.AsNoTracking().AsQueryable();

        query = (q.Status ?? "active").ToLowerInvariant() switch
        {
            "archived" => query.Where(s => s.Status == SupplierStatus.Archived),
            "all" => query,
            _ => query.Where(s => s.Status == SupplierStatus.Active),
        };
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var term = q.Q.Trim().ToLower();
            query = query.Where(s => s.Name.ToLower().Contains(term) || s.Category.ToLower().Contains(term)
                                     || (s.TaxId != null && s.TaxId.ToLower().Contains(term)));
        }
        if (!string.IsNullOrWhiteSpace(q.Level)) query = query.Where(s => s.RiskLevel == q.Level);
        if (!string.IsNullOrWhiteSpace(q.Category)) query = query.Where(s => s.Category == q.Category);
        if (!string.IsNullOrWhiteSpace(q.Country)) query = query.Where(s => s.Country == q.Country.ToUpper());

        var desc = !string.Equals(q.Dir, "asc", StringComparison.OrdinalIgnoreCase);
        query = (q.Sort ?? "risk").ToLowerInvariant() switch
        {
            "name" => desc ? query.OrderByDescending(s => s.Name) : query.OrderBy(s => s.Name),
            "spend" => desc ? query.OrderByDescending(s => s.AnnualSpendCents) : query.OrderBy(s => s.AnnualSpendCents),
            "updated" => desc ? query.OrderByDescending(s => s.UpdatedAtUtc) : query.OrderBy(s => s.UpdatedAtUtc),
            "country" => desc ? query.OrderByDescending(s => s.Country) : query.OrderBy(s => s.Country),
            _ => desc ? query.OrderByDescending(s => s.RiskScore).ThenBy(s => s.Name) : query.OrderBy(s => s.RiskScore).ThenBy(s => s.Name),
        };

        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * size).Take(size)
            .Select(s => new SupplierListItem(s.Id, s.Name, s.Country, s.Category, s.Currency, s.RiskScore, s.RiskLevel,
                s.Status.ToString(), Cents.To(s.AnnualSpendCents),
                db.Invoices.Count(i => i.SupplierId == s.Id),
                db.Alerts.Count(a => a.SupplierId == s.Id && a.Status != AlertStatus.Dismissed && a.Status != AlertStatus.Resolved && a.Status != AlertStatus.ConfirmedFraud),
                s.IsDemo, s.UpdatedAtUtc))
            .ToListAsync(ct);
        return new(items, page, size, total);
    }

    public async Task<SupplierDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Supplier");
        var latest = await db.RiskAssessments.AsNoTracking().Where(r => r.SupplierId == id)
            .OrderByDescending(r => r.AssessedAtUtc).FirstOrDefaultAsync(ct);

        var inv = await db.Invoices.AsNoTracking().Where(i => i.SupplierId == id)
            .Select(i => new { i.TotalCents, i.Status, i.Decision }).ToListAsync(ct);
        var openAlerts = await db.Alerts.CountAsync(a => a.SupplierId == id && a.Status != AlertStatus.Dismissed
                                                         && a.Status != AlertStatus.Resolved && a.Status != AlertStatus.ConfirmedFraud, ct);
        var fin = new SupplierFinancials(
            inv.Count,
            Cents.To(inv.Sum(i => i.TotalCents)),
            Cents.To(inv.Where(i => i.Status is InvoiceStatus.Pending or InvoiceStatus.Approved).Sum(i => i.TotalCents)),
            Cents.To(inv.Where(i => i.Status == InvoiceStatus.Paid).Sum(i => i.TotalCents)),
            inv.Count(i => i.Decision != InvoiceDecision.AutoApprove),
            openAlerts,
            inv.Count == 0 ? 0 : Math.Round(Cents.To(inv.Sum(i => i.TotalCents)) / inv.Count, 2));

        return new SupplierDetail(s.Id, s.Name, s.Country, s.Category, s.TaxId, s.ContactEmail, s.ContactPhone, s.Address,
            s.Iban is null ? null : Iban.Mask(s.Iban), Iban.IsValid(s.Iban), s.IbanChangedAtUtc, s.Currency,
            Cents.To(s.AnnualSpendCents), s.PaymentTermsDays, s.Notes, s.Status.ToString(), s.OnboardedOn, s.IsDemo,
            latest is null ? null : RiskService.ToDto(latest), fin);
    }

    public async Task<Guid> CreateAsync(SupplierUpsertRequest r, CancellationToken ct)
    {
        var name = r.Name.Trim();
        if (await db.Suppliers.AnyAsync(s => s.Name.ToLower() == name.ToLower(), ct))
            throw new ConflictException($"A supplier named '{name}' already exists.");
        var iban = ValidateIban(r.Iban);
        var now = clock.GetUtcNow().UtcDateTime;
        var s = new Supplier { OnboardedOn = DateOnly.FromDateTime(now), Iban = iban, CreatedAtUtc = now, UpdatedAtUtc = now };
        Apply(s, r);
        db.Suppliers.Add(s);
        audit.Record("supplier.created", "Supplier", s.Id.ToString(), s.Name);
        await db.SaveChangesAsync(ct);
        await risk.AssessAsync(s.Id, "Supplier onboarded", ct);
        return s.Id;
    }

    public async Task UpdateAsync(Guid id, SupplierUpsertRequest r, CancellationToken ct)
    {
        var s = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Supplier");
        if (s.Status == SupplierStatus.Archived) throw new BusinessRuleException("Archived suppliers cannot be edited. Restore it first.");
        var name = r.Name.Trim();
        if (await db.Suppliers.AnyAsync(x => x.Id != id && x.Name.ToLower() == name.ToLower(), ct))
            throw new ConflictException($"A supplier named '{name}' already exists.");

        // The API only ever exposes masked IBANs, so edits that keep the account say so explicitly.
        var newIban = r.KeepExistingIban ? s.Iban : ValidateIban(r.Iban);
        var bankChanged = Iban.Normalize(newIban) != Iban.Normalize(s.Iban);
        var now = clock.GetUtcNow().UtcDateTime;
        if (bankChanged)
        {
            db.BankDetailChanges.Add(new BankDetailChange
            {
                SupplierId = s.Id, OldIbanMasked = s.Iban is null ? null : Iban.Mask(s.Iban),
                NewIbanMasked = newIban is null ? null : Iban.Mask(newIban), ChangedAtUtc = now, ChangedById = user.Id,
            });
            s.Iban = newIban;
            s.IbanChangedAtUtc = now;
            audit.Record("supplier.bank_details_changed", "Supplier", s.Id.ToString(), $"{s.Name}: bank account changed");
        }
        Apply(s, r);
        s.UpdatedAtUtc = now;
        audit.Record("supplier.updated", "Supplier", s.Id.ToString(), s.Name);
        await db.SaveChangesAsync(ct);
        if (bankChanged) await risk.AssessAsync(s.Id, "Bank details changed", ct);
    }

    public async Task SetArchivedAsync(Guid id, bool archived, CancellationToken ct)
    {
        if (!user.IsAdmin) throw new ForbiddenException("Only administrators can archive or restore suppliers.");
        var s = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Supplier");
        s.Status = archived ? SupplierStatus.Archived : SupplierStatus.Active;
        s.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(archived ? "supplier.archived" : "supplier.restored", "Supplier", s.Id.ToString(), s.Name);
        await db.SaveChangesAsync(ct);
    }

    public async Task<RiskAssessmentDto> ReassessAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Suppliers.AnyAsync(s => s.Id == id, ct)) throw new NotFoundException("Supplier");
        await risk.AssessAsync(id, $"Manual reassessment by {user.Name}", ct);
        audit.Record("supplier.risk_reassessed", "Supplier", id.ToString());
        await db.SaveChangesAsync(ct);
        var latest = await db.RiskAssessments.AsNoTracking().Where(r => r.SupplierId == id).OrderByDescending(r => r.AssessedAtUtc).FirstAsync(ct);
        return RiskService.ToDto(latest);
    }

    public async Task<IReadOnlyList<RiskHistoryPoint>> RiskHistoryAsync(Guid id, CancellationToken ct) =>
        await db.RiskAssessments.AsNoTracking().Where(r => r.SupplierId == id).OrderBy(r => r.AssessedAtUtc)
            .Select(r => new RiskHistoryPoint(r.AssessedAtUtc, r.Score, r.Level, r.Trigger)).ToListAsync(ct);

    public async Task<IReadOnlyList<BankChangeDto>> BankChangesAsync(Guid id, CancellationToken ct) =>
        await db.BankDetailChanges.AsNoTracking().Where(b => b.SupplierId == id).OrderByDescending(b => b.ChangedAtUtc)
            .Select(b => new BankChangeDto(b.ChangedAtUtc, b.OldIbanMasked, b.NewIbanMasked)).ToListAsync(ct);

    public async Task<IReadOnlyList<SupplierDetail>> CompareAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count is < 2 or > 4) throw new BusinessRuleException("Select between 2 and 4 suppliers to compare.");
        var list = new List<SupplierDetail>();
        foreach (var id in distinct) list.Add(await GetAsync(id, ct));
        return list;
    }

    public async Task<object> FacetsAsync(CancellationToken ct) => new
    {
        categories = await db.Suppliers.Select(s => s.Category).Distinct().OrderBy(c => c).ToListAsync(ct),
        countries = await db.Suppliers.Select(s => s.Country).Distinct().OrderBy(c => c).ToListAsync(ct),
    };

    private static string? ValidateIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return null;
        var n = Iban.Normalize(iban);
        if (!Iban.IsValid(n)) throw new BusinessRuleException("The IBAN is not valid (ISO 13616 checksum failed).", "invalid_iban");
        return n;
    }

    private static void Apply(Supplier s, SupplierUpsertRequest r)
    {
        s.Name = r.Name.Trim();
        s.Country = r.Country.ToUpperInvariant();
        s.Category = r.Category.Trim();
        s.TaxId = Clean(r.TaxId);
        s.ContactEmail = Clean(r.ContactEmail);
        s.ContactPhone = Clean(r.ContactPhone);
        s.Address = Clean(r.Address);
        s.Currency = r.Currency.ToUpperInvariant();
        s.AnnualSpendCents = Cents.From(r.AnnualSpend);
        s.PaymentTermsDays = r.PaymentTermsDays;
        s.Notes = Clean(r.Notes);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
