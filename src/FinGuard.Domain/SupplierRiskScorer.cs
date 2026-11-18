using System.Globalization;

namespace FinGuard.Domain;

/// <summary>Inputs to the supplier risk model. All values come from stored platform data.</summary>
public record SupplierStats(
    int TotalInvoices,
    int RejectedInvoices,
    int DuplicateFlags,
    int OpenHighSeverityAlerts,
    int ConfirmedFraudCases,
    int BankDetailChanges90Days,
    int AccountAgeDays,
    decimal AvgPriceVariancePct);

public record RiskFactor(string Key, string Label, int Points, int MaxPoints, string Detail);

public record RiskAssessment(int Score, string Level, IReadOnlyList<RiskFactor> Factors,
    string Recommendation, string Confidence);

/// <summary>
/// Transparent, rule-based supplier risk model: score 0 (lowest risk) to 100 (highest risk).
/// The score is a sum of capped factor contributions, so every point is attributable.
/// This is an internal heuristic based on platform data — not a credit rating or a verified external assessment.
/// Weights are documented in docs/RISK_SCORING.md and must be kept in sync with this class.
/// </summary>
public static class SupplierRiskScorer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static RiskAssessment Assess(SupplierStats s)
    {
        var factors = new List<RiskFactor>();
        void Add(string key, string label, int pts, int max, string detail) =>
            factors.Add(new(key, label, Math.Clamp(pts, 0, max), max, detail));

        var rejectRate = s.TotalInvoices == 0 ? 0m : (decimal)s.RejectedInvoices / s.TotalInvoices;
        Add("rejection_rate", "Invoice rejection rate", (int)Math.Round(rejectRate * 100), 25,
            $"{s.RejectedInvoices} of {s.TotalInvoices} invoices rejected ({Pct(rejectRate)}).");
        Add("duplicates", "Duplicate invoice flags", s.DuplicateFlags * 8, 20,
            $"{s.DuplicateFlags} duplicate or possible-duplicate flag(s).");
        Add("open_alerts", "Open high-severity alerts", s.OpenHighSeverityAlerts * 6, 15,
            $"{s.OpenHighSeverityAlerts} open high/critical fraud alert(s).");
        Add("confirmed_fraud", "Confirmed fraud cases", s.ConfirmedFraudCases * 40, 40,
            $"{s.ConfirmedFraudCases} alert(s) confirmed as fraud by an administrator.");
        Add("bank_changes", "Recent bank-detail changes", s.BankDetailChanges90Days * 10, 15,
            $"{s.BankDetailChanges90Days} bank-detail change(s) in the last 90 days.");
        Add("new_supplier", "New supplier", s.AccountAgeDays < 90 ? 10 : 0, 10,
            $"Supplier relationship is {s.AccountAgeDays} day(s) old.");
        var variance = Math.Max(0, s.AvgPriceVariancePct);
        Add("price_variance", "Price variance above PO", (int)Math.Round(variance * 1.5m), 15,
            $"Average unit price {variance.ToString("0.0", Inv)}% above purchase-order prices.");

        var score = Math.Clamp(factors.Sum(f => f.Points), 0, 100);
        // Any confirmed fraud is at least High regardless of other factors.
        if (s.ConfirmedFraudCases > 0) score = Math.Max(score, 45);

        var level = LevelFor(score);
        var rec = level switch
        {
            "Critical" => "Suspend payments and require full manual vetting before any further payment.",
            "High" => "Require administrator approval for every invoice and verify bank details by phone.",
            "Medium" => "Monitor closely and spot-check invoices against purchase orders.",
            _ => "Standard processing."
        };
        var confidence = s.TotalInvoices >= 10 ? "High" : s.TotalInvoices >= 4 ? "Medium" : "Low";
        return new(score, level, factors, rec, confidence);
    }

    /// <summary>"0.5" -> "50%" (the invariant culture's "P0" format would render "50 %").</summary>
    internal static string Pct(decimal fraction) => (fraction * 100).ToString("0", Inv) + "%";

    public static string LevelFor(int score) =>
        score >= 70 ? "Critical" : score >= 45 ? "High" : score >= 20 ? "Medium" : "Low";
}
