using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using System.Text.Json;

namespace KernelMK.Engine.Notifications;

/// <summary>Envoie et teste les notifications configurées sur un job pour un événement donné.</summary>
public class NotificationDispatcher
{
    private readonly SmtpOptions _smtp;
    private readonly ILogger<NotificationDispatcher> _logger;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public NotificationDispatcher(IOptions<SmtpOptions> smtp, ILogger<NotificationDispatcher> logger)
    {
        _smtp = smtp.Value;
        _logger = logger;
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
            await client.AuthenticateAsync(_smtp.Username, _smtp.Password ?? string.Empty);
        }
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    private static async Task SendTeamsWebhookAsync(string? url, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var payloadObj = new
        {
            summary = subject,
            text = $"### 🔔 {subject}\n\n" + body.Replace("\r\n", "\n\n")
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
