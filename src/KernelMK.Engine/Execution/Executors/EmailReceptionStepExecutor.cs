using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.Entities;
using KernelMK.Core.StepConfigs;
using KernelMK.Engine.Notifications;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>
/// Relève une boîte mail par IMAP et télécharge les pièces jointes correspondant aux filtres (ex. des messages
/// EDIFACT reçus par email d'un armateur/partenaire plutôt que par SFTP). Les fichiers téléchargés peuvent ensuite
/// être traités par une étape suivante du même job (ex. EdifactMessage en mode Analyser).
/// </summary>
public class EmailReceptionStepExecutor : IStepExecutor
{
    private readonly Microsoft365TokenProvider _tokenProvider;

    public EmailReceptionStepExecutor(Microsoft365TokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    public IReadOnlyCollection<StepType> SupportedTypes { get; } = new[] { StepType.ReceptionEmailImap };

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var config = JsonSerializer.Deserialize<EmailReceptionStepConfig>(context.Step.ConfigJson)
                      ?? throw new InvalidOperationException("Configuration de réception email invalide.");

        if (context.ResolvedCredential is not { Host: not null } cred)
        {
            return StepExecutionResult.Fail("Aucun credential IMAP (hôte/utilisateur/mot de passe) associé à cette étape — sélectionne un credential de type « Compte Email (IMAP) ».");
        }

        if (string.IsNullOrWhiteSpace(config.DownloadPath))
        {
            return StepExecutionResult.Fail("Aucun dossier local de destination (DownloadPath) renseigné pour les pièces jointes.");
        }

        var extensions = (config.AttachmentExtensionsCsv ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToList();

        try
        {
            Directory.CreateDirectory(config.DownloadPath);
        }
        catch (Exception ex)
        {
            return StepExecutionResult.Fail($"Impossible de créer/accéder au dossier de destination '{config.DownloadPath}' : {ex.Message}");
        }

        using var client = new ImapClient();

        try
        {
            await client.ConnectAsync(cred.Host, cred.Port ?? 993, SecureSocketOptions.SslOnConnect, context.CancellationToken);

            if (cred.AuthType == CredentialAuthType.OAuth2Microsoft365)
            {
                if (string.IsNullOrWhiteSpace(cred.OAuth2ClientId) || string.IsNullOrWhiteSpace(cred.OAuth2TenantId))
                {
                    return StepExecutionResult.Fail("Credential OAuth2 Microsoft 365 incomplet : Id d'application (ClientId) ou Id de tenant manquant.");
                }
                var accessToken = await _tokenProvider.GetTokenAsync(cred.OAuth2TenantId, cred.OAuth2ClientId, cred.Secret ?? string.Empty, context.CancellationToken);
                await client.AuthenticateAsync(new SaslMechanismOAuth2(cred.Username ?? string.Empty, accessToken), context.CancellationToken);
            }
            else
            {
                await client.AuthenticateAsync(cred.Username ?? string.Empty, cred.Secret ?? string.Empty, context.CancellationToken);
            }

            var folder = string.Equals(config.MailboxFolder, "INBOX", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(config.MailboxFolder)
                ? client.Inbox
                : await client.GetFolderAsync(config.MailboxFolder, context.CancellationToken);

            await folder.OpenAsync(FolderAccess.ReadWrite, context.CancellationToken);

            var uids = await folder.SearchAsync(SearchQuery.NotSeen, context.CancellationToken);
            var downloaded = new List<string>();
            var processedCount = 0;

            foreach (var uid in uids)
            {
                if (processedCount >= config.MaxMessagesPerRun) break;

                var message = await folder.GetMessageAsync(uid, context.CancellationToken);

                if (!string.IsNullOrWhiteSpace(config.SenderFilter) &&
                    !message.From.Mailboxes.Any(m => m.Address.Contains(config.SenderFilter, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(config.SubjectFilter) &&
                    !(message.Subject?.Contains(config.SubjectFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    continue;
                }

                var matchedAny = false;
                foreach (var attachment in message.Attachments)
                {
                    var rawFileName = attachment.ContentDisposition?.FileName
                                   ?? attachment.ContentType.Name
                                   ?? $"piece-jointe-{Guid.NewGuid():N}";

                    // Le nom de la pièce jointe est fourni par l'expéditeur de l'email (non fiable) : on ne
                    // garde que le segment nom-de-fichier pour empêcher une traversée de chemin (ex. un nom
                    // "..\..\Windows\System32\evil.dll" ou un chemin absolu écrivant hors du dossier prévu).
                    var fileName = Path.GetFileName(rawFileName);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        fileName = $"piece-jointe-{Guid.NewGuid():N}";
                    }

                    var ext = Path.GetExtension(fileName);

                    if (extensions.Count > 0 && !extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                        continue;

                    // Avant ce correctif, deux pièces jointes de même nom reçues dans le même passage (ex. deux
                    // emails différents avec "facture.pdf") s'écrasaient silencieusement — seule la dernière
                    // survivait, sans avertissement. On suffixe désormais le nom avec " (2)", " (3)"... si le
                    // fichier de destination existe déjà.
                    var destPath = GetUniqueDestPath(config.DownloadPath, fileName);
                    await using (var stream = File.Create(destPath))
                    {
                        if (attachment is MimePart { Content: not null } part)
                        {
                            await part.Content.DecodeToAsync(stream, context.CancellationToken);
                        }
                        else if (attachment is MessagePart { Message: not null } msgPart)
                        {
                            await msgPart.Message.WriteToAsync(stream, context.CancellationToken);
                        }
                    }

                    downloaded.Add(destPath);
                    matchedAny = true;
                }

                if (matchedAny)
                {
                    processedCount++;
                    if (config.DeleteAfterDownload)
                    {
                        await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, context.CancellationToken);
                    }
                    else if (config.MarkAsReadAfterDownload)
                    {
                        await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, context.CancellationToken);
                    }
                }
            }

            if (config.DeleteAfterDownload && processedCount > 0)
            {
                await folder.ExpungeAsync(context.CancellationToken);
            }

            await client.DisconnectAsync(true, context.CancellationToken);

            return downloaded.Count > 0
                ? StepExecutionResult.Ok(
                    $"{downloaded.Count} pièce(s) jointe(s) téléchargée(s) depuis {processedCount} email(s) correspondant aux filtres.",
                    filesProcessedCsv: string.Join(';', downloaded))
                : StepExecutionResult.Ok("Aucun email correspondant aux filtres trouvé dans la boîte.");
        }
        catch (Exception ex)
        {
            return StepExecutionResult.Fail($"Échec de la relève IMAP sur {cred.Host} : {ex.Message}");
        }
    }

    private static string GetUniqueDestPath(string downloadPath, string fileName)
    {
        var destPath = Path.Combine(downloadPath, fileName);
        if (!File.Exists(destPath)) return destPath;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(downloadPath, $"{baseName} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
