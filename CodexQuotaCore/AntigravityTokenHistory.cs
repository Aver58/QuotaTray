using Microsoft.Data.Sqlite;
using ProtoBuf;

namespace CodexQuotaCore;

public sealed class AntigravityTokenHistory
{
    private readonly Dictionary<string, FileSummary> _cache = new(StringComparer.OrdinalIgnoreCase);
    private sealed record FileSummary((long, long, long, long) Stamp, Dictionary<DateOnly, long> Days, bool Partial);

    public CodexQuotaSnapshot AddTo(CodexQuotaSnapshot snapshot)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini", "antigravity", "conversations");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var days = Enumerable.Range(-6, 7).ToDictionary(today.AddDays, _ => 0L);
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
                }
                catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or OverflowException)
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
            TodayTokens = days[today],
            TokenHistoryNotice = partial ? "仅显示可读取的本地记录。" : null
        };
    }

    private static FileSummary ReadSession(string file, (long, long, long, long) stamp)
    {
        var options = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly,
            Pooling = false, DefaultTimeout = 1 };
        using var db = new SqliteConnection(options.ToString());
        db.Open();
        using var transaction = db.BeginTransaction(deferred: true);
        using var step = db.CreateCommand();
        step.Transaction = transaction;
        step.CommandText = "SELECT metadata FROM steps WHERE idx = $idx";
        var index = step.Parameters.Add("$idx", SqliteType.Integer);
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT data FROM gen_metadata";
        using var rows = query.ExecuteReader();
        var days = new Dictionary<DateOnly, long>();
        var partial = false;
        while (rows.Read())
        {
            try
            {
                var generation = Decode<Generation>((byte[])rows[0]);
                var usage = generation.Model?.Usage;
                if (usage == null) { partial = true; continue; }
                long? seconds = generation.Model?.Timing?.At?.Seconds;
                if (seconds is null && generation.Steps is { Length: > 0 })
                {
                    index.Value = generation.Steps[0];
                    if (step.ExecuteScalar() is byte[] bytes) seconds = Decode<Step>(bytes).Created?.Seconds;
                }
                if (seconds is null or <= 0) { partial = true; continue; }
                var day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(seconds.Value).LocalDateTime);
                // Total output already includes reasoning. Cached reads are separate from uncached input.
                long total = checked((long)(usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite));
                days[day] = checked(days.GetValueOrDefault(day) + total);
            }
            catch (Exception ex) when (ex is ProtoException or EndOfStreamException or OverflowException or ArgumentOutOfRangeException or InvalidCastException)
            {
                partial = true;
            }
        }
        return new FileSummary(stamp, days, partial);
    }

    private static T Decode<T>(byte[] bytes) => Serializer.Deserialize<T>(bytes.AsMemory());

    [ProtoContract] private sealed class Generation
    {
        [ProtoMember(1)] public ChatModel? Model { get; set; }
        [ProtoMember(2, IsPacked = true)] public int[]? Steps { get; set; }
    }
    [ProtoContract] private sealed class ChatModel
    {
        [ProtoMember(4)] public UsageCounters? Usage { get; set; }
        [ProtoMember(9)] public Timing? Timing { get; set; }
    }
    [ProtoContract] private sealed class UsageCounters
    {
        [ProtoMember(2)] public ulong Input { get; set; }
        [ProtoMember(3)] public ulong Output { get; set; }
        [ProtoMember(4)] public ulong CacheWrite { get; set; }
        [ProtoMember(5)] public ulong CacheRead { get; set; }
    }
    [ProtoContract] private sealed class Timing { [ProtoMember(4)] public Timestamp? At { get; set; } }
    [ProtoContract] private sealed class Step { [ProtoMember(1)] public Timestamp? Created { get; set; } }
    [ProtoContract] private sealed class Timestamp { [ProtoMember(1)] public long Seconds { get; set; } }
}
