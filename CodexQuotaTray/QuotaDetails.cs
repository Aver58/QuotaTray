using CodexQuotaCore;

namespace CodexQuotaTray;

internal readonly record struct TokenChartBar(string Period, string? Tick, long Tokens);

internal sealed record QuotaDetailRow(string Name, string Used, string Remaining, string Reset, double? Fraction, string? Note)
{
    internal static QuotaDetailRow For(string name, QuotaBucket bucket)
    {
        var available = bucket.IsAvailable && double.IsFinite(bucket.RemainingPercent) && double.IsFinite(bucket.UsedPercent);
        var fraction = available ? Math.Clamp(bucket.RemainingPercent / 100, 0, 1) : (double?)null;
        var note = bucket.TotalAmount is { } total && bucket.UsedAmount is { } used && bucket.Unit is { } unit
            ? $"剩余 {Math.Max(0, total - used):0.##} / {total:0.##} {unit}" : null;
        return new(name, available ? $"{Math.Clamp(bucket.UsedPercent, 0, 100):0.#}%" : "—",
            fraction is { } remaining ? $"{remaining * 100:0.#}%" : "—",
            bucket.ResetsAt is { } reset ? $"{reset.LocalDateTime:MM/dd HH:mm}" : "—", fraction, note);
    }
}

internal static class QuotaDetails
{
    internal static IReadOnlyList<QuotaDetailRow> Rows(CodexQuotaSnapshot snapshot, QuotaSource source)
    {
        if (source == QuotaSource.Antigravity)
        {
            return
            [
                QuotaDetailRow.For("Gemini · 5 小时", snapshot.Primary),
                QuotaDetailRow.For("Gemini · 周", snapshot.Secondary),
                QuotaDetailRow.For("Claude + GPT · 5 小时", snapshot.Daily),
                QuotaDetailRow.For("Claude + GPT · 周", snapshot.Monthly)
            ];
        }

        var rows = new List<QuotaDetailRow>(4);
        if (source == QuotaSource.Codex && snapshot.Primary.ResetsAt is null && snapshot.AvailableResetCredits is int cards)
            rows.Add(new("重置卡", "—", $"{cards} 张", "—", null, null));
        else if (snapshot.Primary.IsAvailable)
            rows.Add(QuotaDetailRow.For("5 小时额度", snapshot.Primary));
        if (snapshot.Secondary.IsAvailable || rows.Count == 0)
            rows.Add(QuotaDetailRow.For("周额度", snapshot.Secondary));
        if (source == QuotaSource.VolcengineAgentPlan)
        {
            if (snapshot.Daily.IsAvailable) rows.Add(QuotaDetailRow.For("日额度", snapshot.Daily));
            if (snapshot.Monthly.IsAvailable) rows.Add(QuotaDetailRow.For("月额度", snapshot.Monthly));
        }
        return rows;
    }

    internal static TokenChartBar[] Chart(CodexQuotaSnapshot snapshot, bool week, bool volcano)
    {
        if (week)
        {
            return snapshot.LastSevenDays.Select(day =>
                new TokenChartBar($"{day.Date:yyyy/MM/dd}", $"{day.Date:MM/dd}", day.TotalTokens)).ToArray();
        }
        var tickInterval = volcano ? 6 : 24;
        return snapshot.Last24Hours.Select((slot, index) =>
        {
            var start = volcano ? slot.Start : slot.Start.ToLocalTime();
            var end = volcano ? slot.End : slot.End.ToLocalTime();
            return new TokenChartBar(FormatPeriod(start, end), index % tickInterval == 0 ? $"{start:HH:mm}" : null, slot.TotalTokens);
        }).ToArray();
    }

    internal static string ChartSummary(CodexQuotaSnapshot snapshot, TokenChartBar[] points, bool week, bool volcano)
    {
        if (points.Length == 0)
            return volcano ? snapshot.TokenHistoryNotice ?? "正在读取火山引擎用量明细…" : "本地会话记录";

        var interval = week ? "每日一柱" : volcano ? "每小时一柱" : "每 15 分钟一柱";
        var timeZone = volcano ? " · 北京时间" : "";
        var total = points.Sum(point => (decimal)point.Tokens);
        var display = total switch
        {
            >= 1_000_000 => $"{total / 1_000_000:0.##}M",
            >= 1_000 => $"{total / 1_000:0.#}K",
            _ => $"{total:0}"
        };
        return $"近 {(week ? "7 天" : "24 小时")}合计 {display} Token · {interval}{timeZone}";
    }

    private static string FormatPeriod(DateTimeOffset start, DateTimeOffset end)
    {
        if (start.Offset != end.Offset)
            return $"{start:MM/dd HH:mm zzz} – {end:MM/dd HH:mm zzz}";
        return start.Date == end.Date
            ? $"{start:MM/dd HH:mm} – {end:HH:mm}"
            : $"{start:MM/dd HH:mm} – {end:MM/dd HH:mm}";
    }
}
