namespace KernelMK.Engine.Notifications;

public class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseTls { get; set; } = true;
    public string? Username { get; set; }

    /// <summary>Mot de passe SMTP (auth classique), OU client secret Entra ID déchiffré si UseOAuth2Microsoft365 = true.</summary>
    public string? Password { get; set; }
    public string From { get; set; } = "automation-platform@local";

    /// <summary>
    /// Microsoft ayant désactivé l'authentification par mot de passe simple (Basic Auth) sur la plupart des
    /// tenants Microsoft 365 depuis 2022-2023, ce mode utilise à la place un jeton OAuth2 (flux "client
    /// credentials") pour l'envoi des emails d'alerte internes (échec de job, etc.).
    /// </summary>
    public bool UseOAuth2Microsoft365 { get; set; }
    public string? OAuth2ClientId { get; set; }
    public string? OAuth2TenantId { get; set; }
}
