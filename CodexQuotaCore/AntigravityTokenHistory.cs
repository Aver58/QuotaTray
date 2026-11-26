namespace CodexQuotaCore;

public sealed class AntigravityTokenHistory
{
    private readonly Dictionary<string, FileSummary> _cache = new(StringComparer.OrdinalIgnoreCase);
    private sealed record FileSummary(
        (long, long, long, long) Stamp, Dictionary<DateOnly, long> Days,
        Dictionary<long, long> Intervals, bool Partial);

    public CodexQuotaSnapshot AddTo(CodexQuotaSnapshot snapshot)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini", "antigravity", "conversations");
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var days = Enumerable.Range(-6, 7).ToDictionary(today.AddDays, _ => 0L);
        var recent = new RecentTokenUsage(now);
        bool partial = false, readable = false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.db"))
            {
                try
                {
                    var main = new FileInfo(file);
                    var wal = new FileInfo(file + "-wal");
                    var stamp = (main.Length, main.LastWriteTimeUtc.Ticks,
                        wal.Exists ? wal.Length : 0L, wal.Exists ? wal.LastWriteTimeUtc.Ticks : 0L);
                    if (!_cache.TryGetValue(file, out var summary) || summary.Stamp != stamp)
                    {
                        summary = ReadSession(file, stamp);
                        _cache[file] = summary;
                    }
                    readable |= !summary.Partial || summary.Days.Count > 0;
                    partial |= summary.Partial;
                    foreach (var (day, tokens) in summary.Days)
                        if (days.ContainsKey(day)) days[day] = checked(days[day] + tokens);
                    foreach (var (seconds, tokens) in summary.Intervals)
                        recent.Add(seconds, tokens);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                           or OverflowException or DllNotFoundException or EntryPointNotFoundException)
                {
                    partial = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            partial = true;
        }

        if (!readable)
            return snapshot with { TokenHistoryNotice = "暂无可读取的本地 Token 记录。" };

        return snapshot with
        {
            LastSevenDays = days.OrderBy(pair => pair.Key).Select(pair => new DailyTokenUsage(pair.Key, pair.Value)).ToArray(),
            Last24Hours = recent.GetIntervals(),
            TodayTokens = days[today],
            TokenHistoryNotice = partial ? "仅显示可读取的本地记录。" : null
        };
    }

    private static FileSummary ReadSession(string file, (long, long, long, long) stamp)
    {
        using var db = new WindowsSqlite(file);
        using var step = db.Prepare("SELECT metadata FROM steps WHERE idx = ?");
        using var rows = db.Prepare("SELECT data FROM gen_metadata");
        var days = new Dictionary<DateOnly, long>();
        var intervals = new Dictionary<long, long>();
        var partial = false;
        while (rows.Read())
        {
            try
            {
                var generation = AntigravityGeneration.Read(rows.Blob());
                if (generation.Tokens is not long total) { partial = true; continue; }
                var seconds = generation.Seconds;
                if (seconds is null && generation.FirstStep is int index)
                {
                    step.BindIndex(index);
                    if (step.Read()) seconds = AntigravityGeneration.StepSeconds(step.Blob());
                }
                if (seconds is null or <= 0) { partial = true; continue; }
                var day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(seconds.Value).LocalDateTime);
                days[day] = checked(days.GetValueOrDefault(day) + total);
                var start = RecentTokenUsage.SlotStart(seconds.Value);
                intervals[start] = checked(intervals.GetValueOrDefault(start) + total);
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
            {
                partial = true;
            }
        }
        return new FileSummary(stamp, days, intervals, partial);
    }

}
