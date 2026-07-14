using KernelMK.Core.Entities;
using KernelMK.Data.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

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
    public DbSet<TrustedHostKey> TrustedHostKeys => Set<TrustedHostKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Toutes les clés Guid de ce projet sont générées côté client (Guid.NewGuid() dans les entités),
        // jamais par la base. Sans ça, EF Core suppose par convention qu'une clé Guid déjà renseignée
        // désigne une ligne existante : une entité neuve ajoutée via une collection de navigation
        // (ex. existing.Steps.Add(new JobStep{...})) est alors traitée comme "Modified" au lieu de
        // "Added", ce qui génère un UPDATE qui ne trouve aucune ligne et lève un DbUpdateConcurrencyException.
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            var idProperty = entityType.FindProperty("Id");
            if (idProperty is not null && idProperty.ClrType == typeof(Guid))
            {
                idProperty.ValueGenerated = ValueGenerated.Never;
            }
        }

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

        builder.Entity<TrustedHostKey>()
            .HasIndex(k => new { k.Host, k.Port }).IsUnique();
    }
}
