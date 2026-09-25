namespace KernelMK.Core.Entities;

public class JobDefinitionVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Job? Job { get; set; }
    public int VersionNumber { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string ChangeSummary { get; set; } = string.Empty;
    public string DefinitionHash { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = string.Empty;
}
