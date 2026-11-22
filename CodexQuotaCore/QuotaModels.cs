namespace CodexQuotaCore;

public sealed record QuotaBucket(
    double UsedPercent,
    double RemainingPercent,
    int WindowMinutes,
    DateTimeOffset? ResetsAt,
    bool IsAvailable = true,
    double? UsedAmount = null,
    double? TotalAmount = null,
    string? Unit = null
)
{
    public static QuotaBucket Unavailable(int windowMinutes) =>
        new(0, 0, windowMinutes, null, false);
}

public sealed record DailyTokenUsage(DateOnly Date, long TotalTokens);

public sealed record CodexQuotaSnapshot(
    DateTimeOffset? LatestEventAt,
    QuotaBucket Primary,
    QuotaBucket Secondary,
    QuotaBucket Daily,
    QuotaBucket Monthly,
    int? AvailableResetCredits,
    long TodayTokens,
    IReadOnlyList<DailyTokenUsage> LastSevenDays,
    string SourcePath,
    string Status,
    VolcanoAccountBalance? VolcanoBalance = null,
    string? VolcanoBalanceError = null,
    string? TokenHistoryNotice = null
)
{
    public static CodexQuotaSnapshot Empty(string status, string sourcePath = "") =>
        new(
            null,
            QuotaBucket.Unavailable(300),
            QuotaBucket.Unavailable(10080),
            QuotaBucket.Unavailable(1440),
            QuotaBucket.Unavailable(43200),
            null,
            0,
            Array.Empty<DailyTokenUsage>(),
            sourcePath,
            status
        );
}

public sealed record VolcanoAccountBalance(decimal AvailableBalance, decimal CashBalance, string Currency = "CNY");
