using Microsoft.Identity.Client;

namespace KernelMK.Engine.Notifications;

/// <summary>
/// Acquiert un jeton OAuth2 pour authentifier une connexion SMTP/IMAP contre Microsoft 365, via le flux
/// "client credentials" (application, pas utilisateur — adapté à un service Windows sans interaction humaine).
/// Nécessite une application enregistrée dans Entra ID (Azure AD) du tenant CIT, avec l'autorisation
/// d'application "IMAP.AccessAsApp"/"SMTP.SendAsApp" (API "Office 365 Exchange Online") consentie par un
/// administrateur, plus une politique d'authentification autorisant OAuth pour la boîte mail utilisée.
/// Un client MSAL par (TenantId, ClientId) est mis en cache — MSAL gère lui-même le cache de jetons en mémoire
/// et leur renouvellement automatique avant expiration.
/// </summary>
public class Microsoft365TokenProvider
{
    private const string Scope = "https://outlook.office365.com/.default";
    private readonly Dictionary<string, IConfidentialClientApplication> _clients = new();
    private readonly object _lock = new();

    public async Task<string> GetTokenAsync(string tenantId, string clientId, string clientSecret, CancellationToken ct = default)
    {
        var app = GetOrCreateClient(tenantId, clientId, clientSecret);
        try
        {
            var result = await app.AcquireTokenForClient(new[] { Scope }).ExecuteAsync(ct);
            return result.AccessToken;
        }
        catch (MsalServiceException ex)
        {
            throw new InvalidOperationException(
                $"Échec de l'authentification OAuth2 Microsoft 365 (tenant {tenantId}) : {ex.Message}. " +
                "Vérifiez que l'application Entra ID a bien le consentement administrateur pour l'API Office 365 Exchange Online " +
                "et qu'une politique d'authentification OAuth est active pour cette boîte mail.", ex);
        }
    }

    private IConfidentialClientApplication GetOrCreateClient(string tenantId, string clientId, string clientSecret)
    {
        var key = $"{tenantId}:{clientId}";
        lock (_lock)
        {
            if (_clients.TryGetValue(key, out var existing)) return existing;

            var app = ConfidentialClientApplicationBuilder.Create(clientId)
                .WithClientSecret(clientSecret)
                .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
                .Build();

            _clients[key] = app;
            return app;
        }
    }
}
