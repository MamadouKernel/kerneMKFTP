namespace KernelMK.Engine.Notifications;

public class VapidOptions
{
    public const string SectionName = "Vapid";

    /// <summary>Identifiant du serveur exigé par le protocole Web Push, ex. "mailto:support@cit.ci".</summary>
    public string Subject { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string? PrivateKey { get; set; }
}
