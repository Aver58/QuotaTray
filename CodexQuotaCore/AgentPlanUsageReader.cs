using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaCore;

public sealed class AgentPlanUsageReader
{
    private const string Host = "ark.cn-beijing.volcengineapi.com";
    private const string Region = "cn-beijing";
    private const string Service = "ark";
    private const string QuotaQuery = "Action=GetAFPUsage&Version=2024-01-01";
    private const string HistoryQuery = "Action=GetUsageDetails&Version=2024-01-01";
    private const string Endpoint = "https://" + Host + "/?";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    public CodexQuotaSnapshot ReadSnapshot(string accessKeyId, string secretAccessKey)
    {
        try
        {
            using var response = SendSignedRequest(accessKeyId, secretAccessKey, QuotaQuery, "{}");
            using var document = JsonDocument.Parse(response.Content.ReadAsStream());
            if (!response.IsSuccessStatusCode)
            {
                var detail = ReadErrorMessage(document.RootElement);
                return WithUnavailableQuotas($"火山引擎 Agent Plan 查询失败（HTTP {(int)response.StatusCode}）。{detail}");
            }

            if (!document.RootElement.TryGetProperty("Result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                return WithUnavailableQuotas("火山引擎未返回 Agent Plan 用量。");
            }

            var fiveHour = ReadBucket(result, "AFPFiveHour", 300);
            var daily = ReadBucket(result, "AFPDaily", 1440);
            var weekly = ReadBucket(result, "AFPWeekly", 10080);
            var monthly = ReadBucket(result, "AFPMonthly", 43200);
            var planType = result.TryGetProperty("PlanType", out var planElement) && planElement.ValueKind == JsonValueKind.String
                ? planElement.GetString()
                : null;
            var status = string.IsNullOrWhiteSpace(planType)
                ? "火山引擎 Agent Plan 实时 AFP 额度。"
                : $"火山引擎 Agent Plan（{planType}）实时 AFP 额度。";

            return new CodexQuotaSnapshot(
                DateTimeOffset.UtcNow,
                fiveHour,
                weekly,
                daily,
                monthly,
                null,
                0,
                [],
                Endpoint + QuotaQuery,
                status);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or CryptographicException or InvalidOperationException)
        {
            return WithUnavailableQuotas($"暂时无法获取火山引擎 Agent Plan 用量。{ex.Message}");
        }
    }

    public AgentPlanTokenHistory ReadTokenHistory(string accessKeyId, string secretAccessKey)
    {
        var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
        var start = now.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var body = $$$"""{"QueryInterval":"Hour","Filter":{"StartTime":"{{{start}}}","EndTime":"{{{end}}}"}}""";

        try
        {
            using var response = SendSignedRequest(accessKeyId, secretAccessKey, HistoryQuery, body);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var message = code is 401 or 403
                    ? $"用量明细鉴权失败（HTTP {code}）。"
                    : $"暂时无法获取用量明细（HTTP {code}）。";
                return new AgentPlanTokenHistory(now, [], [], message);
            }

            using var document = JsonDocument.Parse(response.Content.ReadAsStream());
            if (document.RootElement.TryGetProperty("Result", out var result) && result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("Details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                return AggregateTokenHistory(details, now);
            }

            var error = ReadErrorMessage(document.RootElement);
            var status = string.IsNullOrWhiteSpace(error) ? "暂无可读取的套餐用量明细。" : "用量明细查询失败。";
            return new AgentPlanTokenHistory(now, [], [], status);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or CryptographicException
                                   or InvalidOperationException or OperationCanceledException or OverflowException)
        {
            return new AgentPlanTokenHistory(now, [], [], "暂时无法获取火山引擎用量明细。");
        }
    }

    private static AgentPlanTokenHistory AggregateTokenHistory(JsonElement details, DateTimeOffset now)
    {
        var currentHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        var firstHour = currentHour.AddHours(-23);
        var firstDay = DateOnly.FromDateTime(now.DateTime).AddDays(-6);
        var earliest = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), now.Offset).ToUnixTimeMilliseconds();
        var latest = now.ToUnixTimeMilliseconds();
        var hourly = new long[24];
        var daily = new long[7];
        foreach (var detail in details.EnumerateArray())
        {
            if (detail.ValueKind != JsonValueKind.Object ||
                !detail.TryGetProperty("Unit", out var unit) || unit.ValueKind != JsonValueKind.String ||
                !string.Equals(unit.GetString(), "Tokens", StringComparison.OrdinalIgnoreCase) ||
                !detail.TryGetProperty("Time", out var time) || time.ValueKind != JsonValueKind.Number ||
                !time.TryGetInt64(out var milliseconds) || milliseconds < earliest || milliseconds > latest ||
                !detail.TryGetProperty("Usage", out var usage) || usage.ValueKind != JsonValueKind.Number ||
                !usage.TryGetInt64(out var tokens) || tokens <= 0)
            {
                continue;
            }

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToOffset(now.Offset);
            var day = DateOnly.FromDateTime(timestamp.DateTime).DayNumber - firstDay.DayNumber;
            daily[day] = checked(daily[day] + tokens);
            if (timestamp >= firstHour)
            {
                var hour = (int)(timestamp - firstHour).TotalHours;
                hourly[hour] = checked(hourly[hour] + tokens);
            }
        }

        if (daily.All(total => total == 0))
        {
            return new AgentPlanTokenHistory(now, [], [], "暂无已同步的 Token 用量。");
        }

        return new AgentPlanTokenHistory(now,
            hourly.Select((total, index) => new TimedTokenUsage(
                firstHour.AddHours(index), index == hourly.Length - 1 ? now : firstHour.AddHours(index + 1), total)).ToArray(),
            daily.Select((total, index) => new DailyTokenUsage(firstDay.AddDays(index), total)).ToArray(),
            "模型调用明细约延迟 0.5–1 天。");
    }

    private static HttpResponseMessage SendSignedRequest(string accessKeyId, string secretAccessKey, string query, string body)
    {
        const string contentType = "application/json";
        const string signedHeaders = "content-type;host;x-content-sha256;x-date";
        var payloadHash = Sha256Hex(body);
        var requestDate = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var shortDate = requestDate[..8];
        var credentialScope = $"{shortDate}/{Region}/{Service}/request";
        var canonicalHeaders = $"content-type:{contentType}\nhost:{Host}\nx-content-sha256:{payloadHash}\nx-date:{requestDate}\n";
        var canonicalRequest = $"POST\n/\n{query}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var stringToSign = $"HMAC-SHA256\n{requestDate}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";
        var signingKey = DeriveSigningKey(secretAccessKey, shortDate);
        var signature = Convert.ToHexString(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(signingKey);

        var requestBody = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        requestBody.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint + query)
        {
            Content = requestBody
        };
        request.Headers.TryAddWithoutValidation("X-Date", requestDate);
        request.Headers.TryAddWithoutValidation("X-Content-Sha256", payloadHash);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "HMAC-SHA256",
            $"Credential={accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}");

        return HttpClient.Send(request);
    }

    private static byte[] DeriveSigningKey(string secretAccessKey, string date)
    {
        var dateKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretAccessKey), Encoding.UTF8.GetBytes(date));
        var regionKey = HMACSHA256.HashData(dateKey, Encoding.UTF8.GetBytes(Region));
        var serviceKey = HMACSHA256.HashData(regionKey, Encoding.UTF8.GetBytes(Service));
        var signingKey = HMACSHA256.HashData(serviceKey, "request"u8);
        CryptographicOperations.ZeroMemory(dateKey);
        CryptographicOperations.ZeroMemory(regionKey);
        CryptographicOperations.ZeroMemory(serviceKey);
        return signingKey;
    }

    private static QuotaBucket ReadBucket(JsonElement result, string propertyName, int windowMinutes)
    {
        if (!result.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("Quota", out var quotaElement) ||
            !window.TryGetProperty("Used", out var usedElement))
        {
            return QuotaBucket.Unavailable(windowMinutes);
        }

        var quota = ReadDouble(quotaElement);
        var used = ReadDouble(usedElement);
        if (quota <= 0)
        {
            return QuotaBucket.Unavailable(windowMinutes);
        }

        var usedPercent = Math.Clamp(used / quota * 100, 0, 100);
        var resetAt = window.TryGetProperty("ResetTime", out var resetElement)
            ? ReadUnixMilliseconds(resetElement)
            : null;
        return new QuotaBucket(usedPercent, 100 - usedPercent, windowMinutes, resetAt, true, used, quota, "AFP");
    }

    private static double ReadDouble(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0
        };

    private static DateTimeOffset? ReadUnixMilliseconds(JsonElement element)
    {
        var milliseconds = element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value)
            ? value
            : 0;
        return milliseconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
    }

    private static string? ReadErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("ResponseMetadata", out var metadata) &&
            metadata.TryGetProperty("Error", out var error) &&
            error.TryGetProperty("Message", out var message) &&
            message.ValueKind == JsonValueKind.String)
        {
            return message.GetString();
        }

        return null;
    }

    private static CodexQuotaSnapshot WithUnavailableQuotas(string status) =>
        CodexQuotaSnapshot.Empty(status, Endpoint + QuotaQuery) with { LatestEventAt = DateTimeOffset.UtcNow };

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record AgentPlanTokenHistory(
    DateTimeOffset RetrievedAt,
    IReadOnlyList<TimedTokenUsage> Last24Hours,
    IReadOnlyList<DailyTokenUsage> LastSevenDays,
    string Notice);
