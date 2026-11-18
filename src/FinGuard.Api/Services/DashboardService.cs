using System.Globalization;
using FinGuard.Api.Contracts;
using FinGuard.Api.Data;
using FinGuard.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinGuard.Api.Services;

/// <summary>Every dashboard figure is computed here from stored rows; the UI only renders it.</summary>
public class DashboardService(AppDbContext db, IOptions<FinanceOptions> finance, TimeProvider clock, Infrastructure.ICurrentUser user)
{
    public async Task<DashboardDto> GetAsync(CancellationToken ct)
    {
        var baseCcy = finance.Value.BaseCurrency;
        var now = clock.GetUtcNow().UtcDateTime;

        var invoices = await db.Invoices.AsNoTracking()
            .Select(i => new { i.InvoiceDate, i.Currency, i.TotalCents, i.Status, i.Decision, i.DiscrepanciesJson, Category = i.Supplier.Category })
            .ToListAsync(ct);
        var alerts = await db.Alerts.AsNoTracking().Select(a => new { a.Severity, a.Status, a.ExposureCents }).ToListAsync(ct);
        var suppliers = await db.Suppliers.AsNoTracking().Where(s => s.Status == SupplierStatus.Active)
            .Select(s => new { s.Id, s.Name, s.RiskScore, s.RiskLevel }).ToListAsync(ct);

        var baseInv = invoices.Where(i => i.Currency == baseCcy).ToList();
        bool Flagged(InvoiceDecision d) => d != InvoiceDecision.AutoApprove;
        bool IsOpen(AlertStatus s) => !AlertWorkflow.IsTerminal(s);
        var openAlerts = alerts.Where(a => IsOpen(a.Status)).ToList();

        var kpis = new KpiDto(
            TotalInvoices: invoices.Count,
            VerifiedInvoices: invoices.Count(i => i.Decision == InvoiceDecision.AutoApprove),
            FlaggedInvoices: invoices.Count(i => Flagged(i.Decision)),
            RejectedBySystem: invoices.Count(i => i.Decision == InvoiceDecision.Reject),
            PendingReview: invoices.Count(i => i.Status == InvoiceStatus.Pending),
            TotalProcessed: Cents.To(baseInv.Sum(i => i.TotalCents)),
            // Exposure = money still payable on invoices the rules flagged.
            FinancialExposure: Cents.To(baseInv.Where(i => Flagged(i.Decision) && i.Status is InvoiceStatus.Pending or InvoiceStatus.Approved).Sum(i => i.TotalCents)),
            OpenAlertExposure: Cents.To(openAlerts.Sum(a => a.ExposureCents)),
            HighRiskSuppliers: suppliers.Count(s => s.RiskLevel is "High" or "Critical"),
            ActiveSuppliers: suppliers.Count,
            OpenAlerts: openAlerts.Count,
            CriticalAlerts: openAlerts.Count(a => a.Severity == AlertSeverity.Critical),
            ConfirmedFraud: alerts.Count(a => a.Status == AlertStatus.ConfirmedFraud),
            BaseCurrency: baseCcy,
            OtherCurrencies: invoices.Select(i => i.Currency).Where(c => c != baseCcy).Distinct().Order().ToList());

        var firstMonth = new DateOnly(now.Year, now.Month, 1).AddMonths(-11);
        var trend = Enumerable.Range(0, 12).Select(k =>
        {
            var start = firstMonth.AddMonths(k);
            var inMonth = baseInv.Where(i => i.InvoiceDate >= start && i.InvoiceDate < start.AddMonths(1)).ToList();
            return new TrendPoint(start.ToString("yyyy-MM", CultureInfo.InvariantCulture), inMonth.Count, inMonth.Count(i => Flagged(i.Decision)),
                Cents.To(inMonth.Sum(i => i.TotalCents)), Cents.To(inMonth.Where(i => Flagged(i.Decision)).Sum(i => i.TotalCents)));
        }).ToList();

        string[] levels = ["Low", "Medium", "High", "Critical"];
        var riskDist = levels.Select(l => new NamedCount(l, suppliers.Count(s => s.RiskLevel == l))).ToList();
        var bySeverity = Enum.GetValues<AlertSeverity>().OrderByDescending(s => s).Select(s => new NamedCount(s.ToString(), openAlerts.Count(a => a.Severity == s))).ToList();
        var byStatus = Enum.GetValues<AlertStatus>().Select(s => new NamedCount(s.ToString(), alerts.Count(a => a.Status == s))).ToList();

        var topDiscrepancies = invoices.SelectMany(i => Json.Deserialize<List<DiscrepancyDto>>(i.DiscrepanciesJson))
            .GroupBy(d => d.Code).Select(g => new NamedCount(g.Key, g.Count())).OrderByDescending(x => x.Count).Take(8).ToList();

        var spend = baseInv.GroupBy(i => i.Category).Select(g => new NamedCount(g.Key, (int)Math.Round(Cents.To(g.Sum(i => i.TotalCents)))))
            .OrderByDescending(x => x.Count).Take(8).ToList();

        var alertCounts = await db.Alerts.AsNoTracking().Where(AlertQueries.IsOpen).GroupBy(a => a.SupplierId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var top = suppliers.OrderByDescending(s => s.RiskScore).ThenBy(s => s.Name).Take(6)
            .Select(s => new TopRiskSupplier(s.Id, s.Name, s.RiskScore, s.RiskLevel, alertCounts.GetValueOrDefault(s.Id))).ToList();

        // The activity feed shows business events. Sign-ins, account and user-admin events are admin-only (audit log).
        var activityQuery = db.AuditLog.AsNoTracking()
            .Where(a => a.Action != "auth.login_failed" && a.Action != "invoice.pdf_downloaded");
        if (!user.IsAdmin)
            activityQuery = activityQuery.Where(a => !a.Action.StartsWith("auth.") && !a.Action.StartsWith("user.")
                                                     && !a.Action.StartsWith("account.") && !a.Action.StartsWith("assistant."));
        var activity = await activityQuery
            .OrderByDescending(a => a.Id).Take(12)
            .Select(a => new ActivityItem(a.AtUtc, a.UserEmail, a.Action, a.EntityType, a.EntityId, a.Details)).ToListAsync(ct);

        return new DashboardDto(kpis, trend, riskDist, bySeverity, byStatus, topDiscrepancies, top, activity, spend, now);
    }
}
