namespace Supporter.Core;

public static class SupportDuration
{
    public static string Format(DateTimeOffset? startedAt, DateTimeOffset now)
    {
        if (startedAt is null || startedAt > now) return "Apoiante";
        var start = startedAt.Value.UtcDateTime.Date;
        var today = now.UtcDateTime.Date;
        var days = (today - start).Days;
        if (days < 7) return "Novo apoiante";

        var months = (today.Year - start.Year) * 12 + today.Month - start.Month;
        if (start.AddMonths(months) > today) months--;
        if (months == 0) return $"Apoiando há {days} dias";
        var remainingDays = (today - start.AddMonths(months)).Days;
        var years = months / 12;
        var remainingMonths = months % 12;
        if (years > 0)
            return "Apoiando há " + Unit(years, "ano", "anos") +
                (remainingMonths > 0 ? " e " + Unit(remainingMonths, "mês", "meses") : "");
        return "Apoiando há " + Unit(months, "mês", "meses") +
            (remainingDays > 0 ? " e " + Unit(remainingDays, "dia", "dias") : "");
    }

    private static string Unit(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";
}
