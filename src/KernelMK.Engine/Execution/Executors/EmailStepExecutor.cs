using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>Envoi d'email SMTP (section 4.2 "Communication").</summary>
public class EmailStepExecutor : IStepExecutor
{
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
            if (!string.IsNullOrWhiteSpace(config.AttachmentPath) && File.Exists(config.AttachmentPath))
            {
                builder.Attachments.Add(config.AttachmentPath);
            }
            message.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(config.SmtpHost, config.SmtpPort,
                config.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                context.CancellationToken);

            if (context.ResolvedCredential is { Username: not null })
            {
                await client.AuthenticateAsync(context.ResolvedCredential.Value.Username, context.ResolvedCredential.Value.Secret, context.CancellationToken);
            }

            await client.SendAsync(message, context.CancellationToken);
            await client.DisconnectAsync(true, context.CancellationToken);

            return StepExecutionResult.Ok($"Email envoyé à {config.ToCsv}.");
        }
        catch (Exception ex)
        {
            return StepExecutionResult.Fail(ex.Message);
        }
    }
}
