namespace KernelMK.Core.Entities;

/// <summary>Dépendance déclarative : ce job doit se lancer après un autre job (succès/échec/fin).</summary>
public class JobDependency
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Job? Job { get; set; }

    public Guid DependsOnJobId { get; set; }
    public Job? DependsOnJob { get; set; }

    public JobDependencyCondition Condition { get; set; } = JobDependencyCondition.Succes;
}
