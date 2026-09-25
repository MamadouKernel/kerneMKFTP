using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KernelMK.Tests.Security;

public sealed class AccountLifecycleTests
{
    [Theory]
    [InlineData(45, InactivityAction.Locked)]
    [InlineData(100, InactivityAction.Deactivated)]
    public async Task ConcurrentInactivityChangesPreserveOneAvailableAdministrator(int inactiveDays, InactivityAction expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateUserAsync(inactiveDays, administrator: true);
        var second = await fixture.CreateUserAsync(inactiveDays, administrator: true);
        var outcomes = await Task.WhenAll(
            fixture.Access.ApplyInactivityPolicyAsync(first.Id, fixture.Now),
            fixture.Access.ApplyInactivityPolicyAsync(second.Id, fixture.Now));
        Assert.Single(outcomes, action => action == expected);
        Assert.Single(outcomes, action => action == InactivityAction.None);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var users = await db.Users.ToListAsync();
        Assert.Single(users, user => user.Active && (!user.LockoutEnabled || user.LockoutEnd is null || user.LockoutEnd <= fixture.Now));
    }

    [Theory]
    [InlineData(45, InactivityAction.Locked)]
    [InlineData(100, InactivityAction.Deactivated)]
    public async Task InactivityRevokesSessionsAndPushSubscriptions(int inactiveDays, InactivityAction expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.CreateUserAsync(inactiveDays);
        var oldStamp = user.SecurityStamp;
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.PushSubscriptions.Add(new PushSubscription { UserId = user.Id, Endpoint = "https://fcm.googleapis.com/fcm/send/test" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(expected, await fixture.Access.ApplyInactivityPolicyAsync(user.Id, fixture.Now));
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var changed = await verify.Users.SingleAsync();
        Assert.NotEqual(oldStamp, changed.SecurityStamp);
        Assert.Empty(await verify.PushSubscriptions.ToListAsync());
        Assert.Equal(expected != InactivityAction.Deactivated, changed.Active);
        if (expected == InactivityAction.Deactivated) Assert.Equal(fixture.Now, changed.DeactivatedForInactivityAt);
        else Assert.Equal(DateTimeOffset.MaxValue, changed.LockoutEnd);
        Assert.Equal(InactivityAction.None, await fixture.Access.ApplyInactivityPolicyAsync(user.Id, fixture.Now));
    }

    [Fact]
    public async Task LockedAdministratorDoesNotCountAsAnAvailableReplacement()
    {
        await using var fixture = await Fixture.CreateAsync();
        var available = await fixture.CreateUserAsync(100, administrator: true);
        await fixture.CreateUserAsync(100, administrator: true, locked: true);
        Assert.Equal(InactivityAction.None, await fixture.Access.ApplyInactivityPolicyAsync(available.Id, fixture.Now));
        Assert.False((await fixture.Access.SetActiveAsync(available.Id, false)).Succeeded);
        Assert.False((await fixture.Access.SetRoleAsync(available.Id, nameof(AppRole.Administrateur), false)).Succeeded);
    }

    [Fact]
    public async Task RecentLoginIsRecheckedBeforeApplyingInactivityPolicy()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.CreateUserAsync(100);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            await db.Users.Where(candidate => candidate.Id == user.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.LastLoginAt, fixture.Now));
        }
        Assert.Equal(InactivityAction.None, await fixture.Access.ApplyInactivityPolicyAsync(user.Id, fixture.Now));
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(user.SecurityStamp, (await verify.Users.SingleAsync()).SecurityStamp);
    }

    [Fact]
    public async Task ReactivatingAnInactiveAccountAlsoClearsItsInactivityLock()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.CreateUserAsync(100, locked: true);
        Assert.Equal(InactivityAction.Deactivated, await fixture.Access.ApplyInactivityPolicyAsync(user.Id, fixture.Now));
        Assert.True((await fixture.Access.SetActiveAsync(user.Id, true)).Succeeded);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var changed = await db.Users.SingleAsync();
        Assert.True(changed.Active);
        Assert.Null(changed.DeactivatedForInactivityAt);
        Assert.Null(changed.LockoutEnd);
        Assert.Equal(0, changed.AccessFailedCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required ServiceProvider Services { get; init; }
        public DateTime Now { get; } = DateTime.UtcNow;
        public UserAccessService Access => Services.GetRequiredService<UserAccessService>();
        public IDbContextFactory<AppDbContext> Factory => Services.GetRequiredService<IDbContextFactory<AppDbContext>>();

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "kernelmk-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(directory, "test.db")};Pooling=False",
                ["DataDirectory"] = directory
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddKernelMKData(config);
            var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            foreach (var role in DbInitializer.AllRoles)
                Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
            return new Fixture { DirectoryPath = directory, Services = provider };
        }

        public async Task<ApplicationUser> CreateUserAsync(int inactiveDays, bool administrator = false, bool locked = false)
        {
            await using var scope = Services.CreateAsyncScope();
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var email = $"{Guid.NewGuid():N}@example.test";
            var user = new ApplicationUser
            {
                UserName = email, Email = email, CreatedAt = Now.AddDays(-inactiveDays),
                LockoutEnabled = true, LockoutEnd = locked ? DateTimeOffset.MaxValue : null
            };
            Assert.True((await manager.CreateAsync(user, "Testing123456!")).Succeeded);
            if (administrator) Assert.True((await manager.AddToRoleAsync(user, nameof(AppRole.Administrateur))).Succeeded);
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
