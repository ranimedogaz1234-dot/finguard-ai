using System.Globalization;

namespace FinGuard.Domain;

/// <summary>
/// Deterministic, explainable rule engine for invoice verification.
/// Every rule that fires produces a <see cref="Discrepancy"/> with a stable code and a plain-language explanation.
/// See docs/DETECTION_RULES.md for the full rule table.
/// </summary>
public class InvoiceVerifier
{
    public const decimal Tolerance = 0.01m;        // rounding tolerance
    public const decimal OverPoThreshold = 0.05m;  // > 5% over PO is critical

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <param name="history">Other invoices from any supplier. The invoice being verified must not be included.</param>
    public VerificationResult Verify(Invoice inv, PurchaseOrder? po, IEnumerable<Invoice> history)
    {
        ArgumentNullException.ThrowIfNull(inv);
        var d = new List<Discrepancy>();
        var sameSupplier = (history ?? []).Where(h => h.SupplierId == inv.SupplierId).ToList();

        // 1. Duplicate detection. Exact number (after normalisation) is a hard duplicate;
        //    same amount + same date with a different number is only a *possible* duplicate.
        var normalized = NormalizeNumber(inv.Number);
        if (sameSupplier.Any(h => NormalizeNumber(h.Number) == normalized))
            d.Add(new("DUPLICATE", Severity.Critical,
                $"Invoice number '{inv.Number}' has already been submitted by this supplier."));
        else if (sameSupplier.Any(h => h.DeclaredTotal == inv.DeclaredTotal && h.Date == inv.Date))
            d.Add(new("POSSIBLE_DUPLICATE", Severity.Warning,
                $"Another invoice from this supplier has the same total ({Money(inv.DeclaredTotal)}) and date ({inv.Date:yyyy-MM-dd})."));

        // 2. Line validity and internal arithmetic.
        if (inv.Lines.Count == 0)
            d.Add(new("NO_LINES", Severity.Critical, "The invoice has no line items."));
        foreach (var l in inv.Lines.Where(l => l.Quantity <= 0 || l.UnitPrice < 0))
            d.Add(new("INVALID_LINE", Severity.Critical,
                $"Line '{l.Description}' has a non-positive quantity or a negative unit price."));

        var computed = inv.Lines.Sum(l => l.Total);
        if (Math.Abs(computed - inv.DeclaredTotal) > Tolerance)
            d.Add(new("TOTAL_MISMATCH", Severity.Critical,
                $"Declared total {Money(inv.DeclaredTotal)} differs from the line-item sum {Money(computed)}."));

        // 3. Purchase-order matching (3-way match without goods receipt).
        if (po is null)
        {
            d.Add(new("NO_PO", Severity.Warning, string.IsNullOrWhiteSpace(inv.PoNumber)
                ? "No purchase order was referenced."
                : $"Purchase order '{inv.PoNumber}' was not found."));
        }
        else
        {
            if (po.SupplierId != inv.SupplierId)
                d.Add(new("SUPPLIER_MISMATCH", Severity.Critical, "Invoice supplier does not match the purchase order supplier."));
            if (!string.Equals(po.Currency, inv.Currency, StringComparison.OrdinalIgnoreCase))
                d.Add(new("CURRENCY_MISMATCH", Severity.Critical, $"Invoice currency {inv.Currency} differs from PO currency {po.Currency}."));

            if (po.Total > 0 && inv.DeclaredTotal > po.Total * (1 + OverPoThreshold))
                d.Add(new("OVER_PO", Severity.Critical,
                    $"Invoice total {Money(inv.DeclaredTotal)} exceeds PO total {Money(po.Total)} by more than {SupplierRiskScorer.Pct(OverPoThreshold)}."));
            else if (inv.DeclaredTotal > po.Total + Tolerance)
                d.Add(new("SLIGHT_OVER_PO", Severity.Warning,
                    $"Invoice total {Money(inv.DeclaredTotal)} is slightly above the PO total {Money(po.Total)}."));

            foreach (var line in inv.Lines)
            {
                var p = po.Lines.FirstOrDefault(x => x.Description.Trim().Equals(line.Description.Trim(), StringComparison.OrdinalIgnoreCase));
                if (p is null)
                    d.Add(new("UNORDERED_ITEM", Severity.Warning, $"Line '{line.Description}' does not appear on the PO."));
                else
                {
                    if (line.UnitPrice > p.UnitPrice + Tolerance)
                        d.Add(new("PRICE_INCREASE", Severity.Warning,
                            $"'{line.Description}' unit price {Money(line.UnitPrice)} exceeds PO price {Money(p.UnitPrice)}."));
                    if (line.Quantity > p.Quantity)
                        d.Add(new("QUANTITY_EXCEEDS_PO", Severity.Warning,
                            $"'{line.Description}' quantity {line.Quantity.ToString("0.##", Inv)} exceeds ordered quantity {p.Quantity.ToString("0.##", Inv)}."));
                }
            }
        }

        var points = d.Sum(x => x.Severity switch { Severity.Critical => 40, Severity.Warning => 15, _ => 0 });
        var decision =
            d.Any(x => x.Code is "DUPLICATE" or "TOTAL_MISMATCH" or "NO_LINES" or "INVALID_LINE") ? InvoiceDecision.Reject
            : d.Count > 0 ? InvoiceDecision.ManualReview
            : InvoiceDecision.AutoApprove;
        return new(decision, Math.Min(points, 100), d);
    }

    /// <summary>
    /// Canonical form of an invoice number for duplicate detection.
    /// The number is split into letter and digit groups (at separators, whitespace and letter/digit boundaries),
    /// letters are upper-cased and leading zeros are removed from <em>every</em> digit group. Group boundaries are kept,
    /// so "AOF-2026-0458", "aof 2026 458" and "AOF2026-458" match, while "INV-12-3" and "INV-1-23" stay distinct.
    /// </summary>
    public static string NormalizeNumber(string number)
    {
        var groups = new List<string>();
        var current = new System.Text.StringBuilder();
        var currentIsDigit = false;
        void Flush()
        {
            if (current.Length == 0) return;
            var g = current.ToString();
            if (currentIsDigit) { g = g.TrimStart('0'); if (g.Length == 0) g = "0"; }
            groups.Add(g);
            current.Clear();
        }
        foreach (var ch in number ?? "")
        {
            if (char.IsAsciiDigit(ch))
            {
                if (current.Length > 0 && !currentIsDigit) Flush();
                currentIsDigit = true;
                current.Append(ch);
            }
            else if (char.IsLetter(ch))
            {
                if (current.Length > 0 && currentIsDigit) Flush();
                currentIsDigit = false;
                current.Append(char.ToUpperInvariant(ch));
            }
            else Flush(); // any separator, whitespace or punctuation ends a group
        }
        Flush();
        return string.Join("-", groups);
    }

    internal static string Money(decimal v) => v.ToString("N2", Inv);
}
