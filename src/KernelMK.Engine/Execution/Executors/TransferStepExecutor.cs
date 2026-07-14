using System.Text.Json;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;
using FluentFTP;
using Renci.SshNet;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>Transferts FTP, FTPS, SFTP et copie réseau SMB (section 4.2 "Transfert").</summary>
public class TransferStepExecutor : IStepExecutor
{
    public IReadOnlyCollection<StepType> SupportedTypes { get; } = new[]
    {
        StepType.TransfertFtp, StepType.TransfertFtps, StepType.TransfertSftp, StepType.TransfertSmb
    };

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        var config = JsonSerializer.Deserialize<TransferStepConfig>(context.Step.ConfigJson)
                      ?? throw new InvalidOperationException("Configuration de transfert invalide.");

        var username = context.ResolvedCredential?.Username ?? "anonymous";
        var secret = context.ResolvedCredential?.Secret ?? string.Empty;

        try
        {
            switch (context.Step.Type)
            {
                case StepType.TransfertSftp:
                    return await ExecuteSftpAsync(config, username, secret, context.CancellationToken);

                case StepType.TransfertFtp:
                case StepType.TransfertFtps:
                    return await ExecuteFtpAsync(config, username, secret, context.Step.Type == StepType.TransfertFtps, context.CancellationToken);

                case StepType.TransfertSmb:
                    return ExecuteSmbCopy(config);

                default:
                    throw new NotSupportedException($"Type de transfert non supporté : {context.Step.Type}");
            }
        }
        catch (Exception ex)
        {
            return StepExecutionResult.Fail(ex.Message);
        }
    }

    private static async Task<StepExecutionResult> ExecuteSftpAsync(TransferStepConfig config, string username, string secret, CancellationToken ct)
    {
        using var client = new SftpClient(config.Host, config.Port <= 0 ? 22 : config.Port, username, secret);
        await Task.Run(client.Connect, ct);

        try
        {
            if (config.Upload)
            {
                await using var stream = File.OpenRead(config.LocalPath);
                await Task.Run(() => client.UploadFile(stream, config.RemotePath, true), ct);

                if (!client.Exists(config.RemotePath))
                {
                    return StepExecutionResult.Fail("Le fichier distant n'a pas été trouvé après transfert (vérification échouée).");
                }
            }
            else
            {
                await using var stream = File.Create(config.LocalPath);
                await Task.Run(() => client.DownloadFile(config.RemotePath, stream), ct);
            }

            ArchiveIfRequested(config);
            return StepExecutionResult.Ok($"Transfert SFTP réussi vers/depuis {config.Host}.", filesProcessedCsv: config.LocalPath);
        }
        finally
        {
            client.Disconnect();
        }
    }

    private static async Task<StepExecutionResult> ExecuteFtpAsync(TransferStepConfig config, string username, string secret, bool useTls, CancellationToken ct)
    {
        using var client = new AsyncFtpClient(config.Host, username, secret, config.Port <= 0 ? 21 : config.Port);
        if (useTls || config.UseTls)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
        }

        await client.Connect(ct);

        try
        {
            if (config.Upload)
            {
                var status = await client.UploadFile(config.LocalPath, config.RemotePath, FtpRemoteExists.Overwrite, true, token: ct);
                if (status != FtpStatus.Success)
                {
                    return StepExecutionResult.Fail("Échec du transfert FTP (statut != Success).");
                }
            }
            else
            {
                var status = await client.DownloadFile(config.LocalPath, config.RemotePath, FtpLocalExists.Overwrite, token: ct);
                if (status != FtpStatus.Success)
                {
                    return StepExecutionResult.Fail("Échec de la récupération FTP (statut != Success).");
                }
            }

            ArchiveIfRequested(config);
            return StepExecutionResult.Ok($"Transfert FTP{(useTls ? "S" : "")} réussi vers/depuis {config.Host}.", filesProcessedCsv: config.LocalPath);
        }
        finally
        {
            await client.Disconnect(ct);
        }
    }

    private static StepExecutionResult ExecuteSmbCopy(TransferStepConfig config)
    {
        var destination = Path.Combine(config.SmbShare ?? config.RemotePath, Path.GetFileName(config.LocalPath));
        if (config.Upload)
        {
            File.Copy(config.LocalPath, destination, true);
        }
        else
        {
            File.Copy(destination, config.LocalPath, true);
        }

        ArchiveIfRequested(config);
        return StepExecutionResult.Ok("Copie réseau SMB réussie.", filesProcessedCsv: destination);
    }

    private static void ArchiveIfRequested(TransferStepConfig config)
    {
        if (!config.ArchiveAfterTransfer || string.IsNullOrWhiteSpace(config.ArchiveDirectory) || !config.Upload) return;
        if (!File.Exists(config.LocalPath)) return;

        Directory.CreateDirectory(config.ArchiveDirectory);
        var archivePath = Path.Combine(config.ArchiveDirectory, Path.GetFileName(config.LocalPath));
        File.Copy(config.LocalPath, archivePath, true);
    }
}
