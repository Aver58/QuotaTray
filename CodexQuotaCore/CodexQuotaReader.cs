using System.Globalization;
using System.Text.Json;

namespace CodexQuotaCore;

public sealed class CodexQuotaReader
{
    private readonly string _codexHome;
    private readonly TimeZoneInfo _localTimeZone;
    private readonly LiveUsageReader _liveUsageReader;

    public CodexQuotaReader(string? codexHome = null, TimeZoneInfo? localTimeZone = null)
    {
        _codexHome = ResolveCodexHome(codexHome);
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        _liveUsageReader = new LiveUsageReader(_codexHome);
    }

    public CodexQuotaSnapshot ReadSnapshot(DateTimeOffset? now = null, bool includeLiveQuota = true)
    {
        var localNow = TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow, _localTimeZone);
        var dayTotals = SeedLastSevenDays(DateOnly.FromDateTime(localNow.DateTime));
        var recent = new RecentTokenUsage(localNow);
        var latest = default(TokenCountEvent);
        var latestFiveHour = default(QuotaSample);
        var latestWeek = default(QuotaSample);
        var scannedFiles = 0;

        foreach (var file in EnumerateRecentSessionFiles(localNow))
        {
            scannedFiles++;
            foreach (var tokenEvent in ReadTokenEvents(file))
            {
                var localEventTime = TimeZoneInfo.ConvertTime(tokenEvent.Timestamp, _localTimeZone);
                var eventDate = DateOnly.FromDateTime(localEventTime.DateTime);
                if (dayTotals.ContainsKey(eventDate))
                {
                    dayTotals[eventDate] += tokenEvent.LastTotalTokens;
                }
                recent.Add(tokenEvent.Timestamp.ToUnixTimeSeconds(), tokenEvent.LastTotalTokens);

                if (latest is null || tokenEvent.Timestamp > latest.Timestamp)
                {
                    latest = tokenEvent;
                }

                foreach (var bucket in tokenEvent.RateLimitBuckets)
                {
                    if (IsFiveHourWindow(bucket.WindowMinutes) && (latestFiveHour is null || tokenEvent.Timestamp > latestFiveHour.Timestamp))
                    {
                        latestFiveHour = new QuotaSample(tokenEvent.Timestamp, bucket, tokenEvent.SourcePath);
                    }

                    if (IsWeekWindow(bucket.WindowMinutes) && (latestWeek is null || tokenEvent.Timestamp > latestWeek.Timestamp))
                    {
                        latestWeek = new QuotaSample(tokenEvent.Timestamp, bucket, tokenEvent.SourcePath);
                    }
                }
            }
        }

        var orderedDays = dayTotals
            .OrderBy(pair => pair.Key)
            .Select(pair => new DailyTokenUsage(pair.Key, pair.Value))
            .ToArray();
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var liveUsage = includeLiveQuota
            ? _liveUsageReader.Read()
            : LiveUsageResult.Failed("已跳过 Codex 实时额度查询。");

        QuotaBucket primary;
        QuotaBucket secondary;
        int? availableResetCredits;
        DateTimeOffset? latestEventAt;
        string source;
        string status;

        if (liveUsage.IsSuccess)
        {
            primary = FindBucket(liveUsage.Buckets, IsFiveHourWindow) ?? QuotaBucket.Unavailable(300);
            secondary = FindBucket(liveUsage.Buckets, IsWeekWindow) ?? QuotaBucket.Unavailable(10080);
            availableResetCredits = liveUsage.AvailableResetCredits;
            latestEventAt = liveUsage.RetrievedAt;
            source = liveUsage.Source;
            status = BuildLiveStatus(liveUsage.Status, primary, secondary, scannedFiles);
        }
        else
        {
            primary = latestFiveHour?.Bucket ?? QuotaBucket.Unavailable(300);
            secondary = latestWeek?.Bucket ?? QuotaBucket.Unavailable(10080);
            availableResetCredits = null;
            latestEventAt = latest?.Timestamp;
            source = latest?.SourcePath ?? Path.Combine(_codexHome, "sessions");
            status = BuildFallbackStatus(liveUsage.Status, latest, scannedFiles);
        }

        return new CodexQuotaSnapshot(
            latestEventAt,
            primary,
            secondary,
            QuotaBucket.Unavailable(1440),
            QuotaBucket.Unavailable(43200),
            availableResetCredits,
            dayTotals.GetValueOrDefault(today),
            orderedDays,
            source,
            status
        )
        {
            Last24Hours = latest is null ? Array.Empty<TimedTokenUsage>() : recent.GetIntervals()
        };
    }

    private static string ResolveCodexHome(string? codexHome)
    {
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            return Environment.ExpandEnvironmentVariables(codexHome);
        }

        var envHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(envHome))
        {
            return Environment.ExpandEnvironmentVariables(envHome);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".codex");
    }

    private Dictionary<DateOnly, long> SeedLastSevenDays(DateOnly today)
    {
        var result = new Dictionary<DateOnly, long>();
        for (var offset = 6; offset >= 0; offset--)
        {
            result[today.AddDays(-offset)] = 0;
        }

        return result;
    }

    private IEnumerable<string> EnumerateRecentSessionFiles(DateTimeOffset localNow)
    {
        var sessionsRoot = Path.Combine(_codexHome, "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            yield break;
        }

        var today = DateOnly.FromDateTime(localNow.DateTime);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 7; offset >= 0; offset--)
        {
            var day = today.AddDays(-offset);
            var path = Path.Combine(
                sessionsRoot,
                day.Year.ToString("0000", CultureInfo.InvariantCulture),
                day.Month.ToString("00", CultureInfo.InvariantCulture),
                day.Day.ToString("00", CultureInfo.InvariantCulture)
            );

            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                if (seen.Add(file))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<TokenCountEvent> ReadTokenEvents(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            TokenCountEvent? tokenEvent;
            try
            {
                tokenEvent = ParseTokenEvent(line, path);
            }
            catch (JsonException)
            {
                tokenEvent = null;
            }
            catch (InvalidOperationException)
            {
                tokenEvent = null;
            }

            if (tokenEvent is not null)
            {
                yield return tokenEvent;
            }
        }
    }

    private static TokenCountEvent? ParseTokenEvent(string jsonLine, string sourcePath)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        var root = doc.RootElement;

        if (!root.TryGetProperty("timestamp", out var timestampElement) ||
            !DateTimeOffset.TryParse(timestampElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            return null;
        }

        if (!root.TryGetProperty("type", out var typeElement) || typeElement.GetString() != "event_msg")
        {
            return null;
        }

        if (!root.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("type", out var payloadType) ||
            payloadType.GetString() != "token_count")
        {
            return null;
        }

        var lastTokens = 0L;
        if (payload.TryGetProperty("info", out var info) &&
            info.TryGetProperty("last_token_usage", out var lastUsage) &&
            lastUsage.TryGetProperty("total_tokens", out var lastTotal))
        {
            lastTokens = ReadInt64(lastTotal);
        }

        var buckets = ReadBuckets(payload);

        return new TokenCountEvent(timestamp, lastTokens, buckets, sourcePath);
    }

    private static IReadOnlyList<QuotaBucket> ReadBuckets(JsonElement payload)
    {
        if (!payload.TryGetProperty("rate_limits", out var rateLimits) || rateLimits.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<QuotaBucket>();
        }

        var buckets = new List<QuotaBucket>();
        AddBucket(rateLimits, "primary", 300, buckets);
        AddBucket(rateLimits, "secondary", 10080, buckets);
        return buckets;
    }

    private static void AddBucket(JsonElement rateLimits, string name, int fallbackWindowMinutes, ICollection<QuotaBucket> buckets)
    {
        if (!rateLimits.TryGetProperty(name, out var bucket) || bucket.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var usedPercent = bucket.TryGetProperty("used_percent", out var usedElement)
            ? Math.Clamp(ReadDouble(usedElement), 0, 100)
            : 0;
        var windowMinutes = bucket.TryGetProperty("window_minutes", out var windowElement)
            ? (int)ReadInt64(windowElement)
            : fallbackWindowMinutes;
        var resetsAt = bucket.TryGetProperty("resets_at", out var resetElement)
            ? ReadUnixSeconds(resetElement)
            : null;

        buckets.Add(new QuotaBucket(usedPercent, 100 - usedPercent, windowMinutes, resetsAt));
    }

    private static QuotaBucket? FindBucket(IEnumerable<QuotaBucket> buckets, Func<int, bool> matches) =>
        buckets.FirstOrDefault(bucket => matches(bucket.WindowMinutes));

    private static bool IsFiveHourWindow(int minutes) => Math.Abs(minutes - 300) <= 2;

    private static bool IsWeekWindow(int minutes) => Math.Abs(minutes - 10080) <= 5;

    private static string BuildLiveStatus(string liveStatus, QuotaBucket primary, QuotaBucket secondary, int scannedFiles)
    {
        var missing = new List<string>();
        if (!primary.IsAvailable)
        {
            missing.Add("5 小时");
        }

        if (!secondary.IsAvailable)
        {
            missing.Add("周");
        }

        var availability = missing.Count == 0
            ? ""
            : $" Codex 未提供{string.Join("和", missing)}额度。";
        return $"{liveStatus}{availability} 已从 {scannedFiles} 个会话文件统计用量。";
    }

    private static string BuildFallbackStatus(string liveStatus, TokenCountEvent? latest, int scannedFiles)
    {
        if (latest is null)
        {
            return $"{liveStatus} 最近 {scannedFiles} 个会话文件中没有用量记录。";
        }

        return $"{liveStatus} 当前显示 {latest.Timestamp.LocalDateTime:MM/dd HH:mm} 的本地额度记录。";
    }

    private static long ReadInt64(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var value) => value,
            JsonValueKind.Number => (long)element.GetDouble(),
            JsonValueKind.String when long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0
        };

    private static double ReadDouble(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0
        };

    private static DateTimeOffset? ReadUnixSeconds(JsonElement element)
    {
        var seconds = ReadInt64(element);
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    private sealed record TokenCountEvent(
        DateTimeOffset Timestamp,
        long LastTotalTokens,
        IReadOnlyList<QuotaBucket> RateLimitBuckets,
        string SourcePath
    );

    private sealed record QuotaSample(DateTimeOffset Timestamp, QuotaBucket Bucket, string SourcePath);
}
