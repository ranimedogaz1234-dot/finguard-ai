using System.ComponentModel.DataAnnotations;
using FinGuard.Domain;

namespace FinGuard.Api.Contracts;

// ---------- Auth ----------
public record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(2), MaxLength(100)] string DisplayName,
    [Required, MinLength(12), MaxLength(128)] string Password);

public record LoginRequest([Required, EmailAddress, MaxLength(256)] string Email, [Required, MaxLength(128)] string Password);
public record ForgotPasswordRequest([Required, EmailAddress, MaxLength(256)] string Email);
public record ResetPasswordRequest([Required, MaxLength(200)] string Token, [Required, MinLength(12), MaxLength(128)] string NewPassword);
public record ChangePasswordRequest([Required, MaxLength(128)] string CurrentPassword, [Required, MinLength(12), MaxLength(128)] string NewPassword);
public record UpdateProfileRequest([Required, MinLength(2), MaxLength(100)] string DisplayName);
public record UserDto(Guid Id, string Email, string DisplayName, string Role, bool IsActive, bool IsDemo,
    DateTime CreatedAtUtc, DateTime? LastLoginAtUtc, bool IsLockedOut);
public record CsrfDto(string Token);
public record UpdateUserRequest([RegularExpression("^(Admin|Analyst)$")] string? Role, bool? IsActive, bool? Unlock);

// ---------- Suppliers ----------
public record SupplierUpsertRequest(
    [Required, MinLength(2), MaxLength(200)] string Name,
    [Required, RegularExpression("^[A-Za-z]{2}$", ErrorMessage = "Use a 2-letter ISO country code.")] string Country,
    [Required, MaxLength(60)] string Category,
    [MaxLength(40)] string? TaxId,
    [EmailAddress, MaxLength(256)] string? ContactEmail,
    [MaxLength(40)] string? ContactPhone,
    [MaxLength(300)] string? Address,
    [MaxLength(42)] string? Iban,
    [Required, RegularExpression("^[A-Za-z]{3}$")] string Currency,
    [Range(0, 1_000_000_000)] decimal AnnualSpend,
    [Range(0, 365)] int PaymentTermsDays,
    [MaxLength(2000)] string? Notes,
    bool KeepExistingIban = false);

public record SupplierListItem(Guid Id, string Name, string Country, string Category, string Currency,
    int RiskScore, string RiskLevel, string Status, decimal AnnualSpend, int InvoiceCount, int OpenAlerts, bool IsDemo, DateTime UpdatedAtUtc);

public record SupplierDetail(Guid Id, string Name, string Country, string Category, string? TaxId, string? ContactEmail,
    string? ContactPhone, string? Address, string? IbanMasked, bool IbanValid, DateTime? IbanChangedAtUtc, string Currency,
    decimal AnnualSpend, int PaymentTermsDays, string? Notes, string Status, DateOnly OnboardedOn, bool IsDemo,
    RiskAssessmentDto? Risk, SupplierFinancials Financials);

public record SupplierFinancials(int InvoiceCount, decimal TotalInvoiced, decimal PendingAmount, decimal PaidAmount,
    int FlaggedInvoices, int OpenAlerts, decimal AverageInvoice);

public record RiskAssessmentDto(int Score, string Level, string Confidence, string Recommendation,
    IReadOnlyList<RiskFactor> Factors, string Trigger, DateTime AssessedAtUtc);

public record RiskHistoryPoint(DateTime AssessedAtUtc, int Score, string Level, string Trigger);
public record BankChangeDto(DateTime ChangedAtUtc, string? OldIbanMasked, string? NewIbanMasked);

// ---------- Invoices ----------
public record InvoiceLineDto([Required, MaxLength(200)] string Description, [Range(0.0001, 1_000_000)] decimal Quantity, [Range(0, 100_000_000)] decimal UnitPrice);

public record InvoiceCreateRequest(
    [Required] Guid SupplierId,
    [Required, MaxLength(64)] string Number,
    [MaxLength(64)] string? PoNumber,
    [Required] DateOnly InvoiceDate,
    DateOnly? DueDate,
    [Required, RegularExpression("^[A-Za-z]{3}$")] string Currency,
    [Range(0, 1_000_000_000)] decimal DeclaredTotal,
    [MaxLength(42)] string? Iban,
    [Required, MinLength(1), MaxLength(100)] List<InvoiceLineDto> Lines);

public record InvoiceListItem(Guid Id, string Number, Guid SupplierId, string SupplierName, DateOnly InvoiceDate,
    string Currency, decimal Total, string Status, string Decision, int VerificationPoints, int DiscrepancyCount,
    bool HasPdf, bool IsDemo);

public record DiscrepancyDto(string Code, string Severity, string Explanation);

public record InvoiceDetail(Guid Id, string Number, Guid SupplierId, string SupplierName, string SupplierRiskLevel,
    string? PoNumber, DateOnly InvoiceDate, DateOnly? DueDate, string Currency, decimal Total, decimal LinesTotal,
    string? IbanMasked, string Status, string Decision, int VerificationPoints, IReadOnlyList<DiscrepancyDto> Discrepancies,
    string Summary, DateTime? VerifiedAtUtc, string? CreatedBy, string? ReviewedBy, string? ReviewNote, DateTime? ReviewedAtUtc,
    string? PdfOriginalName, bool IsDemo, IReadOnlyList<InvoiceLineDto> Lines, IReadOnlyList<AlertListItem> Alerts,
    PurchaseOrderDto? PurchaseOrder, InvoicePermissions Permissions);

public record InvoicePermissions(bool CanApprove, bool CanReject, bool CanMarkPaid, string? Reason);
public record PurchaseOrderDto(string Number, string Currency, decimal Total, DateOnly IssuedOn, IReadOnlyList<InvoiceLineDto> Lines);
public record ReviewRequest([MaxLength(1000)] string? Note);
public record ExtractionResult(bool TextFound, string? Number, DateOnly? InvoiceDate, DateOnly? DueDate, decimal? Total,
    string? Currency, string? Iban, string? PoNumber, Guid? MatchedSupplierId, string? MatchedSupplierName,
    IReadOnlyList<string> Notes);

// ---------- Alerts ----------
public record AlertListItem(Guid Id, int Number, string Code, string Severity, string Status, string Title,
    Guid SupplierId, string SupplierName, Guid? InvoiceId, string? InvoiceNumber, string? AssignedTo,
    decimal Exposure, bool IsDemo, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public record AlertEventDto(string Kind, string? FromStatus, string? ToStatus, string? Note, string ActorName, DateTime AtUtc);

public record AlertDetail(AlertListItem Alert, string Explanation, IReadOnlyDictionary<string, string> Evidence,
    Guid? AssignedToId, string? ResolutionNote, IReadOnlyList<AlertEventDto> Events, IReadOnlyList<string> AllowedTransitions,
    bool CanEdit);

public record AlertStatusRequest([Required] AlertStatus Status, [MaxLength(1000)] string? Note);
public record AlertAssignRequest(Guid? UserId);
public record AlertNoteRequest([Required, MinLength(1), MaxLength(1000)] string Note);

// ---------- Dashboard ----------
public record KpiDto(int TotalInvoices, int VerifiedInvoices, int FlaggedInvoices, int RejectedBySystem, int PendingReview,
    decimal TotalProcessed, decimal FinancialExposure, decimal OpenAlertExposure, int HighRiskSuppliers, int ActiveSuppliers,
    int OpenAlerts, int CriticalAlerts, int ConfirmedFraud, string BaseCurrency, IReadOnlyList<string> OtherCurrencies);
public record TrendPoint(string Month, int Invoices, int Flagged, decimal Amount, decimal FlaggedAmount);
public record NamedCount(string Name, int Count);
public record ActivityItem(DateTime AtUtc, string Actor, string Action, string EntityType, string? EntityId, string? Details);
public record TopRiskSupplier(Guid Id, string Name, int Score, string Level, int OpenAlerts);
public record DashboardDto(KpiDto Kpis, IReadOnlyList<TrendPoint> Trend, IReadOnlyList<NamedCount> RiskDistribution,
    IReadOnlyList<NamedCount> AlertsBySeverity, IReadOnlyList<NamedCount> AlertsByStatus, IReadOnlyList<NamedCount> TopDiscrepancies,
    IReadOnlyList<TopRiskSupplier> TopRiskSuppliers, IReadOnlyList<ActivityItem> RecentActivity, IReadOnlyList<NamedCount> SpendByCategory,
    DateTime GeneratedAtUtc);

// ---------- Assistant ----------
public record AssistantMessage([Required, RegularExpression("^(user|assistant)$")] string Role, [Required, MaxLength(4000)] string Content);
public record AssistantRequest([Required, MinLength(1), MaxLength(20)] List<AssistantMessage> Messages);
public record AssistantReference(string Type, string Id, string Label);
public record AssistantResponse(string Reply, string Mode, string? Model, IReadOnlyList<AssistantReference> References);
public record AssistantStatus(string Mode, string? Provider, string? Model, string Description);
