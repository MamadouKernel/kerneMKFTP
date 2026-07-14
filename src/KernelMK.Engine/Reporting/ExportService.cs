using System.Globalization;
using System.Text;
using System.Text.Json;
using KernelMK.Core.Entities;

namespace KernelMK.Engine.Reporting;

/// <summary>Export des historiques d'exécution en CSV ou JSON (section 4.7 "Export").</summary>
public static class ExportService
{
    public static string ToCsv(IEnumerable<JobExecution> executions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("JobId;JobName;Statut;DebutUtc;FinUtc;DureeSecondes;DeclencheurPar;CodeRetour;Message");

        foreach (var e in executions)
        {
            var duration = e.Duration?.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) ?? "";
            sb.AppendLine(string.Join(';', new[]
            {
                e.JobId.ToString(),
                Escape(e.Job?.Name),
                e.Status.ToString(),
                e.StartedAt.ToString("O"),
                e.FinishedAt?.ToString("O") ?? "",
                duration,
                Escape(e.TriggeredBy),
                e.ReturnCode?.ToString() ?? "",
                Escape(e.Message)
            }));
        }

        return sb.ToString();
    }

    public static string ToJson(IEnumerable<JobExecution> executions)
    {
        var payload = executions.Select(e => new
        {
            e.Id,
            e.JobId,
            JobName = e.Job?.Name,
            Status = e.Status.ToString(),
            e.StartedAt,
            e.FinishedAt,
            DurationSeconds = e.Duration?.TotalSeconds,
            e.TriggeredBy,
            e.ReturnCode,
            e.Message,
            Steps = e.StepLogs.Select(l => new
            {
                l.StepName,
                l.Order,
                Status = l.Status.ToString(),
                l.StartedAt,
                l.FinishedAt,
                l.ReturnCode,
                l.Output,
                l.ErrorOutput
            })
        });

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Escape(string? value) => string.IsNullOrEmpty(value) ? "" : value.Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');
}
