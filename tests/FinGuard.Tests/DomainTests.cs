using FinGuard.Domain;
using Xunit;

public class DomainTests
{
    static readonly Guid Sup = Guid.NewGuid();
    static Invoice Inv(decimal total, string no = "INV-1", decimal price = 100) =>
        new(no, Sup, new(2026, 1, 5), "EUR", new[] { new LineItem("Laptop", 1, price) }, total, "PO-1");
    static PurchaseOrder Po(decimal total = 100) =>
        new("PO-1", Sup, "EUR", new[] { new LineItem("Laptop", 1, 100) }, total);

    [Fact] public void CleanInvoice_AutoApproves() =>
        Assert.Equal(InvoiceDecision.AutoApprove, new InvoiceVerifier().Verify(Inv(100), Po(), []).Decision);

    [Fact] public void DuplicateNumber_Rejects() =>
        Assert.Equal(InvoiceDecision.Reject, new InvoiceVerifier().Verify(Inv(100), Po(), [Inv(100)]).Decision);

    [Fact] public void TotalMismatch_Rejects() =>
        Assert.Equal(InvoiceDecision.Reject, new InvoiceVerifier().Verify(Inv(150), Po(150), []).Decision);

    [Fact] public void FarOverPo_NeedsReview() =>
        Assert.Equal(InvoiceDecision.ManualReview, new InvoiceVerifier().Verify(Inv(120, price: 120), Po(), []).Decision);

    [Fact] public void RiskScore_IsBoundedAndExplained()
    {
        var r = SupplierRiskScorer.Assess(new(10, 10, 9, 20, 10, 50));
        Assert.InRange(r.Score, 0, 100); Assert.Equal("Critical", r.Level); Assert.NotEmpty(r.Factors);
    }

    [Fact] public void HealthySupplier_IsLow() =>
        Assert.Equal("Low", SupplierRiskScorer.Assess(new(50, 0, 0, 0, 800, 0.5m)).Level);
}
