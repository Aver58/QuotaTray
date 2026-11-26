namespace CodexQuotaCore;

internal sealed class RecentTokenUsage(DateTimeOffset now)
{
    private const int SlotCount = 96;
    private const long SlotSeconds = 15 * 60;
    private readonly long _first = SlotStart(now.ToUnixTimeSeconds()) - (SlotCount - 1) * SlotSeconds;
    private readonly long _lastSecond = now.ToUnixTimeSeconds();
    private readonly long[] _tokens = new long[SlotCount];

    internal static long SlotStart(long seconds)
    {
        var remainder = seconds % SlotSeconds;
        return seconds - (remainder < 0 ? remainder + SlotSeconds : remainder);
    }

    internal void Add(long seconds, long tokens)
    {
        if (tokens <= 0 || seconds < _first || seconds > _lastSecond)
        {
            return;
        }

        var index = (int)((seconds - _first) / SlotSeconds);
        _tokens[index] = checked(_tokens[index] + tokens);
    }

    internal IReadOnlyList<TimedTokenUsage> GetIntervals()
    {
        var intervals = new TimedTokenUsage[SlotCount];
        for (var index = 0; index < intervals.Length; index++)
        {
            var start = DateTimeOffset.FromUnixTimeSeconds(_first + index * SlotSeconds);
            var end = index == SlotCount - 1 ? now : start.AddSeconds(SlotSeconds);
            intervals[index] = new TimedTokenUsage(start, end, _tokens[index]);
        }
        return intervals;
    }
}
