using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaCore;

public sealed class VolcanoAccountBalanceReader
{
    private const string Host = "billing.volcengineapi.com";
    private const string Region = "cn-beijing";
    private const string Service = "billing";
    private const string Query = "Action=QueryBalanceAcct&Version=2022-01-01";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public VolcanoAccountBalance Read(string accessKeyId, string secretAccessKey)
    {
        const string body = "";
        var payloadHash = Sha256Hex(body);
        var requestDate = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var shortDate = requestDate[..8];
        var scope = $"{shortDate}/{Region}/{Service}/request";
        const string signedHeaders = "host;x-date";
        var canonicalHeaders = $"host:{Host}\nx-date:{requestDate}\n";
        var canonicalRequest = $"GET\n/\n{Query}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var toSign = $"HMAC-SHA256\n{requestDate}\n{scope}\n{Sha256Hex(canonicalRequest)}";
        var signingKey = DeriveKey(secretAccessKey, shortDate);
        var signature = Convert.ToHexString(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(toSign))).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(signingKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/?{Query}");
        request.Headers.TryAddWithoutValidation("X-Date", requestDate);
        request.Headers.Authorization = new AuthenticationHeaderValue("HMAC-SHA256",
            $"Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        using var response = Client.Send(request);
        using var document = JsonDocument.Parse(response.Content.ReadAsStream());
        if (!response.IsSuccessStatusCode)
        {
            var message = document.RootElement.TryGetProperty("ResponseMetadata", out var metadata) &&
                          metadata.TryGetProperty("Error", out var error) &&
                          error.TryGetProperty("Message", out var errorMessage)
                ? errorMessage.GetString()
                : response.ReasonPhrase;
            throw new HttpRequestException($"余额查询失败（HTTP {(int)response.StatusCode}）。{message}");
        }

        if (!document.RootElement.TryGetProperty("Result", out var result) ||
            !result.TryGetProperty("AvailableBalance", out var available) ||
            !result.TryGetProperty("CashBalance", out var cash) ||
            !TryReadDecimal(available, out var availableBalance) ||
            !TryReadDecimal(cash, out var cashBalance))
        {
            throw new InvalidDataException("余额响应缺少有效的可用余额或现金余额。");
        }

        return new VolcanoAccountBalance(availableBalance, cashBalance);
    }

    private static byte[] DeriveKey(string secret, string date)
    {
        var dateKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(date));
        var regionKey = HMACSHA256.HashData(dateKey, Encoding.UTF8.GetBytes(Region));
        var serviceKey = HMACSHA256.HashData(regionKey, Encoding.UTF8.GetBytes(Service));
        var key = HMACSHA256.HashData(serviceKey, "request"u8);
        CryptographicOperations.ZeroMemory(dateKey);
        CryptographicOperations.ZeroMemory(regionKey);
        CryptographicOperations.ZeroMemory(serviceKey);
        return key;
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDecimal(out value);
        }

        return element.ValueKind == JsonValueKind.String &&
               decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
