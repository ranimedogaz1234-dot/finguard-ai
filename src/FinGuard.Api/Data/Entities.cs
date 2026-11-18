using FinGuard.Domain;

namespace FinGuard.Api.Data;

/// <summary>Money is persisted as integer minor units (cents) so sums and ordering are exact in every provider.</summary>
public static class Cents
{
    public static long From(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
    public static decimal To(long cents) => cents / 100m;
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Analyst = "Analyst";
    public static readonly string[] All = [Admin, Analyst];
}

public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Analyst;
    public bool IsActive { get; set; } = true;
    /// <summary>Rotated on password change / role change; invalidates every existing session cookie.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAtUtc { get; set; }
    public bool IsDemo { get; set; }
}

public class PasswordResetToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    /// <summary>SHA-256 of the token. The raw token is only ever sent to the user.</summary>
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? UsedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Development-only outbox standing in for an email provider. Disabled outside Development.</summary>
public class OutboxEmail
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public enum SupplierStatus { Active, Archived }

public class Supplier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Country { get; set; } = "";
    public string Category { get; set; } = "";
    public string? TaxId { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Address { get; set; }
    public string? Iban { get; set; }
    public DateTime? IbanChangedAtUtc { get; set; }
    public string Currency { get; set; } = "EUR";
    public long AnnualSpendCents { get; set; }
    public int PaymentTermsDays { get; set; } = 30;
    public string? Notes { get; set; }
    public SupplierStatus Status { get; set; } = SupplierStatus.Active;
    public DateOnly OnboardedOn { get; set; }
    public int RiskScore { get; set; }
    public string RiskLevel { get; set; } = "Low";
    public DateTime? RiskAssessedAtUtc { get; set; }
    public bool IsDemo { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<RiskAssessmentRecord> RiskAssessments { get; set; } = [];
    public List<BankDetailChange> BankDetailChanges { get; set; } = [];
}

public class BankDetailChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SupplierId { get; set; }
    public string? OldIbanMasked { get; set; }
    public string? NewIbanMasked { get; set; }
    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? ChangedById { get; set; }
}

public class RiskAssessmentRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SupplierId { get; set; }
    public int Score { get; set; }
    public string Level { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public string FactorsJson { get; set; } = "[]";
    public string Trigger { get; set; } = "";
    public DateTime AssessedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PurchaseOrderEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public string Currency { get; set; } = "EUR";
    public long TotalCents { get; set; }
    public DateOnly IssuedOn { get; set; }
    public bool IsDemo { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];
}

public class PurchaseOrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseOrderId { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public long UnitPriceCents { get; set; }
}

public enum InvoiceStatus { Pending, Approved, Rejected, Paid }

public class InvoiceEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public string NormalizedNumber { get; set; } = "";
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public string? PoNumber { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string Currency { get; set; } = "EUR";
    public long TotalCents { get; set; }
    public string? Iban { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Pending;

    // Verification result (system recommendation)
    public InvoiceDecision Decision { get; set; }
    public int VerificationPoints { get; set; }
    public string DiscrepanciesJson { get; set; } = "[]";
    public DateTime? VerifiedAtUtc { get; set; }

    // Human review
    public Guid? CreatedById { get; set; }
    public Guid? ReviewedById { get; set; }
    public string? ReviewNote { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }

    public string? PdfStoredName { get; set; }
    public string? PdfOriginalName { get; set; }
    public long? PdfSizeBytes { get; set; }

    public bool IsDemo { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<InvoiceLine> Lines { get; set; } = [];
}

public class InvoiceLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public long UnitPriceCents { get; set; }
}

public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SupplierId { get; set; }
    public Guid? InvoiceId { get; set; }
    public long AmountCents { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? Iban { get; set; }
    public DateOnly PaidOn { get; set; }
    public string Reference { get; set; } = "";
    public Guid? RecordedById { get; set; }
    public bool IsDemo { get; set; }
}

public class FraudAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Number { get; set; }
    public string Code { get; set; } = "";
    public AlertSeverity Severity { get; set; }
    public AlertStatus Status { get; set; } = AlertStatus.Open;
    public string Title { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string EvidenceJson { get; set; } = "{}";
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public Guid? InvoiceId { get; set; }
    public InvoiceEntity? Invoice { get; set; }
    public Guid? AssignedToId { get; set; }
    public AppUser? AssignedTo { get; set; }
    public long ExposureCents { get; set; }
    public string? ResolutionNote { get; set; }
    public bool IsDemo { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<AlertEvent> Events { get; set; } = [];
}

public class AlertEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AlertId { get; set; }
    public string Kind { get; set; } = "";        // created | status | assigned | note
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public string? Note { get; set; }
    public Guid? ActorId { get; set; }
    public string ActorName { get; set; } = "System";
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

public class AuditEntry
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
    public string UserEmail { get; set; } = "system";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}
