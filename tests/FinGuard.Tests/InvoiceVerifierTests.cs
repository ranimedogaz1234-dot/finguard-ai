using FinGuard.Domain;
using Xunit;

namespace FinGuard.Tests;

public class InvoiceVerifierTests
{
    private static readonly Guid Sup = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly InvoiceVerifier _v = new();

    private static Invoice Inv(decimal total, string no = "INV-1", decimal price = 100, decimal qty = 1,
        Guid? supplier = null, string currency = "EUR", DateOnly? date = null, string desc = "Laptop") =>
        new(no, supplier ?? Sup, date ?? new(2026, 1, 5), currency, [new LineItem(desc, qty, price)], total, "PO-1");

    private static PurchaseOrder Po(decimal total = 100, Guid? supplier = null, string currency = "EUR", decimal qty = 1) =>
        new("PO-1", supplier ?? Sup, currency, [new LineItem("Laptop", qty, 100)], total);

    private static string[] Codes(VerificationResult r) => r.Discrepancies.Select(d => d.Code).ToArray();

    [Fact]
    public void CleanInvoice_AutoApproves_WithNoDiscrepancies()
    {
        var r = _v.Verify(Inv(100), Po(), []);
        Assert.Equal(InvoiceDecision.AutoApprove, r.Decision);
        Assert.Empty(r.Discrepancies);
        Assert.Equal(0, r.RiskPoints);
    }

    [Theory]
    [InlineData("INV-1")]
    [InlineData("inv 1")]
    [InlineData("INV-0001")]
    public void DuplicateNumber_FromSameSupplier_Rejects(string historicNumber)
    {
        var r = _v.Verify(Inv(100), Po(), [Inv(100, no: historicNumber, date: new(2025, 12, 1))]);
        Assert.Equal(InvoiceDecision.Reject, r.Decision);
        Assert.Contains("DUPLICATE", Codes(r));
    }

    [Fact]
    public void SameNumber_FromDifferentSupplier_IsNotDuplicate()
    {
        var r = _v.Verify(Inv(100), Po(), [Inv(100, supplier: Other)]);
        Assert.DoesNotContain("DUPLICATE", Codes(r));
        Assert.Equal(InvoiceDecision.AutoApprove, r.Decision);
    }

    [Fact]
    public void SameAmountAndDate_DifferentNumber_IsOnlyPossibleDuplicate()
    {
        var r = _v.Verify(Inv(100), Po(), [Inv(100, no: "INV-2")]);
        Assert.Contains("POSSIBLE_DUPLICATE", Codes(r));
        Assert.DoesNotContain("DUPLICATE", Codes(r));
        Assert.Equal(InvoiceDecision.ManualReview, r.Decision);
    }

    [Fact]
    public void TotalMismatch_Rejects()
    {
        var r = _v.Verify(Inv(150), Po(150), []);
        Assert.Equal(InvoiceDecision.Reject, r.Decision);
        Assert.Contains("TOTAL_MISMATCH", Codes(r));
    }

    [Fact]
    public void TotalWithinRoundingTolerance_IsAccepted()
    {
        var r = _v.Verify(Inv(100.01m), Po(100.01m), []);
        Assert.DoesNotContain("TOTAL_MISMATCH", Codes(r));
    }

    [Fact]
    public void FarOverPo_IsCriticalAndNeedsReview()
    {
        var r = _v.Verify(Inv(120, price: 120), Po(), []);
        Assert.Equal(InvoiceDecision.ManualReview, r.Decision);
        Assert.Contains("OVER_PO", Codes(r));
        Assert.Contains("PRICE_INCREASE", Codes(r));
        Assert.EndsWith("by more than 5%.", r.Discrepancies.Single(d => d.Code == "OVER_PO").Explanation);
    }

    [Fact]
    public void SlightlyOverPo_IsWarning()
    {
        var r = _v.Verify(Inv(103, price: 103), Po(), []);
        Assert.Contains("SLIGHT_OVER_PO", Codes(r));
        Assert.DoesNotContain("OVER_PO", Codes(r));
    }

    [Fact]
    public void MissingPo_NeedsReview()
    {
        var r = _v.Verify(Inv(100), null, []);
        Assert.Equal(InvoiceDecision.ManualReview, r.Decision);
        Assert.Contains("NO_PO", Codes(r));
    }

    [Fact]
    public void SupplierAndCurrencyMismatch_AreCritical()
    {
        var r = _v.Verify(Inv(100), Po(supplier: Other, currency: "USD"), []);
        Assert.Contains("SUPPLIER_MISMATCH", Codes(r));
        Assert.Contains("CURRENCY_MISMATCH", Codes(r));
        Assert.All(r.Discrepancies.Where(d => d.Code.EndsWith("MISMATCH")), d => Assert.Equal(Severity.Critical, d.Severity));
    }

    [Fact]
    public void UnorderedItem_AndExcessQuantity_AreFlagged()
    {
        var r1 = _v.Verify(Inv(100, desc: "Monitor"), Po(), []);
        Assert.Contains("UNORDERED_ITEM", Codes(r1));

        var r2 = _v.Verify(Inv(200, qty: 2), Po(total: 200, qty: 1), []);
        Assert.Contains("QUANTITY_EXCEEDS_PO", Codes(r2));
    }

    [Fact]
    public void InvalidLine_Rejects()
    {
        var r = _v.Verify(Inv(0, qty: 0), Po(), []);
        Assert.Equal(InvoiceDecision.Reject, r.Decision);
        Assert.Contains("INVALID_LINE", Codes(r));
    }

    [Fact]
    public void Explanations_UseInvariantNumberFormat()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            var r = _v.Verify(Inv(1500), Po(1500), []);
            Assert.Contains("1,500.00", r.Discrepancies.Single(d => d.Code == "TOTAL_MISMATCH").Explanation);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    public void RiskPoints_AreCappedAt100()
    {
        var r = _v.Verify(Inv(999, desc: "X", currency: "USD"), Po(supplier: Other), [Inv(999, desc: "X")]);
        Assert.Equal(100, r.RiskPoints);
    }

    [Theory]
    [InlineData("INV-0042", "INV42")]
    [InlineData("inv 42", "INV42")]
    [InlineData("A-000", "A0")]
    [InlineData("ABC", "ABC")]
    public void NormalizeNumber_Works(string input, string expected) =>
        Assert.Equal(expected, InvoiceVerifier.NormalizeNumber(input));
}
