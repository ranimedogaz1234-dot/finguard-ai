using System.Globalization;

namespace FinGuard.Domain;

public enum AlertSeverity { Low, Medium, High, Critical }

/// <summary>
/// Alert lifecycle. Every alert starts as *suspicious* (Open). Only an administrator can mark it
/// ConfirmedFraud; until then the platform never describes it as fraud.
/// </summary>
public enum AlertStatus { Open, Investigating, Escalated, Dismissed, Resolved, ConfirmedFraud }

public record InvoiceSnapshot(Guid Id, string Number, DateOnly Date, decimal Amount, string Currency, string? Iban);
public record PaymentSnapshot(Guid? InvoiceId, decimal Amount, DateOnly PaidOn, string? Iban);
public record SupplierProfile(Guid Id, string Name, string? Iban, DateTime? IbanChangedAtUtc);

public record FraudContext(
    InvoiceSnapshot Invoice,
    SupplierProfile Supplier,
    IReadOnlyList<InvoiceSnapshot> SupplierHistory,   // other invoices from the same supplier
    IReadOnlyList<PaymentSnapshot> SupplierPayments,
    decimal ApprovalThreshold,
    DateOnly Today);

public record FraudSignal(string Code, AlertSeverity Severity, string Title, string Explanation,
    IReadOnlyDictionary<string, string> Evidence);

/// <summary>
/// Rule-based anomaly detection. Each rule is independent, deterministic and explainable.
/// Signals indicate *suspicious patterns*, never confirmed fraud. See docs/DETECTION_RULES.md.
/// </summary>
public static class FraudDetector
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const int OutlierMinHistory = 4;

    public static IReadOnlyList<FraudSignal> Analyze(FraudContext c)
    {
        var s = new List<FraudSignal>();
        var inv = c.Invoice;
        var history = c.SupplierHistory.Where(h => h.Id != inv.Id).ToList();

        // R1 — payment details differ from supplier master data (classic invoice-redirection fraud).
        var invIban = Iban.Normalize(inv.Iban);
        var supIban = Iban.Normalize(c.Supplier.Iban);
        if (invIban.Length > 0 && supIban.Length > 0 && invIban != supIban)
            s.Add(new("PAYMENT_DETAILS_MISMATCH", AlertSeverity.Critical, "Payment details differ from supplier record",
                $"The invoice asks for payment to {Iban.Mask(invIban)}, but the verified account on file for {c.Supplier.Name} is {Iban.Mask(supIban)}.",
                Ev(("invoiceIban", Iban.Mask(invIban)), ("supplierIban", Iban.Mask(supIban)))));
        else if (invIban.Length > 0 && !Iban.IsValid(invIban))
            s.Add(new("INVALID_IBAN", AlertSeverity.Medium, "Invoice IBAN fails checksum",
                $"The IBAN on the invoice ({Iban.Mask(invIban)}) does not pass the ISO 13616 mod-97 check.",
                Ev(("invoiceIban", Iban.Mask(invIban)))));

        // R2 — bank details changed shortly before this invoice.
        if (c.Supplier.IbanChangedAtUtc is { } changed)
        {
            var days = inv.Date.DayNumber - DateOnly.FromDateTime(changed).DayNumber;
            if (days is >= 0 and <= 30)
                s.Add(new("BANK_DETAILS_RECENTLY_CHANGED", AlertSeverity.High, "Bank details changed recently",
                    $"{c.Supplier.Name}'s bank account was changed {days} day(s) before this invoice. Confirm the change through a known phone number before paying.",
                    Ev(("changedOn", changed.ToString("yyyy-MM-dd", Inv)), ("daysBeforeInvoice", days.ToString(Inv)))));
        }

        // R3 — amount is an outlier against the supplier's own history.
        var sameCurrency = history.Where(h => string.Equals(h.Currency, inv.Currency, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sameCurrency.Count >= OutlierMinHistory)
        {
            var median = Median(sameCurrency.Select(h => h.Amount));
            if (median > 0)
            {
                var ratio = inv.Amount / median;
                if (ratio >= 2)
                    s.Add(new("AMOUNT_OUTLIER", ratio >= 3 ? AlertSeverity.High : AlertSeverity.Medium, "Unusual invoice amount",
                        $"This invoice ({Money(inv.Amount)}) is {ratio.ToString("0.0", Inv)}× the supplier's median invoice ({Money(median)}) over {sameCurrency.Count} previous invoices.",
                        Ev(("amount", Money(inv.Amount)), ("median", Money(median)), ("ratio", ratio.ToString("0.00", Inv)), ("sample", sameCurrency.Count.ToString(Inv)))));
            }
        }

        // R4 — just below the approval threshold.
        if (c.ApprovalThreshold > 0 && inv.Amount < c.ApprovalThreshold && inv.Amount >= c.ApprovalThreshold * 0.95m)
            s.Add(new("JUST_BELOW_THRESHOLD", AlertSeverity.Medium, "Amount just below approval threshold",
                $"{Money(inv.Amount)} is within 5% below the {Money(c.ApprovalThreshold)} administrator-approval threshold.",
                Ev(("amount", Money(inv.Amount)), ("threshold", Money(c.ApprovalThreshold)))));

        // R5 — split invoicing: several sub-threshold invoices close together that add up above it.
        if (c.ApprovalThreshold > 0 && inv.Amount < c.ApprovalThreshold)
        {
            var window = history.Where(h => Math.Abs(h.Date.DayNumber - inv.Date.DayNumber) <= 7
                                            && h.Amount < c.ApprovalThreshold
                                            && string.Equals(h.Currency, inv.Currency, StringComparison.OrdinalIgnoreCase)).ToList();
            var combined = window.Sum(h => h.Amount) + inv.Amount;
            if (window.Count >= 1 && combined >= c.ApprovalThreshold)
                s.Add(new("SPLIT_INVOICING", AlertSeverity.High, "Possible split invoicing",
                    $"{window.Count + 1} invoices from this supplier within 7 days are each below the {Money(c.ApprovalThreshold)} threshold but total {Money(combined)}.",
                    Ev(("invoices", string.Join(", ", window.Select(w => w.Number).Append(inv.Number))), ("combined", Money(combined)))));
        }

        // R6 — round amount.
        if (inv.Amount >= 5000 && inv.Amount % 1000 == 0)
            s.Add(new("ROUND_AMOUNT", AlertSeverity.Low, "Round-number amount",
                $"{Money(inv.Amount)} is an exact multiple of 1,000. Round amounts are more common in fabricated invoices; low weight on its own.",
                Ev(("amount", Money(inv.Amount)))));

        // R7 — future dated.
        if (inv.Date > c.Today.AddDays(1))
            s.Add(new("FUTURE_DATED", AlertSeverity.Medium, "Invoice dated in the future",
                $"Invoice date {inv.Date:yyyy-MM-dd} is after today ({c.Today:yyyy-MM-dd}).",
                Ev(("invoiceDate", inv.Date.ToString("yyyy-MM-dd", Inv)))));

        // R8 — a payment of the same amount was already made recently for a different invoice.
        var prior = c.SupplierPayments.FirstOrDefault(p => p.InvoiceId != inv.Id && p.Amount == inv.Amount
                                                         && Math.Abs(p.PaidOn.DayNumber - inv.Date.DayNumber) <= 60);
        if (prior is not null)
            s.Add(new("DUPLICATE_PAYMENT_RISK", AlertSeverity.High, "Same amount already paid recently",
                $"A payment of {Money(prior.Amount)} to this supplier was made on {prior.PaidOn:yyyy-MM-dd}. Paying this invoice could duplicate that payment.",
                Ev(("paidOn", prior.PaidOn.ToString("yyyy-MM-dd", Inv)), ("amount", Money(prior.Amount)))));

        return s;
    }

    /// <summary>Maps a verification discrepancy to an alert, for the duplicate rules that warrant investigation.</summary>
    public static FraudSignal? FromDiscrepancy(Discrepancy d, InvoiceSnapshot inv) => d.Code switch
    {
        "DUPLICATE" => new("DUPLICATE_INVOICE", AlertSeverity.Critical, "Duplicate invoice number", d.Explanation, Ev(("invoice", inv.Number))),
        "POSSIBLE_DUPLICATE" => new("POSSIBLE_DUPLICATE", AlertSeverity.Medium, "Possible duplicate invoice", d.Explanation, Ev(("invoice", inv.Number))),
        "OVER_PO" => new("OVER_PO", AlertSeverity.High, "Invoice exceeds purchase order", d.Explanation, Ev(("invoice", inv.Number))),
        _ => null
    };

    public static decimal Median(IEnumerable<decimal> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return 0;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    private static string Money(decimal v) => v.ToString("N2", Inv);
    private static IReadOnlyDictionary<string, string> Ev(params (string k, string v)[] items) => items.ToDictionary(i => i.k, i => i.v);
}

public static class AlertWorkflow
{
    private static readonly Dictionary<AlertStatus, AlertStatus[]> AnalystTransitions = new()
    {
        [AlertStatus.Open] = [AlertStatus.Investigating, AlertStatus.Dismissed],
        [AlertStatus.Investigating] = [AlertStatus.Escalated, AlertStatus.Dismissed, AlertStatus.Resolved],
        [AlertStatus.Escalated] = [AlertStatus.Investigating],
    };

    public static bool IsTerminal(AlertStatus s) => s is AlertStatus.Dismissed or AlertStatus.Resolved or AlertStatus.ConfirmedFraud;

    /// <summary>Analysts may move alerts through the investigation; only admins may confirm fraud or reopen closed alerts.</summary>
    public static bool CanTransition(AlertStatus from, AlertStatus to, bool isAdmin)
    {
        if (from == to) return false;
        if (AnalystTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to)) return true;
        if (!isAdmin) return false;
        if (to == AlertStatus.ConfirmedFraud) return from is AlertStatus.Investigating or AlertStatus.Escalated;
        if (IsTerminal(from) && to == AlertStatus.Investigating) return true; // reopen
        return from == AlertStatus.Escalated && to is AlertStatus.Dismissed or AlertStatus.Resolved;
    }
}

public static class Iban
{
    public static string Normalize(string? iban) =>
        iban is null ? "" : new string(iban.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>ISO 13616 mod-97 check.</summary>
    public static bool IsValid(string? iban)
    {
        var s = Normalize(iban);
        if (s.Length is < 15 or > 34 || !char.IsLetter(s[0]) || !char.IsLetter(s[1])) return false;
        var rearranged = s[4..] + s[..4];
        var remainder = 0;
        foreach (var ch in rearranged)
        {
            var val = char.IsDigit(ch) ? ch - '0' : char.IsLetter(ch) ? ch - 'A' + 10 : -1;
            if (val < 0) return false;
            remainder = val >= 10 ? (remainder * 100 + val) % 97 : (remainder * 10 + val) % 97;
        }
        return remainder == 1;
    }

    /// <summary>Builds a checksum-valid IBAN from a country code and BBAN (used for fictional demo data).</summary>
    public static string Create(string country, string bban)
    {
        var cc = country.ToUpperInvariant();
        var b = Normalize(bban);
        var rearranged = b + cc + "00";
        var remainder = 0;
        foreach (var ch in rearranged)
        {
            var val = char.IsDigit(ch) ? ch - '0' : ch - 'A' + 10;
            remainder = val >= 10 ? (remainder * 100 + val) % 97 : (remainder * 10 + val) % 97;
        }
        var check = 98 - remainder;
        return $"{cc}{check:00}{b}";
    }

    public static string Mask(string? iban)
    {
        var s = Normalize(iban);
        return s.Length <= 8 ? s : $"{s[..4]} •••• {s[^4..]}";
    }
}
