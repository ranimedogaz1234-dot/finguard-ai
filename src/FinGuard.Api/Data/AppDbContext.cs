using Microsoft.EntityFrameworkCore;

namespace FinGuard.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<OutboxEmail> Outbox => Set<OutboxEmail>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<BankDetailChange> BankDetailChanges => Set<BankDetailChange>();
    public DbSet<RiskAssessmentRecord> RiskAssessments => Set<RiskAssessmentRecord>();
    public DbSet<PurchaseOrderEntity> PurchaseOrders => Set<PurchaseOrderEntity>();
    public DbSet<InvoiceEntity> Invoices => Set<InvoiceEntity>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<FraudAlert> Alerts => Set<FraudAlert>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Enums are stored as readable strings.
        b.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<AppUser>(e =>
        {
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.NormalizedEmail).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.Role).HasMaxLength(20);
        });
        m.Entity<PasswordResetToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<Supplier>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.RiskScore);
            e.HasMany(x => x.RiskAssessments).WithOne().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.BankDetailChanges).WithOne().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<RiskAssessmentRecord>().HasIndex(x => new { x.SupplierId, x.AssessedAtUtc });
        m.Entity<PurchaseOrderEntity>(e =>
        {
            e.HasIndex(x => new { x.SupplierId, x.Number }).IsUnique();
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<InvoiceEntity>(e =>
        {
            e.Property(x => x.Number).HasMaxLength(64);
            e.Property(x => x.NormalizedNumber).HasMaxLength(64);
            e.HasIndex(x => new { x.SupplierId, x.NormalizedNumber });
            e.HasIndex(x => x.InvoiceDate);
            e.HasIndex(x => x.Status);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Payment>(e =>
        {
            e.HasIndex(x => new { x.SupplierId, x.PaidOn });
            e.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<InvoiceEntity>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.SetNull);
        });
        m.Entity<FraudAlert>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.Status, x.Severity });
            e.HasIndex(x => new { x.InvoiceId, x.Code });
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.AssignedTo).WithMany().HasForeignKey(x => x.AssignedToId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);
        });
        m.Entity<AuditEntry>(e =>
        {
            e.HasIndex(x => x.AtUtc);
            e.Property(x => x.Action).HasMaxLength(64);
        });
    }
}
