using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using System.Net;
using System.Text.Json;
using WebPush;
using WebPushSubscription = WebPush.PushSubscription;

namespace KernelMK.Engine.Notifications;

/// <summary>Envoie et teste les notifications configurées sur un job pour un événement donné.</summary>
public class NotificationDispatcher
{
    private readonly IOptionsMonitor<SmtpOptions> _smtpMonitor;
    private readonly IOptionsMonitor<VapidOptions> _vapidMonitor;
    private readonly NotificationThrottleService _throttle;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly KernelMK.Engine.Infrastructure.IDbWriteQueue _writeQueue;
    private readonly Microsoft365TokenProvider _tokenProvider;
    private readonly ILogger<NotificationDispatcher> _logger;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly WebPushClient PushClient = new();

    /// <summary>Valeur SMTP courante, relue à chaque appel : contrairement à IOptions&lt;T&gt;, IOptionsMonitor&lt;T&gt;
    /// reflète les modifications faites à appsettings.json sans redémarrage du service.</summary>
    private SmtpOptions _smtp => _smtpMonitor.CurrentValue;
    private VapidOptions _vapid => _vapidMonitor.CurrentValue;

    public NotificationDispatcher(
        IOptionsMonitor<SmtpOptions> smtpMonitor, IOptionsMonitor<VapidOptions> vapidMonitor, NotificationThrottleService throttle,
        IDbContextFactory<AppDbContext> dbFactory, KernelMK.Engine.Infrastructure.IDbWriteQueue writeQueue,
        Microsoft365TokenProvider tokenProvider, ILogger<NotificationDispatcher> logger)
    {
        _smtpMonitor = smtpMonitor;
        _vapidMonitor = vapidMonitor;
        _throttle = throttle;
        _dbFactory = dbFactory;
        _writeQueue = writeQueue;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    /// <summary>
    /// Comme <see cref="DispatchAsync"/>, mais regroupe les occurrences répétées d'un même événement pour un
    /// même job : une seule notification externe part par fenêtre de temps (identifiée par <paramref name="dedupeKey"/>),
    /// les occurrences suivantes dans la fenêtre sont silencieusement supprimées pour ne pas spammer email/Teams/webhook.
    /// </summary>
    public async Task DispatchGroupedAsync(Job job, NotificationEvent evt, JobExecution? execution, string dedupeKey, TimeSpan groupingWindow, string? extraDetails = null)
    {
        if (_throttle.ShouldSuppress(dedupeKey, groupingWindow))
        {
            _logger.LogInformation("Notification groupée supprimée (déjà envoyée récemment dans la fenêtre) : job {JobName}, événement {Event}.", job.Name, evt);
            return;
        }

        // Job manquant / Anomalie de durée n'ont aujourd'hui aucune autre trace visible dans l'app (contrairement
        // à Succès/Échec/Timeout, déjà visibles via JobExecution) : on les persiste pour le tableau de bord.
        if (evt is NotificationEvent.JobManquant or NotificationEvent.AnomalieDuree)
        {
            await PersistNotificationAsync(job, evt, extraDetails);
        }

        // Notification push navigateur : diffusée à tous les appareils abonnés (indépendamment des règles
        // NotificationRule par job, qui ciblent des destinataires email/webhook fixes) — chaque utilisateur
        // choisit lui-même d'activer ou non les alertes push depuis n'importe quelle page de kernelMK.
        await SendPushBroadcastAsync(job, evt, extraDetails);

        await DispatchAsync(job, evt, execution, extraDetails);
    }

    /// <summary>
    /// Envoie une notification push navigateur (Web Push) à tous les appareils abonnés. Silencieux si les clés
    /// VAPID ne sont pas configurées (section "Vapid" de appsettings.json) ou si aucun appareil n'est abonné.
    /// Un abonnement expiré ou révoqué côté navigateur (HTTP 404/410 renvoyé par le service push) est retiré
    /// automatiquement de la base pour ne pas continuer à échouer indéfiniment dessus.
    /// </summary>
    private async Task SendPushBroadcastAsync(Job job, NotificationEvent evt, string? extraDetails)
    {
        var vapid = _vapid;
        if (string.IsNullOrWhiteSpace(vapid.PublicKey) || string.IsNullOrWhiteSpace(vapid.PrivateKey))
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var subscriptions = await db.PushSubscriptions.AsNoTracking().ToListAsync();
        if (subscriptions.Count == 0) return;

        var vapidDetails = new VapidDetails(vapid.Subject, vapid.PublicKey, vapid.PrivateKey);
        var payload = JsonSerializer.Serialize(new
        {
            title = $"{job.Name} — {evt}",
            body = extraDetails ?? evt.ToString(),
            tag = $"kernelmk-{job.Id}-{evt}",
            url = "/"
        });

        var staleIds = new List<Guid>();
        foreach (var sub in subscriptions)
        {
            try
            {
                var webSub = new WebPushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await PushClient.SendNotificationAsync(webSub, payload, vapidDetails);
                _logger.LogInformation("Notification push envoyée avec succès vers l'abonnement {SubId} (job {JobName}, événement {Event}).", sub.Id, job.Name, evt);
            }
            catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                staleIds.Add(sub.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Échec d'envoi d'une notification push (job {JobName}, événement {Event}).", job.Name, evt);
            }
        }

        if (staleIds.Count > 0)
        {
            await db.PushSubscriptions.Where(p => staleIds.Contains(p.Id)).ExecuteDeleteAsync();
        }
    }

    private Task PersistNotificationAsync(Job job, NotificationEvent evt, string? message)
    {
        // Empilé via la file d'écriture plutôt qu'écrit directement : évite qu'une notification proactive
        // déclenchée pendant l'exécution d'un job se dispute le verrou SQLite avec d'autres écritures en cours.
        _writeQueue.Enqueue(async (db, ct) =>
        {
            db.Notifications.Add(new Notification
            {
                JobId = job.Id,
                JobName = job.Name,
                Event = evt,
                Message = message ?? evt.ToString()
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Échec de la persistance de la notification proactive pour le job {JobName}.", job.Name);
            }
        });
        return Task.CompletedTask;
    }

    public async Task DispatchAsync(Job job, NotificationEvent evt, JobExecution? execution, string? extraDetails = null)
    {
        var rules = job.NotificationRules.Where(r => r.Enabled && r.Event == evt).ToList();
        if (rules.Count == 0) return;

        var subject = $"[{job.Criticite}] Job {job.Name} - {evt}";
        var body = BuildBody(job, evt, execution, extraDetails);

        foreach (var rule in rules)
        {
            try
            {
                switch (rule.Channel)
                {
                    case NotificationChannel.Email:
                        await SendEmailAsync(rule.RecipientsCsv, subject, body);
                        break;
                    case NotificationChannel.Teams:
                        await SendTeamsWebhookAsync(rule.WebhookUrl, subject, body);
                        break;
                    case NotificationChannel.Webhook:
                        await SendGenericWebhookAsync(rule.WebhookUrl, subject, body);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Échec d'envoi de notification pour le job {JobName}, événement {Event}, canal {Channel}", job.Name, evt, rule.Channel);
            }
        }
    }

    /// <summary>Teste immédiatement une règle de notification et retourne le diagnostic (succès/échec).</summary>
    public async Task<(bool Success, string Message)> TestRuleAsync(NotificationRule rule, string jobName = "Test")
    {
        var testSubject = $"[TEST] Notification KernelMK - Job {jobName} ({rule.Event})";
        var testBody = $"Ceci est un test de notification déclenché depuis l'interface KernelMK.\n\n" +
                       $"• Job testé : {jobName}\n" +
                       $"• Événement configuré : {rule.Event}\n" +
                       $"• Canal : {rule.Channel}\n" +
                       $"• Date : {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n" +
                       $"Si vous recevez ce message, votre canal de notification est parfaitement opérationnel sur Côte d'Ivoire Terminal (CIT).";

        try
        {
            switch (rule.Channel)
            {
                case NotificationChannel.Email:
                    if (string.IsNullOrWhiteSpace(rule.RecipientsCsv))
                        return (false, "Veuillez renseigner au moins une adresse e-mail destinataire.");
                    if (string.IsNullOrWhiteSpace(_smtp.Host))
                        return (false, "Serveur SMTP non configuré dans appsettings.json (section Smtp:Host).");
                    await SendEmailAsync(rule.RecipientsCsv, testSubject, testBody);
                    return (true, $"E-mail de test envoyé avec succès à : {rule.RecipientsCsv}");

                case NotificationChannel.Teams:
                    if (string.IsNullOrWhiteSpace(rule.WebhookUrl))
                        return (false, "Veuillez renseigner l'URL du connecteur Webhook Microsoft Teams.");
                    if (!Uri.TryCreate(rule.WebhookUrl, UriKind.Absolute, out var teamsUri) || (teamsUri.Scheme != "http" && teamsUri.Scheme != "https"))
                        return (false, "L'URL du Webhook Teams est invalide (doit commencer par https://).");
                    await SendTeamsWebhookAsync(rule.WebhookUrl, testSubject, testBody);
                    return (true, "Message de test envoyé avec succès dans le canal Microsoft Teams !");

                case NotificationChannel.Webhook:
                    if (string.IsNullOrWhiteSpace(rule.WebhookUrl))
                        return (false, "Veuillez renseigner l'URL cible du Webhook HTTP.");
                    if (!Uri.TryCreate(rule.WebhookUrl, UriKind.Absolute, out var webhookUri) || (webhookUri.Scheme != "http" && webhookUri.Scheme != "https"))
                        return (false, "L'URL du Webhook est invalide (doit commencer par http:// ou https://).");
                    await SendGenericWebhookAsync(rule.WebhookUrl, testSubject, testBody);
                    return (true, "Appel Webhook HTTP exécuté avec succès (Code 200 OK) !");

                default:
                    return (false, "Canal de notification inconnu.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec du test de notification pour le canal {Channel}", rule.Channel);
            var detail = ex.InnerException != null ? $"{ex.Message} -> {ex.InnerException.Message}" : ex.Message;
            return (false, $"Erreur lors de l'envoi : {detail}");
        }
    }

    private static string BuildBody(Job job, NotificationEvent evt, JobExecution? execution, string? extraDetails)
    {
        var lines = new List<string>
        {
            $"Job : {job.Name}",
            $"Événement : {evt}",
            $"Criticité : {job.Criticite}",
            $"Date : {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
        };
        if (execution is not null)
        {
            lines.Add($"Statut d'exécution : {execution.Status}");
            lines.Add($"Code retour : {execution.ReturnCode}");
            lines.Add($"Message : {execution.Message}");
        }
        if (!string.IsNullOrWhiteSpace(extraDetails)) lines.Add(extraDetails);
        return string.Join(Environment.NewLine, lines);
    }

    private async Task SendEmailAsync(string? recipientsCsv, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(recipientsCsv) || string.IsNullOrWhiteSpace(_smtp.Host)) return;

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_smtp.From));
        foreach (var to in recipientsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            message.To.Add(MailboxAddress.Parse(to));
        }
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        client.Timeout = 15000;
        await client.ConnectAsync(_smtp.Host, _smtp.Port, _smtp.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None);
        if (!string.IsNullOrEmpty(_smtp.Username))
        {
            if (_smtp.UseOAuth2Microsoft365)
            {
                if (string.IsNullOrWhiteSpace(_smtp.OAuth2ClientId) || string.IsNullOrWhiteSpace(_smtp.OAuth2TenantId))
                {
                    throw new InvalidOperationException("SMTP OAuth2 Microsoft 365 activé mais OAuth2ClientId/OAuth2TenantId manquant dans la configuration (section \"Smtp\").");
                }
                var accessToken = await _tokenProvider.GetTokenAsync(_smtp.OAuth2TenantId, _smtp.OAuth2ClientId, _smtp.Password ?? string.Empty);
                await client.AuthenticateAsync(new SaslMechanismOAuth2(_smtp.Username, accessToken));
            }
            else
            {
                await client.AuthenticateAsync(_smtp.Username, _smtp.Password ?? string.Empty);
            }
        }
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    /// <summary>
    /// Envoie une carte Adaptive Card (format actuel des workflows Power Automate "Quand une requête webhook
    /// Teams est reçue"). L'ancien format "MessageCard"/connecteur Office 365 — utilisé ici avant ce correctif —
    /// a été retiré par Microsoft en 2024-2025 ; les webhooks encore configurés avec ce format peuvent déjà
    /// refuser silencieusement ce payload. Compatible avec le nouveau type de webhook Teams (URL au format
    /// https://.../workflows/...) recommandé par Microsoft depuis la dépréciation des connecteurs entrants.
    /// </summary>
    private static async Task SendTeamsWebhookAsync(string? url, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var payloadObj = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = new object[]
                        {
                            new { type = "TextBlock", text = $"🔔 {subject}", weight = "Bolder", size = "Medium", wrap = true },
                            new { type = "TextBlock", text = body, wrap = true }
                        }
                    }
                }
            }
        };
        var payload = JsonSerializer.Serialize(payloadObj);
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await HttpClient.PostAsync(url, content);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Le serveur Teams a renvoyé l'erreur HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) : {errBody}");
        }
    }

    private static async Task SendGenericWebhookAsync(string? url, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var payloadObj = new { subject, body, timestamp = DateTime.UtcNow };
        var payload = JsonSerializer.Serialize(payloadObj);
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await HttpClient.PostAsync(url, content);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Le serveur Webhook a renvoyé l'erreur HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) : {errBody}");
        }
    }
}
