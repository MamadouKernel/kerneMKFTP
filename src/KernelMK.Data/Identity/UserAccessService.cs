using KernelMK.Core;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KernelMK.Data.Identity;

public enum InactivityAction { None, Locked, Deactivated }

/// <summary>Serializes access changes, protects the last available administrator and revokes old sessions.</summary>
public sealed class UserAccessService(IServiceScopeFactory scopes)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<IdentityResult> SetActiveAsync(string userId, bool active) =>
        ChangeAsync(userId, active, null, false);

    public Task<IdentityResult> SetRoleAsync(string userId, string role, bool assign) =>
        ChangeAsync(userId, null, role, assign);

    /// <summary>Rechecks inactivity and remaining administrators atomically with manual access changes.</summary>
    public async Task<InactivityAction> ApplyInactivityPolicyAsync(string userId, DateTime now, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var user = await manager.FindByIdAsync(userId);
            if (user is null || !user.Active) return InactivityAction.None;

            var reference = user.LastLoginAt ?? user.CreatedAt;
            var action = reference <= now.AddDays(-90) ? InactivityAction.Deactivated
                : reference <= now.AddDays(-30) && !IsLocked(user, now) ? InactivityAction.Locked
                : InactivityAction.None;
            if (action == InactivityAction.None || await IsLastAvailableAdministratorAsync(manager, user, now))
                return InactivityAction.None;

            if (action == InactivityAction.Deactivated)
            {
                user.Active = false;
                user.DeactivatedForInactivityAt = now;
            }
            else
            {
                user.LockoutEnabled = true;
                user.LockoutEnd = DateTimeOffset.MaxValue;
            }

            var result = await manager.UpdateAsync(user);
            if (result.Succeeded) result = await manager.UpdateSecurityStampAsync(user);
            if (!result.Succeeded)
                throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Description)));

            await db.PushSubscriptions.Where(subscription => subscription.UserId == userId).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
            return action;
        }
        finally { _gate.Release(); }
    }

    private async Task<IdentityResult> ChangeAsync(string userId, bool? active, string? role, bool assign)
    {
        if (role is not null && !DbInitializer.AllRoles.Contains(role))
            return Failure("Rôle inconnu.");

        await _gate.WaitAsync();
        try
        {
            // A Blazor circuit can live for hours. Never mutate a stale UserManager-tracked user.
            await using var scope = scopes.CreateAsyncScope();
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var user = await manager.FindByIdAsync(userId);
            if (user is null) return Failure("Utilisateur introuvable.");
            var adminRole = nameof(AppRole.Administrateur);
            var removesAdministrator = active == false || (role == adminRole && !assign);
            if (removesAdministrator && user.Active && await IsLastAvailableAdministratorAsync(manager, user, DateTime.UtcNow))
                return Failure("Impossible de retirer ou désactiver le dernier administrateur disponible.");

            IdentityResult result;
            if (active.HasValue)
            {
                user.Active = active.Value;
                if (active.Value && user.DeactivatedForInactivityAt.HasValue)
                {
                    user.DeactivatedForInactivityAt = null;
                    user.LockoutEnd = null;
                    user.AccessFailedCount = 0;
                }
                result = await manager.UpdateAsync(user);
            }
            else
            {
                result = assign
                    ? await manager.AddToRoleAsync(user, role!)
                    : await manager.RemoveFromRoleAsync(user, role!);
            }
            if (!result.Succeeded) return result;
            result = await manager.UpdateSecurityStampAsync(user);
            if (!result.Succeeded) return result;
            if (active == false)
                await db.PushSubscriptions.Where(p => p.UserId == userId).ExecuteDeleteAsync();
            await transaction.CommitAsync();
            return IdentityResult.Success;
        }
        finally { _gate.Release(); }
    }

    private static bool IsLocked(ApplicationUser user, DateTime now) =>
        user.LockoutEnabled && user.LockoutEnd > new DateTimeOffset(now, TimeSpan.Zero);

    private static async Task<bool> IsLastAvailableAdministratorAsync(UserManager<ApplicationUser> manager, ApplicationUser user, DateTime now)
    {
        var role = nameof(AppRole.Administrateur);
        if (!await manager.IsInRoleAsync(user, role)) return false;
        var administrators = await manager.GetUsersInRoleAsync(role);
        return !administrators.Any(other => other.Id != user.Id && other.Active && !IsLocked(other, now));
    }

    private static IdentityResult Failure(string description) =>
        IdentityResult.Failed(new IdentityError { Code = "AccessChangeRejected", Description = description });
}
