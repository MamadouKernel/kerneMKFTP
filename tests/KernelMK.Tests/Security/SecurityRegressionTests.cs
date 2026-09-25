using System.Security.Claims;
using System.Text;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using KernelMK.Data.Identity;
using KernelMK.Engine.Backup;
using KernelMK.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KernelMK.Tests.Security;

public sealed class SecurityRegressionTests
{
    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/example", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/example", true)]
    [InlineData("https://wns2.notify.windows.com/w/?token=example", true)]
    [InlineData("https://web.push.apple.com/example", true)]
    [InlineData("http://fcm.googleapis.com/fcm/send/example", false)]
    [InlineData("https://127.0.0.1/private", false)]
    [InlineData("https://fcm.googleapis.com.attacker.example/path", false)]
    [InlineData("https://attacker.example@fcm.googleapis.com/path", false)]
    [InlineData("https://fcm.googleapis.com:8443/path", false)]
    [InlineData("https://metadata.google.internal/computeMetadata/v1/", false)]
    public void PushEndpointsOnlyAllowBrowserPushProviders(string endpoint, bool allowed) =>
        Assert.Equal(allowed, PushSubscriptionSecurity.IsValidEndpoint(endpoint));

    [Fact]
    public async Task PushCannotStealOrDeleteAnotherUsersSubscription()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var owner = await fixture.CreateUserAsync("owner@example.test");
        var other = await fixture.CreateUserAsync("other@example.test");
        const string endpoint = "https://fcm.googleapis.com/fcm/send/example";
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.PushSubscriptions.Add(new PushSubscription { UserId = owner.Id, Endpoint = endpoint, P256dh = "key", Auth = "auth" });
            await db.SaveChangesAsync();
        }
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, other.Id)], "test")) };
        await PushSubscriptionSecurity.UnsubscribeAsync(new PushUnsubscribeRequest(endpoint), context, fixture.Factory);
        var key = new byte[65]; key[0] = 4;
        var result = await PushSubscriptionSecurity.SubscribeAsync(new PushSubscribeRequest(endpoint,
            new PushKeys(WebEncoders.Base64UrlEncode(key), WebEncoders.Base64UrlEncode(new byte[16]))), context, fixture.Factory);
        Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)result).StatusCode);
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(owner.Id, (await verify.PushSubscriptions.SingleAsync()).UserId);
    }

    [Fact]
    public async Task LastActiveAdministratorCannotBeDisabledOrLoseRole()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var admin = await fixture.CreateUserAsync("admin@example.test", nameof(AppRole.Administrateur));
        var access = fixture.Services.GetRequiredService<UserAccessService>();
        Assert.False((await access.SetActiveAsync(admin.Id, false)).Succeeded);
        Assert.False((await access.SetRoleAsync(admin.Id, nameof(AppRole.Administrateur), false)).Succeeded);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.True((await db.Users.SingleAsync()).Active);
        Assert.Single(await db.UserRoles.ToListAsync());
    }

    [Fact]
    public async Task RoleAndActivityChangesRevokeOldSecurityStamps()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var user = await fixture.CreateUserAsync("operator@example.test");
        var initialStamp = user.SecurityStamp;
        var access = fixture.Services.GetRequiredService<UserAccessService>();
        Assert.True((await access.SetRoleAsync(user.Id, nameof(AppRole.Developpeur), true)).Succeeded);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var changed = await db.Users.SingleAsync();
            Assert.NotEqual(initialStamp, changed.SecurityStamp);
            initialStamp = changed.SecurityStamp;
            db.PushSubscriptions.Add(new PushSubscription { UserId = user.Id, Endpoint = "https://fcm.googleapis.com/fcm/send/x" });
            await db.SaveChangesAsync();
        }
        Assert.True((await access.SetActiveAsync(user.Id, false)).Succeeded);
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var disabled = await verify.Users.SingleAsync();
        Assert.False(disabled.Active);
        Assert.NotEqual(initialStamp, disabled.SecurityStamp);
        Assert.Empty(await verify.PushSubscriptions.ToListAsync());
    }

    [Fact]
    public async Task OnlineBackupIncludesCommittedWalData()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var source = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
        source.Open();
        using var command = source.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE WalProbe (Value TEXT); INSERT INTO WalProbe VALUES ('committed');";
        command.ExecuteNonQuery();
        Assert.True(File.Exists(fixture.DatabasePath + "-wal"));
        var snapshot = Path.Combine(fixture.DirectoryPath, "snapshot.db");
        BackupService.CreateDatabaseSnapshot(fixture.DatabasePath, snapshot);
        using var copy = new SqliteConnection($"Data Source={snapshot};Mode=ReadOnly;Pooling=False");
        copy.Open();
        using var read = copy.CreateCommand();
        read.CommandText = "SELECT Value FROM WalProbe";
        Assert.Equal("committed", read.ExecuteScalar());
    }

    [Fact]
    public async Task InvalidRestoreCannotReplacePreviouslyValidatedPendingFile()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var backup = new BackupService(fixture.Factory, fixture.Configuration);
        var snapshot = Path.Combine(fixture.DirectoryPath, "snapshot.db");
        BackupService.CreateDatabaseSnapshot(fixture.DatabasePath, snapshot);
        await using (var valid = File.OpenRead(snapshot)) await backup.StagePendingDatabaseRestoreAsync(valid);
        var pending = Path.Combine(fixture.DirectoryPath, BackupService.PendingRestoreFileName);
        var before = await File.ReadAllBytesAsync(pending);
        await Assert.ThrowsAnyAsync<Exception>(() => backup.StagePendingDatabaseRestoreAsync(new MemoryStream(Encoding.ASCII.GetBytes("SQLite format 3\0fake database"))));
        Assert.Equal(before, await File.ReadAllBytesAsync(pending));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.upload"));
    }

    [Fact]
    public async Task ConfigurationImportPreservesExecutionHistoryAndRequiresReactivation()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var job = new Job { Name = "important", Steps = [new JobStep { Name = "step" }] };
        var execution = new JobExecution { JobId = job.Id, Status = JobStatus.Succes, FinishedAt = DateTime.UtcNow };
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.Jobs.Add(job);
            db.JobExecutions.Add(execution);
            await db.SaveChangesAsync();
        }
        var backup = new BackupService(fixture.Factory, fixture.Configuration);
        var json = await backup.ExportConfigurationAsync();
        Assert.Equal(1, await backup.ImportConfigurationAsync(json, "tester"));
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.False((await verify.Jobs.SingleAsync()).Enabled);
        Assert.Equal(execution.Id, (await verify.JobExecutions.SingleAsync()).Id);
        Assert.Single(await verify.JobSteps.ToListAsync());
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string DatabasePath { get; init; }
        public required ServiceProvider Services { get; init; }
        public required IConfiguration Configuration { get; init; }
        public IDbContextFactory<AppDbContext> Factory => Services.GetRequiredService<IDbContextFactory<AppDbContext>>();

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "kernelmk-security-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var database = Path.Combine(directory, "test.db");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"Data Source={database};Pooling=False",
                ["DataDirectory"] = directory
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddKernelMKData(config);
            var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            foreach (var role in DbInitializer.AllRoles) Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
            return new DatabaseFixture { DirectoryPath = directory, DatabasePath = database, Services = provider, Configuration = config };
        }

        public async Task<ApplicationUser> CreateUserAsync(string email, string? role = null)
        {
            await using var scope = Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email };
            Assert.True((await users.CreateAsync(user, "Testing123456!")).Succeeded);
            if (role is not null) Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
