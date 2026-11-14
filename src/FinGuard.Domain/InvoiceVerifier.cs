namespace FinGuard.Domain;

/// <summary>Deterministic, explainable rule engine for invoice verification.</summary>
public class InvoiceVerifier
{
    private const decimal Tolerance = 0.01m;       // rounding tolerance
    private const decimal OverPoThreshold = 0.05m; // 5% over PO needs review

    public VerificationResult Verify(Invoice inv, PurchaseOrder? po, IEnumerable<Invoice> history)
    {
        var d = new List<Discrepancy>();

        // 1. Duplicate detection: same supplier + number, or same supplier + total + date.
        if (history.Any(h => h.SupplierId == inv.SupplierId &&
            (string.Equals(h.Number, inv.Number, StringComparison.OrdinalIgnoreCase) ||
             (h.DeclaredTotal == inv.DeclaredTotal && h.Date == inv.Date))))
            d.Add(new("DUPLICATE", Severity.Critical,
                "A matching invoice from this supplier (same number, or same amount and date) already exists."));

        // 2. Internal arithmetic.
        var computed = inv.Lines.Sum(l => l.Total);
        if (Math.Abs(computed - inv.DeclaredTotal) > Tolerance)
            d.Add(new("TOTAL_MISMATCH", Severity.Critical,
                $"Declared total {inv.DeclaredTotal:F2} differs from line-item sum {computed:F2}."));

        // 3. Purchase order matching.
        if (po is null)
            d.Add(new("NO_PO", Severity.Warning, "No purchase order was referenced or found."));
        else
        {
            if (po.SupplierId != inv.SupplierId)
                d.Add(new("SUPPLIER_MISMATCH", Severity.Critical, "Invoice supplier does not match the purchase order."));
            if (po.Currency != inv.Currency)
                d.Add(new("CURRENCY_MISMATCH", Severity.Critical, $"Invoice currency {inv.Currency} differs from PO currency {po.Currency}."));
            if (po.Total > 0 && inv.DeclaredTotal > po.Total * (1 + OverPoThreshold))
                d.Add(new("OVER_PO", Severity.Critical,
                    $"Invoice total {inv.DeclaredTotal:F2} exceeds PO total {po.Total:F2} by more than {OverPoThreshold:P0}."));
            else if (inv.DeclaredTotal > po.Total + Tolerance)
                d.Add(new("SLIGHT_OVER_PO", Severity.Warning, "Invoice total is slightly above the PO total."));

            foreach (var line in inv.Lines)
                if (!po.Lines.Any(p => p.Description.Equals(line.Description, StringComparison.OrdinalIgnoreCase)))
                    d.Add(new("UNORDERED_ITEM", Severity.Warning, $"Line '{line.Description}' does not appear on the PO."));
                else
                {
                    var p = po.Lines.First(x => x.Description.Equals(line.Description, StringComparison.OrdinalIgnoreCase));
                    if (line.UnitPrice > p.UnitPrice + Tolerance)
                        d.Add(new("PRICE_INCREASE", Severity.Warning,
                            $"'{line.Description}' unit price {line.UnitPrice:F2} exceeds PO price {p.UnitPrice:F2}."));
                }
        }

        var points = d.Sum(x => x.Severity switch { Severity.Critical => 40, Severity.Warning => 15, _ => 0 });
        var decision = d.Any(x => x.Code is "DUPLICATE" or "TOTAL_MISMATCH") ? InvoiceDecision.Reject
            : d.Any(x => x.Severity == Severity.Critical) || points >= 30 ? InvoiceDecision.ManualReview
            : d.Count > 0 ? InvoiceDecision.ManualReview : InvoiceDecision.AutoApprove;
        return new(decision, Math.Min(points, 100), d);
    }
}
