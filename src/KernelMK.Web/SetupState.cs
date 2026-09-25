namespace KernelMK.Web;

/// <summary>Single-process bootstrap state. Creation is serialized and rechecked in the database.</summary>
public sealed class SetupState
{
    private volatile bool _adminExists;
    public bool AdminExists { get => _adminExists; set => _adminExists = value; }
    public SemaphoreSlim CreationLock { get; } = new(1, 1);
}
