using System.Net.Http.Headers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexLimitShow;

// The ChatGPT subscription endpoints are not part of the public App Server API.
// Keep this isolated, read-only, and strictly opt-in per account/event.
internal sealed class SubscriptionLookup
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public DateTimeOffset? ReadLocalExpiry(AccountSummary expected)
    {
        var authPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
        using var auth = JsonDocument.Parse(File.ReadAllText(authPath));
        var tokens = auth.RootElement.GetProperty("tokens");
        var accountId = tokens.GetProperty("account_id").GetString();
        var idToken = tokens.GetProperty("id_token").GetString();
        if (string.IsNullOrWhiteSpace(accountId) ||
            !TryReadTokenClaims(idToken, accountId, expected.Label, out var expiry))
            throw new CodexClientException("订阅查询：本地登录账号无法确认一致");
        // This is a saved login claim, not a fresh subscription verification.
        return expiry;
    }

    public async Task<DateTimeOffset?> ReadExpiryAsync(AccountSummary expected, CancellationToken token)
    {
        var authPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
        using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(authPath, token));
        var tokens = auth.RootElement.GetProperty("tokens");
        var accessToken = tokens.GetProperty("access_token").GetString();
        var accountId = tokens.GetProperty("account_id").GetString();
        var idToken = tokens.GetProperty("id_token").GetString();
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(accountId) ||
            !TryReadTokenClaims(idToken, accountId, expected.Label, out _))
            throw new CodexClientException("订阅查询：本地登录账号无法确认一致");

        var offset = -(int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now).TotalMinutes;
        try
        {
            using var check = await GetAsync($"/backend-api/accounts/check/v4-2023-04-27?timezone_offset_min={offset}", accessToken, null, token);
            var expiry = FindMatchingExpiry(check.RootElement, accountId);
            if (expiry is not null && expiry > DateTimeOffset.Now)
                return expiry;
        }
        catch (CodexClientException ex) when (!ex.Message.Contains("HTTP 401", StringComparison.Ordinal) &&
                                               !ex.Message.Contains("网页防护拦截", StringComparison.Ordinal))
        {
            // This private endpoint is not stable. Try the account-scoped subscription view once.
        }

        using var subscriptions = await GetAsync("/backend-api/subscriptions?account_id=" + Uri.EscapeDataString(accountId), accessToken, null, token);
        var matched = FindMatchingExpiry(subscriptions.RootElement, accountId);
        if (matched is not null) return matched;
        // The subscription endpoint can return a single account-scoped object without an ID.
        // Accept it only when the request was bound to the verified local account and no other ID is present.
        return ContainsConflictingId(subscriptions.RootElement, accountId) ? null :
            ParseDateProperty(subscriptions.RootElement, "active_until") ??
            ParseDateProperty(subscriptions.RootElement, "expires_at");
    }

    private static async Task<JsonDocument> GetAsync(string path, string accessToken, string? accountId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://chatgpt.com" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Referrer = new Uri("https://chatgpt.com/");
        request.Headers.TryAddWithoutValidation("x-openai-target-path", path.Split('?')[0]);
        request.Headers.TryAddWithoutValidation("x-openai-target-route", path.Split('?')[0]);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
        if (accountId is not null)
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            throw new CodexClientException(IsWebProtectionResponse(response)
                ? "订阅查询被网页防护拦截（403）"
                : $"订阅查询失败：HTTP {(int)response.StatusCode}");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    private static bool IsWebProtectionResponse(HttpResponseMessage response) =>
        response.StatusCode == System.Net.HttpStatusCode.Forbidden &&
        response.Content.Headers.ContentType?.MediaType == "text/html" &&
        response.Headers.Server.Any(value => value.Product?.Name?.Equals("cloudflare", StringComparison.OrdinalIgnoreCase) == true);

    private static bool TryReadTokenClaims(string? jwt, string accountId, string expectedEmail,
        out DateTimeOffset? subscriptionExpiry)
    {
        subscriptionExpiry = null;
        try
        {
            var part = jwt!.Split('.')[1].Replace('-', '+').Replace('_', '/');
            part = part.PadRight((part.Length + 3) / 4 * 4, '=');
            using var payload = JsonDocument.Parse(Convert.FromBase64String(part));
            var root = payload.RootElement;
            var email = root.TryGetProperty("email", out var directEmail) ? directEmail.GetString() :
                root.TryGetProperty("https://api.openai.com/profile", out var profile) &&
                profile.TryGetProperty("email", out var profileEmail) ? profileEmail.GetString() : null;
            if (!string.Equals(email, expectedEmail, StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("https://api.openai.com/auth", out var auth)) return false;
            var tokenAccountId = auth.TryGetProperty("chatgpt_account_id", out var chatgptId) ? chatgptId.GetString() :
                auth.TryGetProperty("account_id", out var fallbackId) ? fallbackId.GetString() : null;
            if (!string.Equals(tokenAccountId, accountId, StringComparison.Ordinal)) return false;
            subscriptionExpiry = ParseDateProperty(auth, "chatgpt_subscription_active_until");
            return true;
        }
        catch { return false; }
    }

    private static DateTimeOffset? FindMatchingExpiry(JsonElement node, string accountId)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var idMatches = MatchesId(node, accountId) ||
                (node.TryGetProperty("account", out var account) && MatchesId(account, accountId));
            if (idMatches)
            {
                var direct = ParseDateProperty(node, "active_until") ?? ParseDateProperty(node, "expires_at");
                if (direct is not null) return direct;
                if (node.TryGetProperty("entitlement", out var entitlement))
                {
                    var nested = ParseDateProperty(entitlement, "expires_at");
                    if (nested is not null) return nested;
                }
            }
            foreach (var property in node.EnumerateObject())
            {
                // Some account/check responses key account records by account ID.
                if (property.Name == accountId)
                {
                    var expiry = ParseDateProperty(property.Value, "active_until") ??
                                 ParseDateProperty(property.Value, "expires_at");
                    if (property.Value.ValueKind == JsonValueKind.Object &&
                        property.Value.TryGetProperty("entitlement", out var entitlement))
                        expiry ??= ParseDateProperty(entitlement, "expires_at");
                    if (expiry is not null) return expiry;
                }
                var child = FindMatchingExpiry(property.Value, accountId);
                if (child is not null) return child;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray())
            {
                var child = FindMatchingExpiry(item, accountId);
                if (child is not null) return child;
            }
        return null;
    }

    private static bool MatchesId(JsonElement node, string accountId) =>
        node.ValueKind == JsonValueKind.Object && new[] { "account_id", "chatgpt_account_id", "id" }
            .Any(key => node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                        value.GetString() == accountId);

    private static bool ContainsConflictingId(JsonElement node, string accountId)
    {
        if (node.ValueKind != JsonValueKind.Object) return false;
        return new[] { "account_id", "chatgpt_account_id", "id" }
            .Any(key => node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                        !string.Equals(value.GetString(), accountId, StringComparison.Ordinal));
    }

    internal static void SelfTest()
    {
        using var blocked = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
            { Content = new StringContent("blocked", System.Text.Encoding.UTF8, "text/html") };
        blocked.Headers.Server.ParseAdd("cloudflare");
        if (!IsWebProtectionResponse(blocked)) throw new InvalidOperationException("Cloudflare block classification failed.");
        using var sample = JsonDocument.Parse("""
            {"accounts":[{"account":{"id":"other"},"entitlement":{"expires_at":"2026-10-01T00:00:00Z"}},
            {"account":{"id":"wanted"},"entitlement":{"expires_at":"2026-11-01T00:00:00Z"}}]}
            """);
        if (FindMatchingExpiry(sample.RootElement, "wanted")?.UtcDateTime.Day != 1 ||
            FindMatchingExpiry(sample.RootElement, "missing") is not null)
            throw new InvalidOperationException("Subscription account isolation failed.");
        var claim = """{"email":"alice@example.com","https://api.openai.com/auth":{"chatgpt_account_id":"wanted","chatgpt_subscription_active_until":"1790975820000"}}""";
        var token = "x." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claim)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".x";
        if (!TryReadTokenClaims(token, "wanted", "alice@example.com", out var localExpiry) ||
            localExpiry != DateTimeOffset.FromUnixTimeMilliseconds(1790975820000).ToLocalTime() ||
            TryReadTokenClaims(token, "other", "alice@example.com", out _) ||
            TryReadTokenClaims(token, "wanted", "bob@example.com", out _))
            throw new InvalidOperationException("Subscription login claim validation failed.");
    }

    private static DateTimeOffset? ParseDateProperty(JsonElement node, string key)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out var value)) return null;
        var raw = value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
        {
            try { return (timestamp > 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                : DateTimeOffset.FromUnixTimeSeconds(timestamp)).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToLocalTime() : null;
    }
}
