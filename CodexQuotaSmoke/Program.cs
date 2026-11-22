using CodexQuotaCore;

var snapshot = new CodexQuotaReader().ReadSnapshot();

Console.WriteLine($"latest={snapshot.LatestEventAt:O}");
Console.WriteLine($"primary_5h={FormatBucket(snapshot.Primary)}");
Console.WriteLine($"secondary_week={FormatBucket(snapshot.Secondary)}");
Console.WriteLine($"available_resets={snapshot.AvailableResetCredits?.ToString() ?? "not provided"}");
Console.WriteLine($"today_tokens={snapshot.TodayTokens}");
Console.WriteLine($"source={snapshot.SourcePath}");
foreach (var day in snapshot.LastSevenDays)
{
    Console.WriteLine($"{day.Date:MM-dd} {day.TotalTokens}");
}

static string FormatBucket(QuotaBucket bucket) =>
    bucket.IsAvailable
        ? $"{bucket.RemainingPercent:0.#}% window={bucket.WindowMinutes}m reset={bucket.ResetsAt:O}"
        : "not provided";
