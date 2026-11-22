using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CodexQuotaCore;

public sealed class AntigravityUsageReader
{
    private const string ServicePath = "/exa.language_server_pb.LanguageServerService/";
    private static readonly HttpClient Client = CreateClient();

    public CodexQuotaSnapshot ReadSnapshot()
    {
        var daemonDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini", "antigravity", "daemon");
        if (!Directory.Exists(daemonDirectory))
        {
            return CodexQuotaSnapshot.Empty("请先启动 Antigravity 桌面端。");
        }

        foreach (var file in Directory.EnumerateFiles(daemonDirectory, "hub_*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                using var hub = JsonDocument.Parse(File.ReadAllText(file));
                var root = hub.RootElement;
                var processId = ReadInt(root, "pid");
                var port = ReadInt(root, "httpsPort");
                var csrfToken = ReadString(root, "csrfToken");
                if (processId <= 0 || port is <= 0 or > 65535 || string.IsNullOrWhiteSpace(csrfToken) || !IsProcessRunning(processId))
                {
                    continue;
                }

                using var response = SendQuotaRequest(port, csrfToken);
                if (TryCreateSnapshot(response, out var snapshot))
                {
                    return snapshot;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or HttpRequestException or InvalidOperationException or TaskCanceledException or ArgumentException)
            {
                // A stale daemon file or a server that is restarting should not block probing other sessions.
            }
        }

        return CodexQuotaSnapshot.Empty("暂时无法读取额度，请确认 Antigravity 已运行并登录。");
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.IsLoopback == true
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static JsonDocument SendQuotaRequest(int port, string csrfToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://127.0.0.1:{port}{ServicePath}RetrieveUserQuotaSummary");
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrfToken);
        request.Content = new StringContent(
            "{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\",\"ideVersion\":\"unknown\"}",
            Encoding.UTF8,
            "application/json");

        using var response = Client.Send(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(response.Content.ReadAsStream());
    }

    private static bool TryCreateSnapshot(JsonDocument document, out CodexQuotaSnapshot snapshot)
    {
        snapshot = CodexQuotaSnapshot.Empty("暂时无法读取 Antigravity 额度。");
        if (!document.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var gemini = FindGroup(groups, "gemini");
        var otherModels = FindGroup(groups, "claude", "3p");
        var geminiFiveHour = FindBucket(gemini, "5h", 300);
        var geminiWeekly = FindBucket(gemini, "week", 10080);
        var otherFiveHour = FindBucket(otherModels, "5h", 300);
        var otherWeekly = FindBucket(otherModels, "week", 10080);
        if (!geminiFiveHour.IsAvailable && !geminiWeekly.IsAvailable &&
            !otherFiveHour.IsAvailable && !otherWeekly.IsAvailable)
        {
            return false;
        }

        snapshot = new CodexQuotaSnapshot(
            DateTimeOffset.UtcNow,
            geminiFiveHour,
            geminiWeekly,
            otherFiveHour,
            otherWeekly,
            null,
            0,
            Array.Empty<DailyTokenUsage>(),
            "Antigravity 本地额度服务",
            "Antigravity 实时额度");
        return true;
    }

    private static JsonElement FindGroup(JsonElement groups, params string[] names)
    {
        foreach (var group in groups.EnumerateArray())
        {
            var displayName = ReadString(group, "displayName") ?? string.Empty;
            if (names.Any(name => displayName.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                return group;
            }
        }

        return default;
    }

    private static QuotaBucket FindBucket(JsonElement group, string cadence, int windowMinutes)
    {
        if (group.ValueKind != JsonValueKind.Object ||
            !group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
        {
            return QuotaBucket.Unavailable(windowMinutes);
        }

        foreach (var bucket in buckets.EnumerateArray())
        {
            var bucketId = ReadString(bucket, "bucketId") ?? string.Empty;
            var window = ReadString(bucket, "window") ?? string.Empty;
            var displayName = ReadString(bucket, "displayName") ?? string.Empty;
            var matches = cadence == "week"
                ? window.Contains("week", StringComparison.OrdinalIgnoreCase) || bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase) || displayName.Contains("weekly", StringComparison.OrdinalIgnoreCase)
                : window.Contains("5h", StringComparison.OrdinalIgnoreCase) || bucketId.Contains("5h", StringComparison.OrdinalIgnoreCase) || displayName.Contains("five hour", StringComparison.OrdinalIgnoreCase);
            if (!matches || !TryReadFraction(bucket, out var remainingFraction))
            {
                continue;
            }

            var resetAt = ReadResetTime(bucket);
            var remaining = Math.Clamp(remainingFraction * 100, 0, 100);
            return new QuotaBucket(100 - remaining, remaining, windowMinutes, resetAt);
        }

        return QuotaBucket.Unavailable(windowMinutes);
    }

    private static bool TryReadFraction(JsonElement bucket, out double fraction)
    {
        fraction = 0;
        if (bucket.TryGetProperty("remainingFraction", out var direct) && direct.ValueKind == JsonValueKind.Number)
        {
            fraction = direct.GetDouble();
            return true;
        }

        return bucket.TryGetProperty("remaining", out var remaining) &&
               remaining.ValueKind == JsonValueKind.Object &&
               remaining.TryGetProperty("remainingFraction", out var nested) &&
               nested.ValueKind == JsonValueKind.Number &&
               (fraction = nested.GetDouble()) >= 0;
    }

    private static DateTimeOffset? ReadResetTime(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("resetTime", out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(epoch);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static int ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
