using FinGuard.Domain;
using Xunit;

namespace FinGuard.Tests;

public class SupplierRiskScorerTests
{
    private static SupplierStats Healthy => new(50, 0, 0, 0, 0, 0, 800, 0.5m);

    [Fact]
    public void HealthySupplier_IsLow_WithHighConfidence()
    {
        var r = SupplierRiskScorer.Assess(Healthy);
        Assert.Equal("Low", r.Level);
        Assert.Equal("High", r.Confidence);
        Assert.True(r.Score < 20);
    }

    [Fact]
    public void WorstCase_IsCritical_AndBoundedAt100()
    {
        var r = SupplierRiskScorer.Assess(new(10, 10, 9, 9, 3, 5, 10, 50));
        Assert.Equal(100, r.Score);
        Assert.Equal("Critical", r.Level);
    }

    [Fact]
    public void Score_EqualsSumOfFactorPoints_WhenUncapped()
    {
        var r = SupplierRiskScorer.Assess(new(20, 4, 1, 1, 0, 1, 400, 2));
        Assert.Equal(r.Factors.Sum(f => f.Points), r.Score);
        Assert.All(r.Factors, f => Assert.InRange(f.Points, 0, f.MaxPoints));
        // 20% rejection = 20, 1 dup = 8, 1 alert = 6, 1 bank change = 10, variance 2% = 3
        Assert.Equal(47, r.Score);
        Assert.Equal("High", r.Level);
    }

    [Fact]
    public void FactorDetails_FormatPercentagesWithoutSpace()
    {
        var r = SupplierRiskScorer.Assess(new(2, 1, 0, 0, 0, 0, 400, 0));
        Assert.Equal("1 of 2 invoices rejected (50%).", r.Factors.Single(f => f.Key == "rejection_rate").Detail);
    }

    [Fact]
    public void ConfirmedFraud_ForcesAtLeastHigh()
    {
        var r = SupplierRiskScorer.Assess(Healthy with { ConfirmedFraudCases = 1 });
        Assert.True(r.Score >= 45);
        Assert.Contains(r.Level, new[] { "High", "Critical" });
    }

    [Fact]
    public void PriceDecrease_IsNotPenalised()
    {
        var r = SupplierRiskScorer.Assess(Healthy with { AvgPriceVariancePct = -20 });
        Assert.Equal(0, r.Factors.Single(f => f.Key == "price_variance").Points);
    }

    [Fact]
    public void NewSupplier_WithFewInvoices_HasLowConfidence()
    {
        var r = SupplierRiskScorer.Assess(new(2, 0, 0, 0, 0, 0, 30, 0));
        Assert.Equal("Low", r.Confidence);
        Assert.Equal(10, r.Factors.Single(f => f.Key == "new_supplier").Points);
    }

    [Theory]
    [InlineData(0, "Low")]
    [InlineData(19, "Low")]
    [InlineData(20, "Medium")]
    [InlineData(45, "High")]
    [InlineData(70, "Critical")]
    public void LevelBoundaries(int score, string level) => Assert.Equal(level, SupplierRiskScorer.LevelFor(score));
}

public class FraudDetectorTests
{
    private static readonly Guid SupId = Guid.NewGuid();
    private static readonly string GoodIban = Iban.Create("FR", "30006000011234567890189");
    private static readonly string OtherIban = Iban.Create("LT", "1000011101001000");
    private static readonly DateOnly Today = new(2026, 6, 30);

    private static InvoiceSnapshot Snap(decimal amount, DateOnly? date = null, string? iban = null, string no = "N-1") =>
        new(Guid.NewGuid(), no, date ?? new(2026, 6, 15), amount, "EUR", iban ?? GoodIban);

    private static FraudContext Ctx(InvoiceSnapshot inv, IEnumerable<InvoiceSnapshot>? history = null,
        DateTime? ibanChanged = null, IEnumerable<PaymentSnapshot>? payments = null) =>
        new(inv, new(SupId, "Acme", GoodIban, ibanChanged), (history ?? []).ToList(), (payments ?? []).ToList(), 10_000m, Today);

    private static string[] Codes(IEnumerable<FraudSignal> s) => s.Select(x => x.Code).ToArray();

    [Fact]
    public void NormalInvoice_ProducesNoSignals() =>
        Assert.Empty(FraudDetector.Analyze(Ctx(Snap(1234.56m), [Snap(1100), Snap(1300), Snap(1200), Snap(1250)])));

    [Fact]
    public void DifferentIban_IsCriticalPaymentDetailsMismatch()
    {
        var s = FraudDetector.Analyze(Ctx(Snap(1234, iban: OtherIban))).Single(x => x.Code == "PAYMENT_DETAILS_MISMATCH");
        Assert.Equal(AlertSeverity.Critical, s.Severity);
        Assert.DoesNotContain(Iban.Normalize(OtherIban), s.Explanation); // IBANs are masked in explanations
    }

    [Fact]
    public void SameIban_WithDifferentFormatting_IsNotMismatch() =>
        Assert.DoesNotContain("PAYMENT_DETAILS_MISMATCH",
            Codes(FraudDetector.Analyze(Ctx(Snap(1234, iban: GoodIban.ToLowerInvariant().Insert(4, " "))))));

    [Fact]
    public void RecentBankChange_IsFlagged_ButOldChangeIsNot()
    {
        Assert.Contains("BANK_DETAILS_RECENTLY_CHANGED", Codes(FraudDetector.Analyze(Ctx(Snap(1234), ibanChanged: new DateTime(2026, 6, 1)))));
        Assert.DoesNotContain("BANK_DETAILS_RECENTLY_CHANGED", Codes(FraudDetector.Analyze(Ctx(Snap(1234), ibanChanged: new DateTime(2026, 1, 1)))));
    }

    [Fact]
    public void AmountOutlier_RequiresEnoughHistory()
    {
        var history = new[] { Snap(1000), Snap(1100), Snap(900), Snap(1000) };
        var s = FraudDetector.Analyze(Ctx(Snap(3500), history)).Single(x => x.Code == "AMOUNT_OUTLIER");
        Assert.Equal(AlertSeverity.High, s.Severity);
        Assert.DoesNotContain("AMOUNT_OUTLIER", Codes(FraudDetector.Analyze(Ctx(Snap(3500), history.Take(3)))));
    }

    [Fact]
    public void JustBelowThreshold_IsFlagged() =>
        Assert.Contains("JUST_BELOW_THRESHOLD", Codes(FraudDetector.Analyze(Ctx(Snap(9_800)))));

    [Fact]
    public void SplitInvoicing_IsDetected()
    {
        var s = FraudDetector.Analyze(Ctx(Snap(6_000, no: "B"), [Snap(5_500, new(2026, 6, 12), no: "A")]));
        Assert.Contains("SPLIT_INVOICING", Codes(s));
    }

    [Fact]
    public void FutureDated_AndRoundAmount_AreFlagged()
    {
        var s = Codes(FraudDetector.Analyze(Ctx(Snap(7_000, new(2026, 8, 1)))));
        Assert.Contains("FUTURE_DATED", s);
        Assert.Contains("ROUND_AMOUNT", s);
    }

    [Fact]
    public void PriorPaymentOfSameAmount_IsDuplicatePaymentRisk() =>
        Assert.Contains("DUPLICATE_PAYMENT_RISK", Codes(FraudDetector.Analyze(
            Ctx(Snap(4321.5m), payments: [new PaymentSnapshot(Guid.NewGuid(), 4321.5m, new(2026, 6, 1), GoodIban)]))));

    [Fact]
    public void Median_HandlesEvenAndOdd()
    {
        Assert.Equal(2m, FraudDetector.Median([3, 1, 2]));
        Assert.Equal(2.5m, FraudDetector.Median([4, 1, 2, 3]));
        Assert.Equal(0m, FraudDetector.Median([]));
    }
}

public class AlertWorkflowTests
{
    [Theory]
    [InlineData(AlertStatus.Open, AlertStatus.Investigating, false, true)]
    [InlineData(AlertStatus.Investigating, AlertStatus.ConfirmedFraud, false, false)]
    [InlineData(AlertStatus.Investigating, AlertStatus.ConfirmedFraud, true, true)]
    [InlineData(AlertStatus.Open, AlertStatus.ConfirmedFraud, true, false)]
    [InlineData(AlertStatus.Dismissed, AlertStatus.Investigating, false, false)]
    [InlineData(AlertStatus.Dismissed, AlertStatus.Investigating, true, true)]
    [InlineData(AlertStatus.Open, AlertStatus.Open, true, false)]
    public void Transitions_RespectRoles(AlertStatus from, AlertStatus to, bool admin, bool expected) =>
        Assert.Equal(expected, AlertWorkflow.CanTransition(from, to, admin));
}

public class IbanTests
{
    [Theory]
    [InlineData("GB82 WEST 1234 5698 7654 32", true)]
    [InlineData("GB83WEST12345698765432", false)]
    [InlineData("DE89370400440532013000", true)]
    [InlineData("not an iban", false)]
    public void Validates(string iban, bool valid) => Assert.Equal(valid, Iban.IsValid(iban));

    [Fact]
    public void Create_ProducesValidIban() => Assert.True(Iban.IsValid(Iban.Create("TN", "10006035183598478831")));

    [Fact]
    public void Mask_HidesMiddle() => Assert.Equal("GB82 •••• 5432", Iban.Mask("GB82WEST12345698765432"));
}
