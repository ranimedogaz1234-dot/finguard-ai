using System.Linq.Expressions;
using System.Text.Json;
using FinGuard.Api.Contracts;
using FinGuard.Api.Data;
using FinGuard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinGuard.Api.Services;

public class FinanceOptions
{
    /// <summary>Invoices at or above this amount need administrator approval (and drive threshold-related fraud rules).</summary>
    public decimal ApprovalThreshold { get; set; } = 10_000m;
    public string BaseCurrency { get; set; } = "EUR";
}

public static class AlertQueries
{
    public static readonly Expression<Func<FraudAlert, bool>> IsOpen = a =>
        a.Status != AlertStatus.Dismissed && a.Status != AlertStatus.Resolved && a.Status != AlertStatus.ConfirmedFraud;

    public static readonly Expression<Func<FraudAlert, int>> SeverityRank = a =>
        a.Severity == AlertSeverity.Critical ? 4 : a.Severity == AlertSeverity.High ? 3 : a.Severity == AlertSeverity.Medium ? 2 : 1;

    public static readonly string[] DuplicateCodes = ["DUPLICATE_INVOICE", "POSSIBLE_DUPLICATE"];
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) where T : new() =>
        string.IsNullOrWhiteSpace(json) ? new T() : JsonSerializer.Deserialize<T>(json, Options) ?? new T();
}

public class RiskService(AppDbContext db, TimeProvider clock)
{
    public async Task<SupplierStats> ComputeStatsAsync(Guid supplierId, DateTime? asOfUtc, CancellationToken ct)
    {
        var asOf = asOfUtc ?? clock.GetUtcNow().UtcDateTime;
        var asOfDate = DateOnly.FromDateTime(asOf);
        var supplier = await db.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supplierId, ct);

        var invoices = await db.Invoices.AsNoTracking()
            .Where(i => i.SupplierId == supplierId && i.CreatedAtUtc <= asOf)
            .Select(i => new { i.Id, i.Decision, i.Status, i.PoNumber, Lines = i.Lines.Select(l => new { l.Description, l.UnitPriceCents }).ToList() })
            .ToListAsync(ct);

        var alerts = await db.Alerts.AsNoTracking()
            .Where(a => a.SupplierId == supplierId && a.CreatedAtUtc <= asOf)
            .Select(a => new { a.Code, a.Severity, a.Status })
            .ToListAsync(ct);

        var bankChanges = await db.BankDetailChanges.AsNoTracking()
            .CountAsync(b => b.SupplierId == supplierId && b.ChangedAtUtc <= asOf && b.ChangedAtUtc >= asOf.AddDays(-90), ct);

        // Price variance vs. purchase orders (only increases count as risk).
        var poNumbers = invoices.Where(i => i.PoNumber != null).Select(i => i.PoNumber!).Distinct().ToList();
        var pos = await db.PurchaseOrders.AsNoTracking()
            .Where(p => p.SupplierId == supplierId && poNumbers.Contains(p.Number))
            .Select(p => new { p.Number, Lines = p.Lines.Select(l => new { l.Description, l.UnitPriceCents }).ToList() })
            .ToListAsync(ct);
        var variances = new List<decimal>();
        foreach (var inv in invoices.Where(i => i.PoNumber != null))
        {
            var po = pos.FirstOrDefault(p => p.Number == inv.PoNumber);
            if (po is null) continue;
            foreach (var line in inv.Lines)
            {
                var pl = po.Lines.FirstOrDefault(x => string.Equals(x.Description, line.Description, StringComparison.OrdinalIgnoreCase));
                if (pl is null || pl.UnitPriceCents == 0) continue;
                variances.Add(Math.Max(0, (line.UnitPriceCents - pl.UnitPriceCents) * 100m / pl.UnitPriceCents));
            }
        }

        bool IsOpen(AlertStatus s) => !AlertWorkflow.IsTerminal(s);
        return new SupplierStats(
            TotalInvoices: invoices.Count,
            RejectedInvoices: invoices.Count(i => i.Status == InvoiceStatus.Rejected || (i.Decision == InvoiceDecision.Reject && i.Status == InvoiceStatus.Pending)),
            DuplicateFlags: alerts.Count(a => AlertQueries.DuplicateCodes.Contains(a.Code) && a.Status != AlertStatus.Dismissed),
            OpenHighSeverityAlerts: alerts.Count(a => IsOpen(a.Status) && a.Severity is AlertSeverity.High or AlertSeverity.Critical),
            ConfirmedFraudCases: alerts.Count(a => a.Status == AlertStatus.ConfirmedFraud),
            BankDetailChanges90Days: bankChanges,
            AccountAgeDays: Math.Max(0, asOfDate.DayNumber - supplier.OnboardedOn.DayNumber),
            AvgPriceVariancePct: variances.Count == 0 ? 0 : Math.Round(variances.Average(), 2));
    }

    /// <summary>Recomputes the supplier's score from stored data. Saves changes.</summary>
    public async Task<RiskAssessment> AssessAsync(Guid supplierId, string trigger, CancellationToken ct, DateTime? asOfUtc = null, bool recordHistory = true)
    {
        var stats = await ComputeStatsAsync(supplierId, asOfUtc, ct);
        var result = SupplierRiskScorer.Assess(stats);
        var at = asOfUtc ?? clock.GetUtcNow().UtcDateTime;
        var supplier = await db.Suppliers.FirstAsync(s => s.Id == supplierId, ct);
        supplier.RiskScore = result.Score;
        supplier.RiskLevel = result.Level;
        supplier.RiskAssessedAtUtc = at;
        if (recordHistory)
            db.RiskAssessments.Add(new RiskAssessmentRecord
            {
                SupplierId = supplierId, Score = result.Score, Level = result.Level, Confidence = result.Confidence,
                Recommendation = result.Recommendation, FactorsJson = Json.Serialize(result.Factors), Trigger = trigger, AssessedAtUtc = at,
            });
        await db.SaveChangesAsync(ct);
        return result;
    }

    public static RiskAssessmentDto ToDto(RiskAssessmentRecord r) =>
        new(r.Score, r.Level, r.Confidence, r.Recommendation, Json.Deserialize<List<RiskFactor>>(r.FactorsJson), r.Trigger, r.AssessedAtUtc);
}

public class VerificationService(AppDbContext db, RiskService risk, IOptions<FinanceOptions> finance, TimeProvider clock)
{
    private readonly InvoiceVerifier _verifier = new();

    public static Invoice ToDomain(InvoiceEntity e) => new(e.Number, e.SupplierId, e.InvoiceDate, e.Currency,
        e.Lines.Select(l => new LineItem(l.Description, l.Quantity, Cents.To(l.UnitPriceCents))).ToList(), Cents.To(e.TotalCents), e.PoNumber);

    /// <summary>
    /// Runs the verification rule engine and the fraud rules for an invoice, persists the result,
    /// raises alerts for new signals and recomputes the supplier risk score.
    /// </summary>
    /// <param name="inv">The tracked invoice entity (with lines) to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="atUtc">Evaluation time; defaults to now. The demo seeder replays history with past timestamps.</param>
    /// <param name="recordRiskHistory">Whether the resulting supplier risk assessment is stored as a history point.</param>
    public async Task<VerificationResult> VerifyAsync(InvoiceEntity inv, CancellationToken ct, DateTime? atUtc = null, bool recordRiskHistory = true)
    {
        var now = atUtc ?? clock.GetUtcNow().UtcDateTime;
        var todayDate = DateOnly.FromDateTime(now);
        var supplier = await db.Suppliers.FirstAsync(s => s.Id == inv.SupplierId, ct);

        PurchaseOrder? po = null;
        if (!string.IsNullOrWhiteSpace(inv.PoNumber))
        {
            var poEntity = await db.PurchaseOrders.AsNoTracking().Include(p => p.Lines)
                .Where(p => p.Number == inv.PoNumber)
                .OrderByDescending(p => p.SupplierId == inv.SupplierId)
                .FirstOrDefaultAsync(ct);
            if (poEntity is not null)
                po = new PurchaseOrder(poEntity.Number, poEntity.SupplierId, poEntity.Currency,
                    poEntity.Lines.Select(l => new LineItem(l.Description, l.Quantity, Cents.To(l.UnitPriceCents))).ToList(), Cents.To(poEntity.TotalCents));
        }

        // Only invoices submitted before this one count as history, so the original of a duplicate stays clean.
        var history = await db.Invoices.AsNoTracking()
            .Where(h => h.SupplierId == inv.SupplierId && h.Id != inv.Id && h.CreatedAtUtc < inv.CreatedAtUtc)
            .Select(h => new { h.Id, h.Number, h.InvoiceDate, h.TotalCents, h.Currency, h.Iban })
            .ToListAsync(ct);

        var result = _verifier.Verify(ToDomain(inv),
            po, history.Select(h => new Invoice(h.Number, inv.SupplierId, h.InvoiceDate, h.Currency, [], Cents.To(h.TotalCents), null)));

        inv.Decision = result.Decision;
        inv.VerificationPoints = result.RiskPoints;
        inv.DiscrepanciesJson = Json.Serialize(result.Discrepancies.Select(d => new DiscrepancyDto(d.Code, d.Severity.ToString(), d.Explanation)));
        inv.VerifiedAtUtc = now;
        inv.UpdatedAtUtc = now;

        var payments = await db.Payments.AsNoTracking().Where(p => p.SupplierId == inv.SupplierId)
            .Select(p => new PaymentSnapshot(p.InvoiceId, Cents.To(p.AmountCents), p.PaidOn, p.Iban)).ToListAsync(ct);

        var snapshot = new InvoiceSnapshot(inv.Id, inv.Number, inv.InvoiceDate, Cents.To(inv.TotalCents), inv.Currency, inv.Iban);
        var signals = FraudDetector.Analyze(new FraudContext(snapshot,
                new SupplierProfile(supplier.Id, supplier.Name, supplier.Iban, supplier.IbanChangedAtUtc),
                history.Select(h => new InvoiceSnapshot(h.Id, h.Number, h.InvoiceDate, Cents.To(h.TotalCents), h.Currency, h.Iban)).ToList(),
                payments, finance.Value.ApprovalThreshold, todayDate))
            .Concat(result.Discrepancies.Select(d => FraudDetector.FromDiscrepancy(d, snapshot)).OfType<FraudSignal>())
            .ToList();

        var existingCodes = await db.Alerts.Where(a => a.InvoiceId == inv.Id).Select(a => a.Code).ToListAsync(ct);
        var nextNumber = Math.Max(await db.Alerts.MaxAsync(a => (int?)a.Number, ct) ?? 1000,
                                  db.Alerts.Local.Select(a => a.Number).DefaultIfEmpty(1000).Max());
        foreach (var s in signals.Where(s => !existingCodes.Contains(s.Code)))
        {
            var alert = new FraudAlert
            {
                Number = ++nextNumber, Code = s.Code, Severity = s.Severity, Title = s.Title, Explanation = s.Explanation,
                EvidenceJson = Json.Serialize(s.Evidence), SupplierId = inv.SupplierId, InvoiceId = inv.Id,
                ExposureCents = inv.TotalCents, IsDemo = inv.IsDemo, CreatedAtUtc = now, UpdatedAtUtc = now,
            };
            alert.Events.Add(new AlertEvent { Kind = "created", ToStatus = nameof(AlertStatus.Open), Note = "Raised automatically by rule " + s.Code, AtUtc = now });
            db.Alerts.Add(alert);
        }
        await db.SaveChangesAsync(ct);
        await risk.AssessAsync(inv.SupplierId, $"Invoice {inv.Number} verified", ct, atUtc, recordRiskHistory);
        return result;
    }
}
