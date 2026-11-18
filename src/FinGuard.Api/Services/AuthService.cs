using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FinGuard.Api.Contracts;
using FinGuard.Api.Data;
using FinGuard.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinGuard.Api.Services;

public class AuthOptions
{
    public bool AllowRegistration { get; set; } = true;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int ResetTokenMinutes { get; set; } = 30;
    public string PublicBaseUrl { get; set; } = "http://localhost:5173";
    /// <summary>
    /// Email allowed to become the first administrator on an empty system. When unset, every self-registered
    /// account is an Analyst, so an open registration page can never hand out admin rights.
    /// </summary>
    public string? BootstrapAdminEmail { get; set; }
    /// <summary>Absolute session lifetime, regardless of activity (sliding expiry alone could keep a session forever).</summary>
    public int AbsoluteSessionMinutes { get; set; } = 720;
}

public static class PasswordPolicy
{
    // Small deny-list of the most common passwords that still satisfy the length rule.
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password1234", "123456789012", "qwertyuiop12", "password123!", "administrator", "letmein12345",
        "welcome12345", "iloveyou1234", "passwordpassword", "changeme1234", "aaaaaaaaaaaa", "qwerty123456"
    };

    /// <summary>NIST SP 800-63B style: length over composition, plus a deny-list and context checks.</summary>
    public static string? Validate(string password, string email)
    {
        if (password.Length < 12) return "Password must be at least 12 characters.";
        if (password.Length > 128) return "Password must be at most 128 characters.";
        if (Common.Contains(password)) return "This password is too common.";
        if (password.Distinct().Count() < 5) return "Password is too repetitive.";
        var local = email.Split('@')[0];
        if (local.Length >= 4 && password.Contains(local, StringComparison.OrdinalIgnoreCase))
            return "Password must not contain your email address.";
        return null;
    }
}

public interface IEmailSender { Task SendAsync(string to, string subject, string body, CancellationToken ct); }

/// <summary>Stand-in for a real email provider: stores messages in a local outbox table. Development only.</summary>
public sealed class DevOutboxEmailSender(AppDbContext db, ILogger<DevOutboxEmailSender> log) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        db.Outbox.Add(new OutboxEmail { To = to, Subject = subject, Body = body });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Development email to {To} stored in the demo outbox: {Subject}", to, subject);
    }
}

/// <summary>Used when no email provider is configured: the message is dropped (never logged, since it contains a secret token).</summary>
public sealed class DisabledEmailSender(ILogger<DisabledEmailSender> log) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        log.LogWarning("Email delivery is not configured; message '{Subject}' was not sent.", subject);
        return Task.CompletedTask;
    }
}

public class AuthService(
    AppDbContext db, IPasswordHasher<AppUser> hasher, IAuditLog audit, IEmailSender email,
    IOptions<AuthOptions> options, IHttpContextAccessor http, ILogger<AuthService> log)
{
    private readonly AuthOptions _o = options.Value;
    public const string StampClaim = "fg:stamp";
    public const string AuthTimeClaim = "fg:auth_time";

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    public async Task<UserDto> RegisterAsync(RegisterRequest r, CancellationToken ct)
    {
        if (!_o.AllowRegistration) throw new ForbiddenException("Self-registration is disabled. Ask an administrator for an account.");
        var policy = PasswordPolicy.Validate(r.Password, r.Email);
        if (policy is not null) throw new BusinessRuleException(policy, "weak_password");

        var norm = Normalize(r.Email);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == norm, ct))
            throw new ConflictException("An account with this email already exists.");

        // Bootstrap rule: only the configured owner email can become the first administrator, and only while
        // no administrator exists. Everyone else always starts as an Analyst.
        var isBootstrapAdmin = !string.IsNullOrWhiteSpace(_o.BootstrapAdminEmail)
                               && Normalize(_o.BootstrapAdminEmail) == norm
                               && !await db.Users.AnyAsync(u => u.Role == Roles.Admin, ct);
        var user = new AppUser
        {
            Email = r.Email.Trim(), NormalizedEmail = norm, DisplayName = r.DisplayName.Trim(),
            Role = isBootstrapAdmin ? Roles.Admin : Roles.Analyst,
        };
        user.PasswordHash = hasher.HashPassword(user, r.Password);
        db.Users.Add(user);
        audit.Record("user.registered", "User", user.Id.ToString(), $"Role {user.Role}");
        await db.SaveChangesAsync(ct);
        await SignInAsync(user);
        return ToDto(user);
    }

    public async Task<UserDto> LoginAsync(LoginRequest r, CancellationToken ct)
    {
        const string generic = "Invalid email or password.";
        var norm = Normalize(r.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == norm, ct);
        if (user is null)
        {
            // Spend comparable time to avoid account enumeration by timing.
            hasher.VerifyHashedPassword(new AppUser(), DummyHash.Value, r.Password);
            audit.Record("auth.login_failed", "User", null, "Unknown email");
            await db.SaveChangesAsync(ct);
            throw new AuthFailedException(generic);
        }
        if (user.LockoutEndUtc is { } until && until > DateTime.UtcNow)
        {
            audit.Record("auth.login_locked", "User", user.Id.ToString());
            await db.SaveChangesAsync(ct);
            throw new AuthFailedException("This account is temporarily locked after repeated failed sign-ins. Try again later or reset your password.", "locked_out");
        }
        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, r.Password);
        if (result == PasswordVerificationResult.Failed || !user.IsActive)
        {
            if (result == PasswordVerificationResult.Failed)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= _o.MaxFailedLogins)
                {
                    user.LockoutEndUtc = DateTime.UtcNow.AddMinutes(_o.LockoutMinutes);
                    user.FailedLoginCount = 0;
                    audit.Record("auth.locked_out", "User", user.Id.ToString(), $"Locked for {_o.LockoutMinutes} minutes");
                }
            }
            audit.Record("auth.login_failed", "User", user.Id.ToString(), user.IsActive ? "Bad password" : "Inactive account");
            await db.SaveChangesAsync(ct);
            throw new AuthFailedException(generic);
        }
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, r.Password);

        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginAtUtc = DateTime.UtcNow;
        db.AuditLog.Add(new AuditEntry { Action = "auth.login", EntityType = "User", EntityId = user.Id.ToString(), UserId = user.Id, UserEmail = user.Email, IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString() });
        await db.SaveChangesAsync(ct);
        await SignInAsync(user);
        return ToDto(user);
    }

    public async Task LogoutAsync()
    {
        audit.Record("auth.logout", "User", null);
        await db.SaveChangesAsync();
        await http.HttpContext!.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public async Task ForgotPasswordAsync(string emailAddress, CancellationToken ct)
    {
        var norm = Normalize(emailAddress);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == norm && u.IsActive, ct);
        if (user is null) return; // Same response either way: no account enumeration.

        var raw = TokenEncoding.Base64Url(RandomNumberGenerator.GetBytes(32));
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id, TokenHash = Hash(raw), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(_o.ResetTokenMinutes),
        });
        audit.Record("auth.reset_requested", "User", user.Id.ToString());
        await db.SaveChangesAsync(ct);

        var link = $"{_o.PublicBaseUrl.TrimEnd('/')}/reset-password?token={Uri.EscapeDataString(raw)}";
        await email.SendAsync(user.Email, "Reset your FinGuard AI password",
            $"Hello {user.DisplayName},\n\nUse this link within {_o.ResetTokenMinutes} minutes to choose a new password:\n{link}\n\nIf you did not request this, you can ignore this email.", ct);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest r, CancellationToken ct)
    {
        var hash = Hash(r.Token);
        var token = await db.PasswordResetTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.UsedAtUtc is not null || token.ExpiresAtUtc < DateTime.UtcNow || !token.User.IsActive)
            throw new BusinessRuleException("This reset link is invalid or has expired. Request a new one.", "invalid_token");

        var policy = PasswordPolicy.Validate(r.NewPassword, token.User.Email);
        if (policy is not null) throw new BusinessRuleException(policy, "weak_password");

        var user = token.User;
        user.PasswordHash = hasher.HashPassword(user, r.NewPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N"); // signs out every existing session
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        token.UsedAtUtc = DateTime.UtcNow;
        await db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.UsedAtUtc == null && t.Id != token.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAtUtc, DateTime.UtcNow), ct);
        db.AuditLog.Add(new AuditEntry { Action = "auth.password_reset", EntityType = "User", EntityId = user.Id.ToString(), UserId = user.Id, UserEmail = user.Email });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Password reset completed for user {UserId}", user.Id);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest r, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct) ?? throw new NotFoundException("User");
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, r.CurrentPassword) == PasswordVerificationResult.Failed)
            throw new BusinessRuleException("Current password is incorrect.", "bad_password");
        var policy = PasswordPolicy.Validate(r.NewPassword, user.Email);
        if (policy is not null) throw new BusinessRuleException(policy, "weak_password");
        user.PasswordHash = hasher.HashPassword(user, r.NewPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        audit.Record("account.password_changed", "User", user.Id.ToString());
        await db.SaveChangesAsync(ct);
        await SignInAsync(user); // re-issue this session with the new stamp; others are invalidated
    }

    /// <param name="user">The account to sign in.</param>
    /// <param name="authTimeUnix">Original authentication time to keep when re-issuing a cookie (e.g. after a profile
    /// change), so refreshing claims never extends the absolute session lifetime. Null starts a new session.</param>
    public async Task SignInAsync(AppUser user, long? authTimeUnix = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role),
            new(StampClaim, user.SecurityStamp),
            new(AuthTimeClaim, (authTimeUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await http.HttpContext!.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
    }

    /// <summary>
    /// Runs on every authenticated request: rejects cookies for deleted, deactivated or re-stamped accounts,
    /// role changes, and sessions older than the absolute lifetime.
    /// </summary>
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var options = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
        var idClaim = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = ctx.Principal?.FindFirstValue(StampClaim);
        if (!Guid.TryParse(idClaim, out var id)) { ctx.RejectPrincipal(); return; }
        if (!long.TryParse(ctx.Principal?.FindFirstValue(AuthTimeClaim), out var authTime)
            || DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(authTime) >= TimeSpan.FromMinutes(options.AbsoluteSessionMinutes))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        var user = await db.Users.AsNoTracking().Where(u => u.Id == id)
            .Select(u => new { u.IsActive, u.SecurityStamp, u.Role }).FirstOrDefaultAsync();
        if (user is null || !user.IsActive || user.SecurityStamp != stamp || ctx.Principal!.FindFirstValue(ClaimTypes.Role) != user.Role)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    public static UserDto ToDto(AppUser u) => new(u.Id, u.Email, u.DisplayName, u.Role, u.IsActive, u.IsDemo,
        u.CreatedAtUtc, u.LastLoginAtUtc, u.LockoutEndUtc > DateTime.UtcNow);

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<AppUser>().HashPassword(new AppUser(), "dummy-password-for-timing"));
}

public sealed class AuthFailedException(string message, string code = "invalid_credentials") : AppException(message, 401, code);

file static class TokenEncoding
{
    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
