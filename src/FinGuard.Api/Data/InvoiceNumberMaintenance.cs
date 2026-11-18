using FinGuard.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinGuard.Api.Data;

/// <summary>Recomputes stored normalised invoice numbers when the normalisation rules change. Idempotent.</summary>
public static class InvoiceNumberMaintenance
{
    public static async Task<int> RefreshAsync(AppDbContext db, ILogger log, CancellationToken ct = default)
    {
        var rows = await db.Invoices.Select(i => new { i.Id, i.Number, i.NormalizedNumber }).ToListAsync(ct);
        var stale = rows.Where(r => r.NormalizedNumber != InvoiceVerifier.NormalizeNumber(r.Number)).ToList();
        foreach (var r in stale)
        {
            var normalized = InvoiceVerifier.NormalizeNumber(r.Number);
            await db.Invoices.Where(i => i.Id == r.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.NormalizedNumber, normalized), ct);
        }
        if (stale.Count > 0) log.LogInformation("Refreshed {Count} normalised invoice number(s).", stale.Count);
        return stale.Count;
    }
}
