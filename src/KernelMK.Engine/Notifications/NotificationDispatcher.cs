using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Data;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace KernelMK.Engine.Notifications;

/// <summary>Envoie les notifications configurées sur un job pour un événement donné (section 4.6).</summary>
public class NotificationDispatcher
{
    private readonly SmtpOptions _smtp;
    private readonly ILogger<NotificationDispatcher> _logger;
    private static readonly HttpClient HttpClient = new();

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
                    case NotificationChannel.Webhook:
                    case NotificationChannel.Teams:
                        await SendWebhookAsync(rule.WebhookUrl, subject, body);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Échec d'envoi de notification pour le job {JobName}, événement {Event}", job.Name, evt);
            }
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
        await client.ConnectAsync(_smtp.Host, _smtp.Port, _smtp.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None);
        if (!string.IsNullOrEmpty(_smtp.Username))
        {
            await client.AuthenticateAsync(_smtp.Username, _smtp.Password);
        }
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    private static async Task SendWebhookAsync(string? url, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var payload = System.Text.Json.JsonSerializer.Serialize(new { subject, body });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        await HttpClient.PostAsync(url, content);
    }
}
