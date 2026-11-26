using CodexQuotaCore;

namespace CodexQuotaTray;

internal static class QuotaText
{
    internal static string Tooltip(CodexQuotaSnapshot snapshot, ResetTimeWindow resetTimeWindow, QuotaSource source)
    {
        var sourceName = source switch
        {
            QuotaSource.VolcengineAgentPlan => "火山引擎",
            QuotaSource.Antigravity => "Antigravity",
            _ => "Codex"
        };
        var hasFiveHourReset = snapshot.Primary.IsAvailable && snapshot.Primary.ResetsAt is not null;
        var primary = hasFiveHourReset
            ? $"5小时 {snapshot.Primary.RemainingPercent:0}%" + (resetTimeWindow == ResetTimeWindow.FiveHour
                ? $" 重置 {snapshot.Primary.ResetsAt!.Value.LocalDateTime:HH:mm}"
                : string.Empty)
            : string.Empty;
        var week = snapshot.Secondary.IsAvailable
            ? $"周 {snapshot.Secondary.RemainingPercent:0}%" + (resetTimeWindow == ResetTimeWindow.Week && snapshot.Secondary.ResetsAt is not null
                ? $" 重置 {snapshot.Secondary.ResetsAt.Value.LocalDateTime:MM/dd HH:mm}"
                : string.Empty)
            : string.Empty;
        var resetCards = !hasFiveHourReset && source == QuotaSource.Codex && snapshot.AvailableResetCredits is int credits
            ? $"重置卡 {credits}"
            : string.Empty;
        var details = string.Join(" ", new[] { primary, week, resetCards }.Where(value => value.Length > 0));
        var today = source switch
        {
            QuotaSource.VolcengineAgentPlan => "",
            QuotaSource.Antigravity when snapshot.LastSevenDays.Count == 0 => " 今日 --",
            _ => $" 今日 {FormatTokens(snapshot.TodayTokens)}"
        };
        var text = (details.Length == 0 ? sourceName : $"{sourceName} {details}") + today;
        return text.Length <= 63 ? text : text[..63];
    }

    private static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000)
        {
            return $"{tokens / 1_000_000.0:0.#}M";
        }

        if (tokens >= 1_000)
        {
            return $"{tokens / 1_000.0:0.#}K";
        }

        return tokens.ToString("0");
    }
}
