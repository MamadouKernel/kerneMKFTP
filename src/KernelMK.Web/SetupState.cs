namespace KernelMK.Web;

/// <summary>Mémorise si un compte Administrateur existe déjà, pour éviter une requête base à chaque requête HTTP.</summary>
public class SetupState
{
    public bool AdminExists { get; set; }
}
