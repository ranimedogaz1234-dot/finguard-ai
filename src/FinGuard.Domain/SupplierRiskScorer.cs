namespace FinGuard.Domain;

public record SupplierStats(int TotalInvoices, int RejectedInvoices, int DuplicateFlags,
    int LatePayments, int AccountAgeDays, decimal AvgPriceVariancePct);

public record RiskAssessment(int Score, string Level, IReadOnlyList<string> Factors, string Recommendation);

/// <summary>Score 0 (safe) to 100 (high risk), with the contribution of each factor explained.</summary>
public static class SupplierRiskScorer
{
    public static RiskAssessment Assess(SupplierStats s)
    {
        var factors = new List<string>(); int score = 0;
        void Add(int pts, string why) { if (pts > 0) { score += pts; factors.Add($"+{pts}: {why}"); } }

        var rejectRate = s.TotalInvoices == 0 ? 0 : (double)s.RejectedInvoices / s.TotalInvoices;
        Add((int)Math.Min(35, rejectRate * 100), $"rejection rate {rejectRate:P0}");
        Add(Math.Min(25, s.DuplicateFlags * 8), $"{s.DuplicateFlags} duplicate invoice flag(s)");
        Add(Math.Min(15, s.LatePayments * 3), $"{s.LatePayments} late payment event(s)");
        Add(s.AccountAgeDays < 90 ? 10 : 0, "new supplier (< 90 days)");
        Add((int)Math.Min(15, Math.Abs(s.AvgPriceVariancePct) * 1.5m), $"average price variance {s.AvgPriceVariancePct:F1}%");

        score = Math.Clamp(score, 0, 100);
        var level = score >= 70 ? "Critical" : score >= 45 ? "High" : score >= 20 ? "Medium" : "Low";
        var rec = level switch
        {
            "Critical" => "Suspend payments and require manual vetting.",
            "High" => "Require manager approval for every invoice.",
            "Medium" => "Monitor closely; spot-check invoices.",
            _ => "Standard processing."
        };
        return new(score, level, factors, rec);
    }
}
