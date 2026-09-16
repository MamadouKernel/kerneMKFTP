using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Core.StepConfigs;
using KernelMK.Engine.Notifications;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>Envoi d'email SMTP (section 4.2 "Communication").</summary>
public class EmailStepExecutor : IStepExecutor
{
    private readonly Microsoft365TokenProvider _tokenProvider;

    public EmailStepExecutor(Microsoft365TokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    public IReadOnlyCollection<StepType> SupportedTypes { get; } = new[] { StepType.EmailSmtp };

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var config = JsonSerializer.Deserialize<EmailStepConfig>(context.Step.ConfigJson)
                      ?? throw new InvalidOperationException("Configuration email invalide.");

        try
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(config.From));
            foreach (var to in config.ToCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                message.To.Add(MailboxAddress.Parse(to));
            }
            message.Subject = config.Subject;

            var builder = new BodyBuilder { TextBody = config.Body };
            var attachmentSent = !string.IsNullOrWhiteSpace(config.AttachmentPath) && File.Exists(config.AttachmentPath);
            if (attachmentSent)
            {
                builder.Attachments.Add(config.AttachmentPath);
            }
            message.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(config.SmtpHost, config.SmtpPort,
                config.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                context.CancellationToken);

            if (context.ResolvedCredential is { Username: not null } cred)
            {
                if (cred.AuthType == CredentialAuthType.OAuth2Microsoft365)
                {
                    if (string.IsNullOrWhiteSpace(cred.OAuth2ClientId) || string.IsNullOrWhiteSpace(cred.OAuth2TenantId))
                    {
                        return StepExecutionResult.Fail("Credential OAuth2 Microsoft 365 incomplet : Id d'application (ClientId) ou Id de tenant manquant.");
                    }
                    var accessToken = await _tokenProvider.GetTokenAsync(cred.OAuth2TenantId, cred.OAuth2ClientId, cred.Secret ?? string.Empty, context.CancellationToken);
                    await client.AuthenticateAsync(new SaslMechanismOAuth2(cred.Username, accessToken), context.CancellationToken);
                }
                else
                {
                    await client.AuthenticateAsync(cred.Username, cred.Secret ?? string.Empty, context.CancellationToken);
                }
            }

            await client.SendAsync(message, context.CancellationToken);
            await client.DisconnectAsync(true, context.CancellationToken);

            return StepExecutionResult.Ok(
                $"Email envoyé à {config.ToCsv}.",
                filesProcessedCsv: attachmentSent ? config.AttachmentPath : null);
        }
        catch (Exception ex)
        {
            return StepExecutionResult.Fail(ex.Message);
        }
    }
}
