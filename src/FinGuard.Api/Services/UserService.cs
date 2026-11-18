using FinGuard.Api.Contracts;
using FinGuard.Api.Data;
using FinGuard.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FinGuard.Api.Services;

public class UserService(AppDbContext db, IAuditLog audit, ICurrentUser me)
{
    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken ct) =>
        (await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct)).Select(AuthService.ToDto).ToList();

    public async Task<IReadOnlyList<object>> AssignableAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName)
            .Select(u => (object)new { u.Id, u.DisplayName, u.Role }).ToListAsync(ct);

    public async Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest r, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("User");
        if (u.Id == me.Id && (r.Role is not null && r.Role != u.Role || r.IsActive == false))
            throw new BusinessRuleException("You cannot change your own role or deactivate your own account.");

        if (r.Role is not null && r.Role != u.Role)
        {
            if (u.Role == Roles.Admin && r.Role != Roles.Admin && await db.Users.CountAsync(x => x.Role == Roles.Admin && x.IsActive, ct) <= 1)
                throw new BusinessRuleException("At least one active administrator is required.");
            audit.Record("user.role_changed", "User", u.Id.ToString(), $"{u.Email}: {u.Role} → {r.Role}");
            u.Role = r.Role;
            u.SecurityStamp = Guid.NewGuid().ToString("N");
        }
        if (r.IsActive is { } active && active != u.IsActive)
        {
            u.IsActive = active;
            u.SecurityStamp = Guid.NewGuid().ToString("N");
            audit.Record(active ? "user.activated" : "user.deactivated", "User", u.Id.ToString(), u.Email);
        }
        if (r.Unlock == true)
        {
            u.LockoutEndUtc = null;
            u.FailedLoginCount = 0;
            audit.Record("user.unlocked", "User", u.Id.ToString(), u.Email);
        }
        await db.SaveChangesAsync(ct);
        return AuthService.ToDto(u);
    }

    public async Task<UserDto> UpdateProfileAsync(UpdateProfileRequest r, CancellationToken ct)
    {
        var u = await db.Users.FirstAsync(x => x.Id == me.Id, ct);
        u.DisplayName = r.DisplayName.Trim();
        audit.Record("account.profile_updated", "User", u.Id.ToString());
        await db.SaveChangesAsync(ct);
        return AuthService.ToDto(u);
    }
}
