using CodexQuotaCore;

namespace CodexQuotaTray;

internal sealed record QuotaSummary(string Value, double? Fraction, string Details)
{
    internal static QuotaSummary Unavailable(string details) => new("—", null, details);

    internal static QuotaSummary FromSnapshot(QuotaSource source, CodexQuotaSnapshot snapshot)
    {
        var (bucket, label) = source == QuotaSource.Antigravity
            ? GetAntigravityReading(snapshot)
            : GetStandardReading(source, snapshot);
        var hasCards = source == QuotaSource.Codex && snapshot.AvailableResetCredits.HasValue;
        var balance = snapshot.VolcanoBalance is { } account
            ? $"可用余额 {account.AvailableBalance:N2} {account.Currency}"
            : null;

        if (!bucket.IsAvailable || !double.IsFinite(bucket.RemainingPercent))
        {
            if (hasCards)
            {
                var count = snapshot.AvailableResetCredits.GetValueOrDefault();
                return new($"{count} 张", null, $"剩余 {count} 张重置卡");
            }

            return Unavailable(balance is null ? snapshot.Status : $"{snapshot.Status}\n{balance}");
        }

        var remaining = Math.Clamp(bucket.RemainingPercent, 0, 100);
        var value = $"{remaining:0}%";
        var details = $"{label}剩余 {value}";
        if (bucket.ResetsAt is { } reset)
        {
            details += $"\n重置 {reset.LocalDateTime:MM/dd HH:mm}";
        }
        if (hasCards && snapshot.Primary.ResetsAt is null)
        {
            details += $"\n重置卡 {snapshot.AvailableResetCredits} 张";
        }
        if (balance is not null)
        {
            details += $"\n{balance}";
        }

        return new(value, remaining / 100, details);
    }

    private static (QuotaBucket Bucket, string Label) GetStandardReading(
        QuotaSource source, CodexQuotaSnapshot snapshot)
    {
        var showCards = source == QuotaSource.Codex && snapshot.AvailableResetCredits.HasValue &&
                        snapshot.Primary.ResetsAt is null;
        if (snapshot.Primary.IsAvailable && !showCards)
        {
            return (snapshot.Primary, "5 小时额度");
        }
        if (snapshot.Secondary.IsAvailable || source == QuotaSource.Codex)
        {
            return (snapshot.Secondary, "周额度");
        }
        return snapshot.Daily.IsAvailable
            ? (snapshot.Daily, "日额度")
            : (snapshot.Monthly, "月额度");
    }

    private static (QuotaBucket Bucket, string Label) GetAntigravityReading(CodexQuotaSnapshot snapshot)
    {
        // The two pools are independent. Show the lower remaining percentage within the same window.
        (QuotaBucket Bucket, string Label)[] buckets =
        [
            (snapshot.Primary, "Gemini 5 小时额度"),
            (snapshot.Daily, "Claude + GPT 5 小时额度"),
            (snapshot.Secondary, "Gemini 周额度"),
            (snapshot.Monthly, "Claude + GPT 周额度")
        ];
        return buckets.Where(reading => reading.Bucket.IsAvailable)
            .OrderBy(reading => reading.Bucket.WindowMinutes)
            .ThenBy(reading => reading.Bucket.RemainingPercent)
            .FirstOrDefault(buckets[0]);
    }
}
