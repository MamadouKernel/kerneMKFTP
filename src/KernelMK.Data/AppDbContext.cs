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
    public DbSet<JobExecutionRequest> JobExecutionRequests => Set<JobExecutionRequest>();
    public DbSet<JobDefinitionVersion> JobDefinitionVersions => Set<JobDefinitionVersion>();
    public DbSet<StepExecutionLog> StepExecutionLogs => Set<StepExecutionLog>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<NotificationRule> NotificationRules => Set<NotificationRule>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();
    public DbSet<TrustedHostKey> TrustedHostKeys => Set<TrustedHostKey>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

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

        builder.Entity<JobExecution>(e =>
        {
            e.HasMany(x => x.StepLogs).WithOne(l => l.JobExecution!).HasForeignKey(l => l.JobExecutionId).OnDelete(DeleteBehavior.Cascade);
            // Quasi toutes les requêtes du dashboard/historique filtrent par période (StartedAt) et/ou par
            // statut (Succès/Échec/EnCours) — sans index, chaque appel scanne la table entière, ce qui devient
            // très lent en production dès que l'historique grossit (constaté : lenteur après déploiement réel).
            e.HasIndex(x => new { x.StartedAt, x.Status });
            e.Property(x => x.JobDefinitionHash).HasMaxLength(64);
        });

        builder.Entity<JobDefinitionVersion>(e =>
        {
            e.HasOne(v => v.Job).WithMany().HasForeignKey(v => v.JobId).OnDelete(DeleteBehavior.Cascade);
            e.Property(v => v.DefinitionHash).HasMaxLength(64);
            e.Property(v => v.ChangeSummary).HasMaxLength(500);
            e.HasIndex(v => new { v.JobId, v.VersionNumber }).IsUnique();
        });
        builder.Entity<JobExecutionRequest>(e =>
        {
            e.HasOne(r => r.Job).WithMany().HasForeignKey(r => r.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Execution).WithMany().HasForeignKey(r => r.ExecutionId).OnDelete(DeleteBehavior.SetNull);
            e.Property(r => r.IdempotencyKey).HasMaxLength(200);
            e.Property(r => r.ExpectedJobDefinitionHash).HasMaxLength(64);
            e.HasIndex(r => r.IdempotencyKey).IsUnique();
            e.HasIndex(r => new { r.Status, r.Priority, r.RequestedAt });
        });

        builder.Entity<StepExecutionLog>()
            .HasIndex(l => l.StartedAt);

        builder.Entity<Notification>()
            .HasIndex(n => n.CreatedAt);

        builder.Entity<JobTrigger>()
            .HasIndex(t => t.NextRunAt);

        builder.Entity<Job>()
            .HasIndex(j => j.LastRunAt);

        builder.Entity<AuditLogEntry>()
            .HasIndex(a => a.Timestamp);

        builder.Entity<TrustedHostKey>()
            .HasIndex(k => new { k.Host, k.Port }).IsUnique();

        builder.Entity<PushSubscription>(e =>
        {
            e.HasIndex(p => p.Endpoint).IsUnique();
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
