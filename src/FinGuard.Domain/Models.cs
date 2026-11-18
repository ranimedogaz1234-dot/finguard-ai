namespace FinGuard.Domain;

/// <summary>Severity of a single invoice-verification discrepancy.</summary>
public enum Severity { Info, Warning, Critical }

/// <summary>The rule engine's recommendation. A human still makes the final approval decision.</summary>
public enum InvoiceDecision { AutoApprove, ManualReview, Reject }

public record LineItem(string Description, decimal Quantity, decimal UnitPrice)
{
    public decimal Total => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
}

public record Invoice(string Number, Guid SupplierId, DateOnly Date, string Currency,
    IReadOnlyList<LineItem> Lines, decimal DeclaredTotal, string? PoNumber);

public record PurchaseOrder(string Number, Guid SupplierId, string Currency,
    IReadOnlyList<LineItem> Lines, decimal Total);

public record Discrepancy(string Code, Severity Severity, string Explanation);

public record VerificationResult(InvoiceDecision Decision, int RiskPoints,
    IReadOnlyList<Discrepancy> Discrepancies)
{
    public string Summary => Discrepancies.Count == 0
        ? "No discrepancies found. Invoice matches its purchase order."
        : string.Join(" ", Discrepancies.Select(d => $"[{d.Severity}] {d.Explanation}"));
}
