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
    private const string Query = "Action=GetAFPUsage&Version=2024-01-01";
    private const string Endpoint = "https://" + Host + "/?" + Query;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public CodexQuotaSnapshot ReadSnapshot(
        string accessKeyId,
        string secretAccessKey,
        CodexQuotaSnapshot localTokenUsage)
    {
        try
        {
            using var response = SendSignedRequest(accessKeyId, secretAccessKey);
            using var document = JsonDocument.Parse(response.Content.ReadAsStream());
            if (!response.IsSuccessStatusCode)
            {
                var detail = ReadErrorMessage(document.RootElement);
                return WithUnavailableQuotas(localTokenUsage,
                    $"Volcano Engine Agent Plan request failed ({(int)response.StatusCode}){(detail is null ? "." : $": {detail}")}");
            }

            if (!document.RootElement.TryGetProperty("Result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                return WithUnavailableQuotas(localTokenUsage, "Volcano Engine Agent Plan response did not contain a Result object.");
            }

            var fiveHour = ReadBucket(result, "AFPFiveHour", 300);
            var daily = ReadBucket(result, "AFPDaily", 1440);
            var weekly = ReadBucket(result, "AFPWeekly", 10080);
            var monthly = ReadBucket(result, "AFPMonthly", 43200);
            var planType = result.TryGetProperty("PlanType", out var planElement) && planElement.ValueKind == JsonValueKind.String
                ? planElement.GetString()
                : null;
            var status = string.IsNullOrWhiteSpace(planType)
                ? "Live Agent Plan AFP quota from Volcano Engine."
                : $"Live Agent Plan {planType} AFP quota from Volcano Engine.";

            return new CodexQuotaSnapshot(
                DateTimeOffset.UtcNow,
                fiveHour,
                weekly,
                daily,
                monthly,
                null,
                localTokenUsage.TodayTokens,
                localTokenUsage.LastSevenDays,
                Endpoint,
                status);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or CryptographicException or InvalidOperationException)
        {
            return WithUnavailableQuotas(localTokenUsage, $"Volcano Engine Agent Plan unavailable: {ex.Message}");
        }
    }

    private static HttpResponseMessage SendSignedRequest(string accessKeyId, string secretAccessKey)
    {
        const string body = "{}";
        const string contentType = "application/json";
        const string signedHeaders = "content-type;host;x-content-sha256;x-date";
        var payloadHash = Sha256Hex(body);
        var requestDate = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var shortDate = requestDate[..8];
        var credentialScope = $"{shortDate}/{Region}/{Service}/request";
        var canonicalHeaders = $"content-type:{contentType}\nhost:{Host}\nx-content-sha256:{payloadHash}\nx-date:{requestDate}\n";
        var canonicalRequest = $"POST\n/\n{Query}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var stringToSign = $"HMAC-SHA256\n{requestDate}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";
        var signingKey = DeriveSigningKey(secretAccessKey, shortDate);
        var signature = Convert.ToHexString(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(signingKey);

        var requestBody = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        requestBody.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
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

    private static CodexQuotaSnapshot WithUnavailableQuotas(CodexQuotaSnapshot localUsage, string status) =>
        new(
            DateTimeOffset.UtcNow,
            QuotaBucket.Unavailable(300),
            QuotaBucket.Unavailable(10080),
            QuotaBucket.Unavailable(1440),
            QuotaBucket.Unavailable(43200),
            null,
            localUsage.TodayTokens,
            localUsage.LastSevenDays,
            Endpoint,
            status);

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
