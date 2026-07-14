using KernelMK.Core.Entities;
using KernelMK.Data.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KernelMK.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobStep> JobSteps => Set<JobStep>();
    public DbSet<JobTrigger> JobTriggers => Set<JobTrigger>();
    public DbSet<JobDependency> JobDependencies => Set<JobDependency>();
    public DbSet<JobExecution> JobExecutions => Set<JobExecution>();
    public DbSet<StepExecutionLog> StepExecutionLogs => Set<StepExecutionLog>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<NotificationRule> NotificationRules => Set<NotificationRule>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Job>(e =>
        {
            e.HasIndex(j => j.Name);
            e.HasMany(j => j.Steps).WithOne(s => s.Job!).HasForeignKey(s => s.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(j => j.Triggers).WithOne(t => t.Job!).HasForeignKey(t => t.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(j => j.NotificationRules).WithOne(n => n.Job!).HasForeignKey(n => n.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(j => j.Executions).WithOne(x => x.Job!).HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<JobDependency>(e =>
        {
            e.HasOne(d => d.Job).WithMany(j => j.Dependencies).HasForeignKey(d => d.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.DependsOnJob).WithMany().HasForeignKey(d => d.DependsOnJobId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<JobStep>()
            .HasOne(s => s.Credential).WithMany().HasForeignKey(s => s.CredentialId).OnDelete(DeleteBehavior.SetNull);

        builder.Entity<JobExecution>()
            .HasMany(x => x.StepLogs).WithOne(l => l.JobExecution!).HasForeignKey(l => l.JobExecutionId).OnDelete(DeleteBehavior.Cascade);
    }
}
