using FinGuard.Api.Services;
using FinGuard.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FinGuard.Api.Data;

public class DemoOptions
{
    /// <summary>Seed fictional demo data into an empty database at startup.</summary>
    public bool Seed { get; set; }
    /// <summary>Password for the fictional demo accounts. Set via configuration; never a real credential.</summary>
    public string? Password { get; set; }
}

/// <summary>
/// Builds a reproducible, fully fictional dataset by replaying 12 months of invoices through the real
/// verification, fraud-detection and risk-scoring pipeline. Every row is flagged IsDemo.
/// </summary>
public class DemoSeeder(AppDbContext db, VerificationService verifier, RiskService risk, IPasswordHasher<AppUser> hasher,
    TimeProvider clock, ILogger<DemoSeeder> log)
{
    private readonly Random _rng = new(2026);
    private int _poSeq = 7000;

    private record Item(string Description, decimal Price);
    private record Profile(string Name, string Country, string Category, int AgeDays, decimal Base, int PerYear, string Scenario, string? TaxId = null);

    private static readonly Dictionary<string, Item[]> Catalog = new()
    {
        ["IT Hardware"] = [new("Business laptop 14\"", 1150), new("27\" monitor", 289), new("Docking station", 189), new("Wireless keyboard set", 59)],
        ["Cloud Services"] = [new("Compute reserved capacity (month)", 2400), new("Object storage (TB-month)", 21), new("Managed database (month)", 860)],
        ["Logistics"] = [new("Full truckload Tunis–Lyon", 3200), new("Pallet handling", 18), new("Customs brokerage", 240)],
        ["Office Supplies"] = [new("A4 paper (box of 5 reams)", 24.5m), new("Toner cartridge", 92), new("Ergonomic chair", 340)],
        ["Consulting"] = [new("Senior consultant day rate", 950), new("Workshop facilitation", 1800), new("Audit preparation package", 4200)],
        ["Facilities"] = [new("Office cleaning (month)", 1650), new("HVAC maintenance visit", 420), new("Security guard shift", 210)],
        ["Marketing"] = [new("Campaign design package", 3500), new("Trade-show booth rental", 5200), new("Printed brochures (1,000)", 640)],
        ["Raw Materials"] = [new("Cold-rolled steel coil (t)", 780), new("Aluminium sheet (t)", 2350), new("Industrial fasteners (box)", 46)],
        ["Telecom"] = [new("Business fibre line (month)", 310), new("Mobile plan (user-month)", 28), new("SIP trunk channels", 15)],
        ["Security Services"] = [new("Penetration test (day)", 1100), new("SOC monitoring (month)", 2900), new("Security awareness training", 1450)],
    };

    private static readonly Profile[] Profiles =
    [
        new("Northwind Office Supplies", "FR", "Office Supplies", 1400, 1200, 14, "clean", "FR40303265045"),
        new("Helios Data Systems", "DE", "IT Hardware", 900, 6800, 10, "price_creep", "DE811907980"),
        new("Baltic Freight Partners", "LT", "Logistics", 700, 7400, 12, "bank_change"),
        new("Nova Consulting Group", "TN", "Consulting", 40, 4800, 5, "split_round"),
        new("Atlas Office Furniture", "ES", "Office Supplies", 1100, 2600, 10, "duplicates", "ESB12345678"),
        new("Meridian Facility Services", "FR", "Facilities", 1600, 2100, 12, "outlier"),
        new("Quantum Cloud Hosting", "NL", "Cloud Services", 820, 5200, 12, "currency"),
        new("Crestline Trading LLC", "US", "Raw Materials", 75, 8600, 4, "fraud"),
        new("Sahel Steel Works", "TN", "Raw Materials", 1300, 9400, 9, "clean"),
        new("Orion Telecom", "IT", "Telecom", 2000, 1400, 12, "clean"),
        new("Cobalt Security Labs", "GB", "Security Services", 600, 5600, 6, "no_po"),
        new("Lumen Marketing Studio", "BE", "Marketing", 480, 4300, 7, "dup_payment"),
        new("Vertex Engineering Consultants", "DE", "Consulting", 1500, 6200, 8, "clean"),
        new("Medina Logistics", "TN", "Logistics", 950, 3900, 11, "late_flags"),
        new("Polar Paper Co.", "PL", "Office Supplies", 1250, 800, 9, "false_positive"),
        new("Aurora Data Centres", "IE", "Cloud Services", 1700, 7600, 12, "clean"),
        new("Granite Facilities Group", "IT", "Facilities", 30, 2300, 2, "new"),
        new("Pacific Components Inc.", "US", "IT Hardware", 1000, 3500, 6, "clean"),
    ];

    public async Task SeedAsync(DemoOptions options, CancellationToken ct = default)
    {
        if (await db.Suppliers.AnyAsync(ct) || await db.AuditLog.AnyAsync(a => a.Action == "demo.purged", ct))
        {
            log.LogInformation("Demo seed skipped: database already contains data, or demo data was purged.");
            return;
        }
        if (string.IsNullOrWhiteSpace(options.Password) || options.Password.Length < 12)
            throw new InvalidOperationException("Demo:Password must be configured (12+ characters) to seed demo accounts.");

        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        log.LogInformation("Seeding fictional demo data…");

        var admin = await EnsureUser("admin@finguard.demo", "Amira Ben Salah", Roles.Admin, options.Password, ct);
        var analyst = await EnsureUser("analyst@finguard.demo", "Youssef Trabelsi", Roles.Analyst, options.Password, ct);
        var analyst2 = await EnsureUser("lina.analyst@finguard.demo", "Lina Haddad", Roles.Analyst, options.Password, ct);

        // ---- Suppliers ----
        var suppliers = new List<(Supplier s, Profile p)>();
        foreach (var p in Profiles)
        {
            var s = new Supplier
            {
                Name = p.Name, Country = p.Country, Category = p.Category, TaxId = p.TaxId ?? $"{p.Country}{_rng.Next(10_000_000, 99_999_999)}",
                ContactEmail = $"accounts@{Slug(p.Name)}.example", ContactPhone = $"+{_rng.Next(30, 99)} {_rng.Next(100, 999)} {_rng.Next(100, 999)} {_rng.Next(100, 999)}",
                Address = $"{_rng.Next(1, 220)} Fictional Avenue, {p.Country}", Iban = MakeIban(p.Country),
                Currency = "EUR", AnnualSpendCents = Cents.From(p.Base * p.PerYear), PaymentTermsDays = p.Category == "Logistics" ? 45 : 30,
                OnboardedOn = today.AddDays(-p.AgeDays), IsDemo = true,
                CreatedAtUtc = now.AddDays(-p.AgeDays), UpdatedAtUtc = now.AddDays(-p.AgeDays),
                Notes = "Fictional demonstration supplier.",
            };
            db.Suppliers.Add(s);
            suppliers.Add((s, p));
        }
        await db.SaveChangesAsync(ct);

        // ---- Build the invoice timeline ----
        var plan = new List<(DateTime at, Supplier s, Profile p, Func<Task<InvoiceEntity>> make)>();
        foreach (var (s, p) in suppliers)
        {
            var start = new[] { today.AddDays(-p.AgeDays + 5), today.AddDays(-360) }.Max();
            var span = Math.Max(1, today.DayNumber - start.DayNumber - 2);
            var count = Math.Max(1, (int)Math.Round(p.PerYear * span / 365.0));
            for (var k = 0; k < count; k++)
            {
                var date = start.AddDays((int)((k + _rng.NextDouble() * 0.8) * span / count));
                var at = date.ToDateTime(new TimeOnly(_rng.Next(8, 18), _rng.Next(0, 60)), DateTimeKind.Utc).AddDays(_rng.Next(0, 3));
                if (at > now) at = now.AddMinutes(-_rng.Next(30, 600));
                var idx = k;
                plan.Add((at, s, p, () => Task.FromResult(NormalInvoice(s, p, date, at, idx))));
            }
        }
        AddScenarios(plan, suppliers, today, now);

        // ---- Replay chronologically through the real pipeline ----
        var monthEnds = Enumerable.Range(0, 12).Select(k => new DateOnly(today.Year, today.Month, 1).AddMonths(-11 + k).AddDays(-1))
            .Where(d => d < today).ToList();
        var nextSnapshot = 0;
        foreach (var step in plan.OrderBy(x => x.at))
        {
            while (nextSnapshot < monthEnds.Count && monthEnds[nextSnapshot].ToDateTime(new TimeOnly(23, 0), DateTimeKind.Utc) < step.at)
                await SnapshotAsync(monthEnds[nextSnapshot++], suppliers.Select(x => x.s), ct);

            var inv = await step.make();
            db.Invoices.Add(inv);
            await db.SaveChangesAsync(ct);
            await verifier.VerifyAsync(inv, ct, inv.CreatedAtUtc, recordRiskHistory: false);
            await ReviewAsync(inv, step.s, step.p, now, admin, analyst, ct);
        }
        while (nextSnapshot < monthEnds.Count) await SnapshotAsync(monthEnds[nextSnapshot++], suppliers.Select(x => x.s), ct);

        await WorkAlertsAsync(admin, analyst, analyst2, now, ct);

        foreach (var (s, _) in suppliers) await risk.AssessAsync(s.Id, "Current assessment", ct);
        db.AuditLog.Add(new AuditEntry { Action = "demo.seeded", EntityType = "System", Details = "Fictional demonstration dataset generated", AtUtc = now });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Demo data ready: {Suppliers} suppliers, {Invoices} invoices, {Alerts} alerts.",
            await db.Suppliers.CountAsync(ct), await db.Invoices.CountAsync(ct), await db.Alerts.CountAsync(ct));
    }

    private async Task<AppUser> EnsureUser(string email, string name, string role, string password, CancellationToken ct)
    {
        var norm = AuthService.Normalize(email);
        var existing = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == norm, ct);
        if (existing is not null) return existing;
        var u = new AppUser { Email = email, NormalizedEmail = norm, DisplayName = name, Role = role, IsDemo = true };
        u.PasswordHash = hasher.HashPassword(u, password);
        db.Users.Add(u);
        await db.SaveChangesAsync(ct);
        return u;
    }

    private async Task SnapshotAsync(DateOnly monthEnd, IEnumerable<Supplier> all, CancellationToken ct)
    {
        var at = monthEnd.ToDateTime(new TimeOnly(23, 0), DateTimeKind.Utc);
        foreach (var s in all.Where(s => s.OnboardedOn <= monthEnd))
            await risk.AssessAsync(s.Id, "Monthly portfolio review", ct, at, recordHistory: true);
    }

    // ---------- Invoice factories ----------
    private PurchaseOrderEntity NewPo(Supplier s, DateOnly date, IEnumerable<(Item item, decimal qty)> lines, string currency = "EUR")
    {
        var po = new PurchaseOrderEntity
        {
            Number = $"PO-{++_poSeq}", SupplierId = s.Id, Currency = currency, IssuedOn = date.AddDays(-_rng.Next(5, 20)), IsDemo = true,
            Lines = lines.Select(l => new PurchaseOrderLine { Description = l.item.Description, Quantity = l.qty, UnitPriceCents = Cents.From(l.item.Price) }).ToList(),
        };
        po.TotalCents = po.Lines.Sum(l => Cents.From(Math.Round(l.Quantity * Cents.To(l.UnitPriceCents), 2)));
        db.PurchaseOrders.Add(po);
        return po;
    }

    private List<(Item item, decimal qty)> PickLines(Profile p, decimal target)
    {
        var items = Catalog[p.Category];
        var lines = new List<(Item, decimal)>();
        var main = items[_rng.Next(items.Length)];
        var qty = Math.Max(1, Math.Round(target * (decimal)(0.7 + _rng.NextDouble() * 0.6) / main.Price));
        lines.Add((main, qty));
        if (_rng.NextDouble() < 0.5)
        {
            var extra = items[_rng.Next(items.Length)];
            if (extra != main) lines.Add((extra, _rng.Next(1, 4)));
        }
        return lines;
    }

    private InvoiceEntity Build(Supplier s, string number, DateOnly date, DateTime at, PurchaseOrderEntity? po,
        IEnumerable<(string desc, decimal qty, decimal price)> lines, decimal? declared = null, string currency = "EUR", string? iban = null)
    {
        var l = lines.Select(x => new InvoiceLine { Description = x.desc, Quantity = x.qty, UnitPriceCents = Cents.From(x.price) }).ToList();
        var total = declared ?? l.Sum(x => Math.Round(x.Quantity * Cents.To(x.UnitPriceCents), 2));
        return new InvoiceEntity
        {
            Number = number, NormalizedNumber = InvoiceVerifier.NormalizeNumber(number), SupplierId = s.Id, PoNumber = po?.Number,
            InvoiceDate = date, DueDate = date.AddDays(s.PaymentTermsDays), Currency = currency, TotalCents = Cents.From(total),
            Iban = iban ?? s.Iban, IsDemo = true, CreatedAtUtc = at, UpdatedAtUtc = at, Lines = l,
        };
    }

    private string Number(Profile p, DateOnly d, int k) => $"{Prefix(p.Name)}-{d:yyMM}-{k + 1:000}";

    private InvoiceEntity NormalInvoice(Supplier s, Profile p, DateOnly date, DateTime at, int k)
    {
        var lines = PickLines(p, p.Base);
        var usePo = p.Scenario != "no_po" || k % 3 == 0;
        var po = usePo ? NewPo(s, date, lines) : null;
        var invLines = lines.Select(x => (x.item.Description, x.qty, x.item.Price)).ToList();

        // Scenario-specific drift on otherwise normal invoices.
        if (p.Scenario == "price_creep" && k >= 4)
            invLines = invLines.Select((x, i) => i == 0 ? (x.Description, x.qty, Math.Round(x.Price * (1.03m + 0.02m * (k - 4)), 2)) : x).ToList();
        if (p.Scenario == "late_flags" && k % 4 == 3)
            invLines.Add(("Fuel surcharge", 1, Math.Round(p.Base * 0.04m, 2)));
        return Build(s, Number(p, date, k), date, at, po, invLines);
    }

    private void AddScenarios(List<(DateTime at, Supplier s, Profile p, Func<Task<InvoiceEntity>> make)> plan,
        List<(Supplier s, Profile p)> suppliers, DateOnly today, DateTime now)
    {
        (Supplier s, Profile p) S(string scenario) => suppliers.First(x => x.p.Scenario == scenario);
        DateTime At(DateOnly d, int hour = 10) { var t = d.ToDateTime(new TimeOnly(hour, 15), DateTimeKind.Utc); return t > now ? now.AddMinutes(-45) : t; }

        // Bank details changed 12 days ago; a new invoice follows, plus one asking payment to an unknown account.
        var bank = S("bank_change");
        var changedAt = now.AddDays(-12);
        var newIban = MakeIban("LT");
        db.BankDetailChanges.Add(new BankDetailChange { SupplierId = bank.s.Id, OldIbanMasked = Iban.Mask(bank.s.Iban), NewIbanMasked = Iban.Mask(newIban), ChangedAtUtc = changedAt });
        bank.s.Iban = newIban;
        bank.s.IbanChangedAtUtc = changedAt;
        plan.Add((At(today.AddDays(-6)), bank.s, bank.p, () =>
        {
            var lines = PickLines(bank.p, bank.p.Base);
            var po = NewPo(bank.s, today.AddDays(-6), lines);
            return Task.FromResult(Build(bank.s, "BFP-URGENT-0091", today.AddDays(-6), At(today.AddDays(-6)), po, lines.Select(x => (x.item.Description, x.qty, x.item.Price))));
        }));
        plan.Add((At(today.AddDays(-3), 14), bank.s, bank.p, () =>
        {
            var lines = PickLines(bank.p, bank.p.Base * 1.4m);
            var po = NewPo(bank.s, today.AddDays(-3), lines);
            return Task.FromResult(Build(bank.s, "BFP-2611-207", today.AddDays(-3), At(today.AddDays(-3), 14), po,
                lines.Select(x => (x.item.Description, x.qty, x.item.Price)), iban: MakeIban("GB")));
        }));

        // New consultancy: split invoicing just below the threshold and round amounts.
        var nova = S("split_round");
        foreach (var (days, amount, no) in new[] { (-9, 6_000m, "NCG-0012"), (-6, 5_500m, "NCG-0013"), (-2, 9_800m, "NCG-0014") })
            plan.Add((At(today.AddDays(days)), nova.s, nova.p, () => Task.FromResult(
                Build(nova.s, no, today.AddDays(days), At(today.AddDays(days)), null, [("Advisory services (fixed fee)", 1, amount)]))));

        // Duplicate resubmission (same number, different format) and a possible duplicate.
        var atlas = S("duplicates");
        var dupDate = today.AddDays(-40);
        plan.Add((At(dupDate), atlas.s, atlas.p, () =>
        {
            var lines = new List<(Item, decimal)> { (Catalog["Office Supplies"][2], 8) };
            var po = NewPo(atlas.s, dupDate, lines);
            return Task.FromResult(Build(atlas.s, "AOF-2026-0458", dupDate, At(dupDate), po, [(Catalog["Office Supplies"][2].Description, 8, 340)]));
        }));
        plan.Add((At(dupDate.AddDays(19), 16), atlas.s, atlas.p, () => Task.FromResult(
            Build(atlas.s, "AOF 2026 458", dupDate, At(dupDate.AddDays(19), 16), null, [(Catalog["Office Supplies"][2].Description, 8, 340)]))));
        plan.Add((At(today.AddDays(-11)), atlas.s, atlas.p, () => Task.FromResult(
            Build(atlas.s, "AOF-2026-0502", today.AddDays(-11), At(today.AddDays(-11)), null, [("Desk organiser set", 20, 31.5m)]))));
        plan.Add((At(today.AddDays(-10), 15), atlas.s, atlas.p, () => Task.FromResult(
            Build(atlas.s, "AOF-2026-0503", today.AddDays(-11), At(today.AddDays(-10), 15), null, [("Desk organiser set (reissued)", 20, 31.5m)]))));

        // Facilities outlier: about 4x the usual monthly invoice.
        var mer = S("outlier");
        plan.Add((At(today.AddDays(-8)), mer.s, mer.p, () =>
        {
            var item = Catalog["Facilities"][0];
            var lines = new List<(Item, decimal)> { (item, 5) };
            var po = NewPo(mer.s, today.AddDays(-8), lines);
            return Task.FromResult(Build(mer.s, "MFS-2611-031", today.AddDays(-8), At(today.AddDays(-8)), po, [(item.Description, 5, item.Price)]));
        }));

        // Cloud provider bills in USD against a EUR purchase order, and over the PO.
        var q = S("currency");
        plan.Add((At(today.AddDays(-15)), q.s, q.p, () =>
        {
            var item = Catalog["Cloud Services"][0];
            var po = NewPo(q.s, today.AddDays(-15), [(item, 2)]);
            return Task.FromResult(Build(q.s, "QCH-INV-88412", today.AddDays(-15), At(today.AddDays(-15)), po, [(item.Description, 2, 2650)], currency: "USD"));
        }));

        // Confirmed fraud case: payment redirected to an unknown account, plus an inflated invoice.
        var fraud = S("fraud");
        plan.Add((At(today.AddDays(-25)), fraud.s, fraud.p, () =>
        {
            var item = Catalog["Raw Materials"][1];
            var po = NewPo(fraud.s, today.AddDays(-25), [(item, 4)]);
            return Task.FromResult(Build(fraud.s, "CTL-3391", today.AddDays(-25), At(today.AddDays(-25)), po, [(item.Description, 5, 2480)], iban: MakeIban("CY")));
        }));

        // Duplicate payment risk: same amount as an invoice already paid last month.
        var lumen = S("dup_payment");
        plan.Add((At(today.AddDays(-50)), lumen.s, lumen.p, () =>
        {
            var item = Catalog["Marketing"][0];
            var po = NewPo(lumen.s, today.AddDays(-50), [(item, 1)]);
            return Task.FromResult(Build(lumen.s, "LMS-0731", today.AddDays(-50), At(today.AddDays(-50)), po, [(item.Description, 1, item.Price)]));
        }));
        plan.Add((At(today.AddDays(-5)), lumen.s, lumen.p, () =>
        {
            var item = Catalog["Marketing"][0];
            var po = NewPo(lumen.s, today.AddDays(-5), [(item, 1)]);
            return Task.FromResult(Build(lumen.s, "LMS-0788", today.AddDays(-5), At(today.AddDays(-5)), po, [(item.Description, 1, item.Price)]));
        }));

        // Total mismatch (arithmetic error) from a normally clean supplier.
        var sahel = suppliers.First(x => x.p.Name == "Sahel Steel Works");
        plan.Add((At(today.AddDays(-4)), sahel.s, sahel.p, () =>
        {
            var item = Catalog[sahel.p.Category][0];
            var po = NewPo(sahel.s, today.AddDays(-4), [(item, 10)]);
            return Task.FromResult(Build(sahel.s, "SSW-2611-090", today.AddDays(-4), At(today.AddDays(-4)), po, [(item.Description, 10, item.Price)], declared: item.Price * 10 + 750));
        }));

        // A round amount that turns out legitimate (dismissed later).
        var polar = S("false_positive");
        plan.Add((At(today.AddDays(-20)), polar.s, polar.p, () =>
        {
            var item = Catalog["Office Supplies"][2];
            var po = NewPo(polar.s, today.AddDays(-20), [(item, 25)]);
            return Task.FromResult(Build(polar.s, "PPC-1120", today.AddDays(-20), At(today.AddDays(-20)), po, [("Ergonomic chair", 25, 320)], declared: 8000));
        }));
    }

    // ---------- Human review simulation ----------
    private async Task ReviewAsync(InvoiceEntity inv, Supplier s, Profile p, DateTime now, AppUser admin, AppUser analyst, CancellationToken ct)
    {
        var age = (now - inv.CreatedAtUtc).TotalDays;
        if (age < 10) return; // recent invoices stay pending
        var reviewer = Cents.To(inv.TotalCents) >= 10_000 || inv.Decision == InvoiceDecision.Reject ? admin : analyst;
        var reviewedAt = inv.CreatedAtUtc.AddHours(_rng.Next(3, 60));
        if (reviewedAt > now) return;

        if (inv.Decision == InvoiceDecision.AutoApprove || (inv.Decision == InvoiceDecision.ManualReview && _rng.NextDouble() < 0.75 && p.Scenario is not "fraud"))
        {
            inv.Status = InvoiceStatus.Approved;
            inv.ReviewNote = inv.Decision == InvoiceDecision.AutoApprove ? null : "Checked with requester; variance accepted.";
        }
        else
        {
            inv.Status = InvoiceStatus.Rejected;
            inv.ReviewNote = inv.Decision == InvoiceDecision.Reject ? "Rejected per verification findings." : "Supplier asked to reissue against the purchase order.";
        }
        inv.ReviewedById = reviewer.Id;
        inv.ReviewedAtUtc = reviewedAt;
        db.AuditLog.Add(new AuditEntry { AtUtc = reviewedAt, UserId = reviewer.Id, UserEmail = reviewer.Email,
            Action = inv.Status == InvoiceStatus.Approved ? "invoice.approved" : "invoice.rejected", EntityType = "Invoice", EntityId = inv.Id.ToString(), Details = inv.Number });

        if (inv.Status == InvoiceStatus.Approved && age > 25)
        {
            var paidOn = DateOnly.FromDateTime(reviewedAt).AddDays(_rng.Next(5, 20));
            if (paidOn.ToDateTime(TimeOnly.MinValue) < now)
            {
                inv.Status = InvoiceStatus.Paid;
                db.Payments.Add(new Payment { SupplierId = s.Id, InvoiceId = inv.Id, AmountCents = inv.TotalCents, Currency = inv.Currency,
                    Iban = s.Iban, PaidOn = paidOn, Reference = $"PAY-{inv.Number}", RecordedById = admin.Id, IsDemo = true });
            }
        }
        await db.SaveChangesAsync(ct);
        if (inv.Status == InvoiceStatus.Rejected) await risk.AssessAsync(s.Id, $"Invoice {inv.Number} rejected", ct, reviewedAt, recordHistory: false);
    }

    private async Task WorkAlertsAsync(AppUser admin, AppUser analyst, AppUser analyst2, DateTime now, CancellationToken ct)
    {
        var alerts = await db.Alerts.Include(a => a.Supplier).OrderBy(a => a.Number).ToListAsync(ct);
        void Move(FraudAlert a, AppUser who, AlertStatus to, string note, double hoursAfter)
        {
            var at = a.UpdatedAtUtc.AddHours(hoursAfter);
            if (at > now) at = now.AddMinutes(-5);
            db.AlertEvents.Add(new AlertEvent { AlertId = a.Id, Kind = "status", FromStatus = a.Status.ToString(), ToStatus = to.ToString(), Note = note, ActorId = who.Id, ActorName = who.DisplayName, AtUtc = at });
            db.AuditLog.Add(new AuditEntry { AtUtc = at, UserId = who.Id, UserEmail = who.Email, Action = "alert.status_changed", EntityType = "Alert", EntityId = a.Id.ToString(), Details = $"#{a.Number} {a.Status} → {to}" });
            a.Status = to; a.UpdatedAtUtc = at;
            if (AlertWorkflow.IsTerminal(to)) a.ResolutionNote = note;
        }
        void Assign(FraudAlert a, AppUser who) { a.AssignedToId = who.Id; db.AlertEvents.Add(new AlertEvent { AlertId = a.Id, Kind = "assigned", Note = $"Assigned to {who.DisplayName}", ActorId = admin.Id, ActorName = admin.DisplayName, AtUtc = a.UpdatedAtUtc.AddHours(1) }); }

        foreach (var a in alerts)
        {
            var scenario = Profiles.First(p => p.Name == a.Supplier.Name).Scenario;
            var old = (now - a.CreatedAtUtc).TotalDays > 30;
            if (scenario == "fraud")
            {
                Assign(a, analyst);
                Move(a, analyst, AlertStatus.Investigating, "Called supplier on the number from the original contract: they never changed bank details.", 2);
                Move(a, analyst, AlertStatus.Escalated, "Escalating: invoice requests payment to an unknown Cypriot account.", 5);
                if (a.Code == "PAYMENT_DETAILS_MISMATCH")
                    Move(a, admin, AlertStatus.ConfirmedFraud, "Confirmed impersonation attempt (fictional scenario). Payment blocked, case reported to the bank.", 20);
            }
            else if (scenario == "false_positive" || (a.Code == "ROUND_AMOUNT" && old))
            {
                Move(a, analyst2, AlertStatus.Dismissed, "Round figure matches the negotiated fixed-price quote. Not suspicious.", 6);
            }
            else if (a.Code == "PAYMENT_DETAILS_MISMATCH" || a.Code == "BANK_DETAILS_RECENTLY_CHANGED")
            {
                Assign(a, analyst);
                Move(a, analyst, AlertStatus.Investigating, "Requested written confirmation of new bank details; payment on hold.", 3);
            }
            else if (a.Code == "SPLIT_INVOICING")
            {
                Assign(a, analyst2);
                Move(a, analyst2, AlertStatus.Investigating, "Comparing against the engagement letter.", 4);
            }
            else if (old)
            {
                Move(a, a.Severity >= AlertSeverity.High ? admin : analyst, AlertStatus.Resolved, "Reviewed with procurement; supplier issued a corrected invoice.", 30);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // ---------- Helpers ----------
    private string MakeIban(string country)
    {
        var len = country switch { "FR" => 23, "DE" => 18, "ES" => 20, "IT" => 23, "NL" => 14, "BE" => 12, "GB" => 18, "IE" => 18, "LT" => 16, "PL" => 24, "TN" => 20, "CY" => 24, _ => 20 };
        var digits = string.Concat(Enumerable.Range(0, len).Select(_ => (char)('0' + _rng.Next(10))));
        return Iban.Create(country == "US" ? "GB" : country, digits);
    }

    private static string Prefix(string name) => string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3).Select(w => char.ToUpperInvariant(w[0])));
    private static string Slug(string name) => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>Removes every fictional business record so the platform can be used with real data.</summary>
public class DemoDataService(AppDbContext db, Infrastructure.IAuditLog audit)
{
    public async Task<object> PurgeAsync(CancellationToken ct)
    {
        var demoSupplierIds = await db.Suppliers.Where(s => s.IsDemo).Select(s => s.Id).ToListAsync(ct);
        var invoiceIds = await db.Invoices.Where(i => i.IsDemo || demoSupplierIds.Contains(i.SupplierId)).Select(i => i.Id).ToListAsync(ct);
        var alerts = await db.Alerts.Where(a => a.IsDemo || demoSupplierIds.Contains(a.SupplierId)).ExecuteDeleteAsync(ct);
        var payments = await db.Payments.Where(p => p.IsDemo || demoSupplierIds.Contains(p.SupplierId)).ExecuteDeleteAsync(ct);
        await db.InvoiceLines.Where(l => invoiceIds.Contains(l.InvoiceId)).ExecuteDeleteAsync(ct);
        var invoices = await db.Invoices.Where(i => invoiceIds.Contains(i.Id)).ExecuteDeleteAsync(ct);
        var poIds = await db.PurchaseOrders.Where(p => p.IsDemo || demoSupplierIds.Contains(p.SupplierId)).Select(p => p.Id).ToListAsync(ct);
        await db.Set<PurchaseOrderLine>().Where(l => poIds.Contains(l.PurchaseOrderId)).ExecuteDeleteAsync(ct);
        await db.PurchaseOrders.Where(p => poIds.Contains(p.Id)).ExecuteDeleteAsync(ct);
        await db.RiskAssessments.Where(r => demoSupplierIds.Contains(r.SupplierId)).ExecuteDeleteAsync(ct);
        await db.BankDetailChanges.Where(b => demoSupplierIds.Contains(b.SupplierId)).ExecuteDeleteAsync(ct);
        var suppliers = await db.Suppliers.Where(s => s.IsDemo).ExecuteDeleteAsync(ct);
        audit.Record("demo.purged", "System", null, $"{suppliers} suppliers, {invoices} invoices, {alerts} alerts, {payments} payments removed");
        await db.SaveChangesAsync(ct);
        return new { suppliers, invoices, alerts, payments };
    }
}
