namespace CodexQuotaCore;

internal static class AntigravityGeneration
{
    internal static (long? Seconds, int? FirstStep, long? Tokens) Read(ReadOnlySpan<byte> bytes)
    {
        long? seconds = null;
        int? firstStep = null;
        var counters = new TokenCounters();
        var reader = new WireReader(bytes);
        while (reader.Next(out var field, out var wire, out var value, out var segment))
        {
            if (field == 1 && wire == 2)
                ReadModel(segment, ref seconds, ref counters);
            if (field != 2 || firstStep.HasValue) continue;
            if (wire == 0) firstStep = unchecked((int)value);
            if (wire == 2 && !segment.IsEmpty)
            {
                var indices = new WireReader(segment);
                firstStep = unchecked((int)indices.Varint());
            }
        }
        // Output includes reasoning. Cached reads and writes are separate counters.
        return (seconds, firstStep, counters.HasUsage
            ? checked((long)(counters.Input + counters.Output + counters.CacheWrite + counters.CacheRead)) : null);
    }

    internal static long? StepSeconds(ReadOnlySpan<byte> bytes) => TimestampField(bytes, 1);

    private static void ReadModel(ReadOnlySpan<byte> bytes, ref long? seconds, ref TokenCounters counters)
    {
        var model = new WireReader(bytes);
        while (model.Next(out var field, out var wire, out _, out var segment))
        {
            if (wire != 2) continue;
            if (field == 4)
            {
                counters.HasUsage = true;
                var usage = new WireReader(segment);
                while (usage.Next(out var counter, out var counterWire, out var value, out _))
                {
                    if (counterWire != 0) continue;
                    switch (counter)
                    {
                        case 2: counters.Input = value; break;
                        case 3: counters.Output = value; break;
                        case 4: counters.CacheWrite = value; break;
                        case 5: counters.CacheRead = value; break;
                    }
                }
            }
            if (field == 9 && TimestampField(segment, 4) is long timestamp) seconds = timestamp;
        }
    }

    private static long? TimestampField(ReadOnlySpan<byte> bytes, int number)
    {
        long? seconds = null;
        var reader = new WireReader(bytes);
        while (reader.Next(out var field, out var wire, out _, out var segment))
        {
            if (field != number || wire != 2) continue;
            seconds ??= 0;
            var stamp = new WireReader(segment);
            while (stamp.Next(out var stampField, out var stampWire, out var value, out _))
                if (stampField == 1 && stampWire == 0) seconds = unchecked((long)value);
        }
        return seconds;
    }

    private struct TokenCounters
    {
        internal bool HasUsage;
        internal ulong Input, Output, CacheWrite, CacheRead;
    }

    private ref struct WireReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;

        internal bool Next(out int field, out int wire, out ulong value, out ReadOnlySpan<byte> segment)
        {
            field = wire = 0;
            value = 0;
            segment = default;
            if (_data.IsEmpty) return false;
            var tag = Varint();
            if (tag is < 8 or > uint.MaxValue) throw new InvalidDataException("无效的会话字段编号。");
            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: value = Varint(); break;
                case 1: Take(8); break;
                case 2:
                    var length = Varint();
                    if (length > (ulong)_data.Length) throw new InvalidDataException("会话字段长度无效。");
                    segment = Take((int)length);
                    break;
                case 5: Take(4); break;
                default: throw new InvalidDataException("未知的会话字段格式。");
            }
            return true;
        }

        internal ulong Varint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64 && !_data.IsEmpty; shift += 7)
            {
                var next = _data[0];
                _data = _data[1..];
                if (shift == 63 && next > 1) break;
                result |= (ulong)(next & 0x7f) << shift;
                if ((next & 0x80) == 0) return result;
            }
            throw new InvalidDataException("会话数字字段无效。");
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > _data.Length) throw new InvalidDataException("会话字段不完整。");
            var part = _data[..count];
            _data = _data[count..];
            return part;
        }
    }
}
