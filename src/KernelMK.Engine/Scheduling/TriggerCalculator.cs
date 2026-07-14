using KernelMK.Core;
using KernelMK.Core.Entities;
using Cronos;

namespace KernelMK.Engine.Scheduling;

/// <summary>Calcule la prochaine date d'exécution d'un déclencheur (section 4.3 "Planification et déclencheurs").</summary>
public static class TriggerCalculator
{
    public static DateTime? ComputeNextRunAt(JobTrigger trigger, DateTime fromUtc)
    {
        return trigger.Type switch
        {
            TriggerType.Horaire or TriggerType.Cron => ComputeHoraire(trigger, fromUtc),
            TriggerType.Calendrier => ComputeCalendrier(trigger, fromUtc),
            _ => null // Evénement dossier, dépendance, démarrage, API et manuel ne sont pas planifiés dans le temps
        };
    }

    private static DateTime? ComputeHoraire(JobTrigger trigger, DateTime fromUtc)
    {
        if (!string.IsNullOrWhiteSpace(trigger.CronExpression))
        {
            var expression = CronExpression.Parse(trigger.CronExpression, CronFormat.IncludeSeconds);
            return expression.GetNextOccurrence(fromUtc, TimeZoneInfo.Local);
        }

        if (trigger.IntervalSeconds is > 0)
        {
            var baseTime = trigger.LastFiredAt ?? fromUtc;
            var next = baseTime.AddSeconds(trigger.IntervalSeconds.Value);
            return next <= fromUtc ? fromUtc.AddSeconds(trigger.IntervalSeconds.Value) : next;
        }

        return null;
    }

    private static DateTime? ComputeCalendrier(JobTrigger trigger, DateTime fromUtc)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, TimeZoneInfo.Local);
        var timeOfDay = trigger.WindowStart ?? TimeSpan.Zero;
        var allowedDays = ParseDaysOfWeek(trigger.DaysOfWeekCsv);
        var holidays = ParseHolidayDates(trigger.HolidayDatesCsv);

        for (var dayOffset = 0; dayOffset < 14; dayOffset++)
        {
            var candidateDate = localNow.Date.AddDays(dayOffset);
            var candidate = candidateDate.Add(timeOfDay);
            if (candidate <= localNow) continue;

            if (trigger.ExcludeWeekends && (candidateDate.DayOfWeek == DayOfWeek.Saturday || candidateDate.DayOfWeek == DayOfWeek.Sunday))
                continue;

            if (allowedDays.Count > 0 && !allowedDays.Contains(candidateDate.DayOfWeek))
                continue;

            if (trigger.ExcludeHolidays && holidays.Contains(DateOnly.FromDateTime(candidateDate)))
                continue;

            return TimeZoneInfo.ConvertTimeToUtc(candidate, TimeZoneInfo.Local);
        }

        return null;
    }

    private static HashSet<DayOfWeek> ParseDaysOfWeek(string? csv)
    {
        var result = new HashSet<DayOfWeek>();
        if (string.IsNullOrWhiteSpace(csv)) return result;

        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<DayOfWeek>(part, true, out var day))
            {
                result.Add(day);
            }
        }
        return result;
    }

    private static HashSet<DateOnly> ParseHolidayDates(string? csv)
    {
        var result = new HashSet<DateOnly>();
        if (string.IsNullOrWhiteSpace(csv)) return result;

        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateOnly.TryParse(part, out var date))
            {
                result.Add(date);
            }
        }
        return result;
    }
}
