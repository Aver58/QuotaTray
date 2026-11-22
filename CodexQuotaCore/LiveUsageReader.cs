using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexQuotaCore;

internal sealed class LiveUsageReader
{
    internal const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly string _authPath;

    public LiveUsageReader(string codexHome)
    {
        _authPath = Path.Combine(codexHome, "auth.json");
    }

    public LiveUsageResult Read()
    {
        try
        {
            var auth = ReadAuth();
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", auth.AccountId);
            request.Headers.UserAgent.ParseAdd("CodexQuotaTray/1.0");

            using var response = HttpClient.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                return LiveUsageResult.Failed($"Live quota request returned {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(response.Content.ReadAsStream());
            var buckets = ReadBuckets(document.RootElement);
            var availableResetCredits = ReadAvailableResetCredits(document.RootElement);
            return new LiveUsageResult(
                true,
                DateTimeOffset.UtcNow,
                buckets,
                availableResetCredits,
                UsageEndpoint,
                "Live quota from Codex."
            );
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return LiveUsageResult.Failed($"Live quota unavailable: {ex.Message}");
        }
    }

    private AuthInfo ReadAuth()
    {
        using var stream = new FileStream(_authPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Codex login tokens were not found.");
        }

        var accessToken = ReadString(tokens, "access_token");
        var accountId = ReadString(tokens, "account_id");
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(accountId))
        {
            throw new InvalidOperationException("Codex login tokens are incomplete.");
        }

        return new AuthInfo(accessToken, accountId);
    }

    private static IReadOnlyList<QuotaBucket> ReadBuckets(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit", out var rateLimit) || rateLimit.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<QuotaBucket>();
        }

        var buckets = new List<QuotaBucket>();
        AddWindow(rateLimit, "primary_window", buckets);
        AddWindow(rateLimit, "secondary_window", buckets);
        return buckets;
    }

    private static int? ReadAvailableResetCredits(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_reset_credits", out var resetCredits) ||
            resetCredits.ValueKind != JsonValueKind.Object ||
            !resetCredits.TryGetProperty("available_count", out var availableCount))
        {
            return null;
        }

        return Math.Max(0, (int)ReadInt64(availableCount));
    }

    private static void AddWindow(JsonElement rateLimit, string name, ICollection<QuotaBucket> buckets)
    {
        if (!rateLimit.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var usedPercent = window.TryGetProperty("used_percent", out var usedElement)
            ? Math.Clamp(ReadDouble(usedElement), 0, 100)
            : 0;
        var windowMinutes = window.TryGetProperty("limit_window_seconds", out var durationElement)
            ? Math.Max(0, (int)Math.Round(ReadInt64(durationElement) / 60.0, MidpointRounding.AwayFromZero))
            : 0;
        var resetsAt = window.TryGetProperty("reset_at", out var resetElement)
            ? ReadUnixSeconds(resetElement)
            : null;

        if (windowMinutes > 0)
        {
            buckets.Add(new QuotaBucket(usedPercent, 100 - usedPercent, windowMinutes, resetsAt));
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long ReadInt64(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var value) => value,
            JsonValueKind.Number => (long)element.GetDouble(),
            JsonValueKind.String when long.TryParse(element.GetString(), out var value) => value,
            _ => 0
        };

    private static double ReadDouble(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString(), out var value) => value,
            _ => 0
        };

    private static DateTimeOffset? ReadUnixSeconds(JsonElement element)
    {
        var seconds = ReadInt64(element);
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    private sealed record AuthInfo(string AccessToken, string AccountId);
}

internal sealed record LiveUsageResult(
    bool IsSuccess,
    DateTimeOffset? RetrievedAt,
    IReadOnlyList<QuotaBucket> Buckets,
    int? AvailableResetCredits,
    string Source,
    string Status
)
{
    public static LiveUsageResult Failed(string status) =>
        new(false, null, Array.Empty<QuotaBucket>(), null, string.Empty, status);
}
