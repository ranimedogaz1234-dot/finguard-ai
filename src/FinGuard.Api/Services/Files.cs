using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FinGuard.Api.Contracts;
using FinGuard.Api.Data;
using FinGuard.Api.Infrastructure;
using FinGuard.Domain;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;

namespace FinGuard.Api.Services;

public class StorageOptions
{
    public string UploadPath { get; set; } = "App_Data/uploads";
    public long MaxPdfBytes { get; set; } = 5 * 1024 * 1024;
}

public interface IFileStorage
{
    Task<string> SavePdfAsync(IFormFile file, CancellationToken ct);
    Stream Open(string storedName);
    void Delete(string storedName);
}

/// <summary>
/// Stores uploads outside the web root under random names. Validates size, extension, declared type and the
/// actual file signature, so a renamed executable or HTML file is rejected.
/// </summary>
public sealed class FileStorage : IFileStorage
{
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private readonly string _root;
    private readonly long _max;

    public FileStorage(Microsoft.Extensions.Options.IOptions<StorageOptions> o, IWebHostEnvironment env)
    {
        _root = Path.GetFullPath(Path.Combine(env.ContentRootPath, o.Value.UploadPath));
        _max = o.Value.MaxPdfBytes;
        Directory.CreateDirectory(_root);
    }

    public static void ValidatePdf(IFormFile file, long maxBytes)
    {
        if (file.Length == 0) throw new BusinessRuleException("The uploaded file is empty.", "invalid_file");
        if (file.Length > maxBytes) throw new BusinessRuleException($"The file exceeds the {maxBytes / 1024 / 1024} MB limit.", "file_too_large");
        if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException("Only .pdf files are accepted.", "invalid_file_type");
        if (!string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException("The file must be sent as application/pdf.", "invalid_file_type");
        Span<byte> head = stackalloc byte[5];
        using var s = file.OpenReadStream();
        if (s.ReadAtLeast(head, 5, throwOnEndOfStream: false) < 5 || !head.SequenceEqual(PdfMagic))
            throw new BusinessRuleException("The file content is not a PDF document.", "invalid_file_type");
    }

    public async Task<string> SavePdfAsync(IFormFile file, CancellationToken ct)
    {
        ValidatePdf(file, _max);
        var name = $"{Guid.NewGuid():N}.pdf";
        await using var fs = new FileStream(Resolve(name), FileMode.CreateNew, FileAccess.Write);
        await file.CopyToAsync(fs, ct);
        return name;
    }

    public Stream Open(string storedName) => new FileStream(Resolve(storedName), FileMode.Open, FileAccess.Read, FileShare.Read);

    public void Delete(string storedName)
    {
        var p = Resolve(storedName);
        if (File.Exists(p)) File.Delete(p);
    }

    /// <summary>Stored names are server-generated GUIDs; anything else is refused (path traversal guard).</summary>
    private string Resolve(string storedName)
    {
        if (!Regex.IsMatch(storedName, "^[a-f0-9]{32}\\.pdf$")) throw new BusinessRuleException("Invalid file reference.");
        var full = Path.GetFullPath(Path.Combine(_root, storedName));
        if (!full.StartsWith(_root, StringComparison.Ordinal)) throw new BusinessRuleException("Invalid file reference.");
        return full;
    }

    public static string SafeDisplayName(string name)
    {
        var n = Path.GetFileName(name);
        n = Regex.Replace(n, "[^A-Za-z0-9._ -]", "_");
        return n.Length > 120 ? n[..120] : n;
    }
}

/// <summary>
/// Best-effort extraction from text-based PDFs using PdfPig plus pattern matching. Scanned images (no text layer)
/// are not OCR'd; the result says so. Extracted values are only suggestions that the user confirms.
/// </summary>
public record SupplierRef(Guid Id, string Name, string? Iban, string? TaxId);

public class PdfExtractionService(AppDbContext db, Microsoft.Extensions.Options.IOptions<StorageOptions> o)
{
    public async Task<ExtractionResult> ExtractAsync(IFormFile file, CancellationToken ct)
    {
        FileStorage.ValidatePdf(file, o.Value.MaxPdfBytes);
        string text;
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        try
        {
            // Untrusted input: parse on a worker with a time budget and a page cap so a pathological PDF
            // cannot tie up the request indefinitely.
            text = await Task.Run(() => ReadText(bytes), ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (TimeoutException)
        {
            throw new BusinessRuleException("The PDF took too long to read. Enter the details manually.", "pdf_timeout");
        }
        catch (Exception ex) when (ex is not BusinessRuleException and not OperationCanceledException)
        {
            throw new BusinessRuleException("The PDF could not be read. It may be encrypted or damaged.", "unreadable_pdf");
        }

        var suppliers = await db.Suppliers.AsNoTracking().Where(s => s.Status == SupplierStatus.Active)
            .Select(s => new { s.Id, s.Name, s.Iban, s.TaxId }).ToListAsync(ct);
        return Parse(text, suppliers.Select(s => new SupplierRef(s.Id, s.Name, s.Iban, s.TaxId)).ToList());
    }

    private static string ReadText(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages().Take(10))
        {
            // Rebuild visual lines from word positions; raw page text can run separate lines together.
            foreach (var line in page.GetWords().GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3)).OrderByDescending(g => g.Key))
                sb.AppendLine(string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            if (sb.Length > 200_000) break; // enough for any invoice; caps memory on hostile files
        }
        return sb.ToString();
    }

    public static ExtractionResult Parse(string text, IReadOnlyList<SupplierRef> suppliers)
    {
        var notes = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return new(false, null, null, null, null, null, null, null, null, null,
                ["No text layer found. This looks like a scanned image; OCR is not enabled, so please enter the details manually."]);

        string? Find(string pattern) { var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline); return m.Success ? m.Groups[1].Value.Trim() : null; }

        var number = Find(@"invoice\s*(?:no\.?|number|#|n[°o])\s*[:#]?\s*([A-Z0-9][A-Z0-9\-/]{2,30})");
        var po = Find(@"\b(?:PO|P\.O\.|purchase\s*order)(?![a-z])\s*(?:no\.?|number|#)?\s*[:#]?\s*([A-Z0-9][A-Z0-9\-/]{2,30})");
        var date = ParseDate(Find(@"(?:invoice\s*date|date\s*of\s*issue|issue\s*date|(?<![a-z]\s?)date)\s*[:]?\s*([0-9]{1,4}[./\-][0-9]{1,2}[./\-][0-9]{1,4})"));
        var due = ParseDate(Find(@"(?:due\s*date|payment\s*due)\s*[:]?\s*([0-9]{1,4}[./\-][0-9]{1,2}[./\-][0-9]{1,4})"));
        var totalRaw = Find(@"(?<![a-z])(?:total\s*(?:due|amount|ttc)?|amount\s*due|grand\s*total)\s*[:]?\s*(?:EUR|USD|GBP|TND|€|\$|£)?\s*([0-9][0-9 .,']*[0-9])");
        var total = ParseAmount(totalRaw);
        var currency = Regex.Match(text, @"\b(EUR|USD|GBP|TND|CHF)\b").Success ? Regex.Match(text, @"\b(EUR|USD|GBP|TND|CHF)\b").Value
            : text.Contains('€') ? "EUR" : text.Contains('£') ? "GBP" : text.Contains('$') ? "USD" : null;
        string? iban = null;
        foreach (Match m in Regex.Matches(text, @"\b([A-Z]{2}[0-9]{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,4})?)\b"))
            if (Iban.IsValid(m.Groups[1].Value)) { iban = Iban.Normalize(m.Groups[1].Value); break; }

        // Supplier matching: exact IBAN first (strongest), then tax id, then name appearing in the text.
        SupplierRef? match = null;
        if (iban is not null) match = suppliers.FirstOrDefault(s => Iban.Normalize(s.Iban) == iban);
        match ??= suppliers.FirstOrDefault(s => s.TaxId is { Length: > 3 } t && text.Contains(t, StringComparison.OrdinalIgnoreCase));
        match ??= suppliers.FirstOrDefault(s => text.Contains(s.Name, StringComparison.OrdinalIgnoreCase));

        if (number is null) notes.Add("Invoice number not found.");
        if (date is null) notes.Add("Invoice date not found.");
        if (total is null) notes.Add("Total amount not found.");
        if (match is null) notes.Add("No existing supplier matched; select the supplier manually.");
        else notes.Add(iban is not null && Iban.Normalize(match.Iban) == iban ? $"Supplier matched by IBAN: {match.Name}." : $"Supplier matched by name/tax id: {match.Name}. Verify bank details.");
        notes.Add("Extracted values are suggestions. Review every field before submitting.");

        return new(true, number, date, due, total, currency, iban, po, match?.Id, match?.Name, notes);
    }

    private static DateOnly? ParseDate(string? s)
    {
        if (s is null) return null;
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy", "dd-MM-yyyy", "yyyy/MM/dd"];
        return DateOnly.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    public static decimal? ParseAmount(string? raw)
    {
        if (raw is null) return null;
        var s = raw.Replace(" ", "").Replace("'", "");
        var lastComma = s.LastIndexOf(','); var lastDot = s.LastIndexOf('.');
        if (lastComma > lastDot) s = s.Replace(".", "").Replace(',', '.');   // 1.234,56
        else s = s.Replace(",", "");                                         // 1,234.56
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

/// <summary>Self-contained, printable HTML verification report. All values are HTML-encoded.</summary>
public static class ReportBuilder
{
    public static string Build(InvoiceDetail i, DateTime generatedUtc, string generatedBy)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        string M(decimal v) => $"{v.ToString("N2", CultureInfo.InvariantCulture)} {E(i.Currency)}";
        var sb = new StringBuilder();
        sb.Append($$"""
<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>Verification report {{E(i.Number)}}</title>
<style>
body{font-family:Segoe UI,Arial,sans-serif;color:#0f172a;margin:40px;max-width:900px}
h1{font-size:22px;margin:0}h2{font-size:15px;margin-top:28px;border-bottom:1px solid #cbd5e1;padding-bottom:4px}
.muted{color:#64748b;font-size:12px}.badge{display:inline-block;padding:3px 10px;border-radius:12px;font-size:12px;font-weight:600}
.AutoApprove{background:#dcfce7;color:#166534}.ManualReview{background:#fef3c7;color:#92400e}.Reject{background:#fee2e2;color:#991b1b}
table{border-collapse:collapse;width:100%;font-size:13px}td,th{border-bottom:1px solid #e2e8f0;padding:6px;text-align:left}
.Critical{color:#b91c1c;font-weight:600}.Warning{color:#b45309;font-weight:600}.demo{background:#ede9fe;color:#5b21b6}
.grid{display:grid;grid-template-columns:1fr 1fr;gap:4px 24px;font-size:13px}
</style></head><body>
<p class="muted">FinGuard AI — Invoice verification report</p>
<h1>Invoice {{E(i.Number)}} <span class="badge {{E(i.Decision)}}">{{E(i.Decision)}}</span>{{(i.IsDemo ? " <span class=\"badge demo\">DEMO DATA</span>" : "")}}</h1>
<p class="muted">Generated {{generatedUtc:yyyy-MM-dd HH:mm}} UTC by {{E(generatedBy)}}</p>
<h2>Summary</h2><p>{{E(i.Summary)}}</p>
<div class="grid">
<div>Supplier: <b>{{E(i.SupplierName)}}</b> (risk {{E(i.SupplierRiskLevel)}})</div><div>Status: <b>{{E(i.Status)}}</b></div>
<div>Invoice date: {{i.InvoiceDate:yyyy-MM-dd}}</div><div>Due date: {{(i.DueDate?.ToString("yyyy-MM-dd") ?? "—")}}</div>
<div>Declared total: <b>{{M(i.Total)}}</b></div><div>Line-item total: {{M(i.LinesTotal)}}</div>
<div>Purchase order: {{E(i.PoNumber ?? "none")}}</div><div>Payment account: {{E(i.IbanMasked ?? "not provided")}}</div>
<div>Verification points: {{i.VerificationPoints}}/100</div><div>Verified: {{i.VerifiedAtUtc:yyyy-MM-dd HH:mm}} UTC</div>
</div>
<h2>Discrepancies ({{i.Discrepancies.Count}})</h2>
""");
        if (i.Discrepancies.Count == 0) sb.Append("<p>None.</p>");
        else
        {
            sb.Append("<table><tr><th>Severity</th><th>Rule</th><th>Explanation</th></tr>");
            foreach (var d in i.Discrepancies)
                sb.Append($"<tr><td class=\"{E(d.Severity)}\">{E(d.Severity)}</td><td>{E(d.Code)}</td><td>{E(d.Explanation)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("<h2>Line items</h2><table><tr><th>Description</th><th>Qty</th><th>Unit price</th><th>Total</th></tr>");
        foreach (var l in i.Lines)
            sb.Append($"<tr><td>{E(l.Description)}</td><td>{l.Quantity.ToString("0.##", CultureInfo.InvariantCulture)}</td><td>{M(l.UnitPrice)}</td><td>{M(Math.Round(l.Quantity * l.UnitPrice, 2))}</td></tr>");
        sb.Append("</table>");
        sb.Append($"<h2>Fraud alerts ({i.Alerts.Count})</h2>");
        if (i.Alerts.Count == 0) sb.Append("<p>None.</p>");
        else
        {
            sb.Append("<table><tr><th>#</th><th>Severity</th><th>Status</th><th>Title</th></tr>");
            foreach (var a in i.Alerts)
                sb.Append($"<tr><td>{a.Number}</td><td>{E(a.Severity)}</td><td>{E(a.Status)}</td><td>{E(a.Title)}</td></tr>");
            sb.Append("</table><p class=\"muted\">Alerts indicate suspicious patterns detected by rules. They are not confirmed fraud unless their status is ConfirmedFraud.</p>");
        }
        sb.Append("<h2>Review</h2>");
        sb.Append(i.ReviewedBy is null ? "<p>Not yet reviewed.</p>"
            : $"<p>{E(i.Status)} by {E(i.ReviewedBy)} on {i.ReviewedAtUtc:yyyy-MM-dd HH:mm} UTC. {E(i.ReviewNote)}</p>");
        sb.Append("<p class=\"muted\">Rule-based decision support. Final approval decisions are made by authorised staff.</p></body></html>");
        return sb.ToString();
    }
}
