using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexLimitShow;

internal sealed record RateWindow(long DurationMinutes, int UsedPercent, DateTimeOffset? ResetsAt)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

internal sealed record ResetCredit(
    string Title,
    string Status,
    DateTimeOffset GrantedAt,
    DateTimeOffset? ExpiresAt);

internal sealed record UsageStats(long? TodayTokens, long? LifetimeTokens);

internal sealed record ResetConsumeParameters(
    [property: System.Text.Json.Serialization.JsonPropertyName("idempotencyKey")] string IdempotencyKey);

internal sealed record AccountSummary(
    string StorageKey,
    string Label,
    string PlanType,
    DateTimeOffset? SubscriptionExpiresAt,
    DateTimeOffset? SubscriptionCheckedAt = null);

internal sealed record QuotaSnapshot(
    RateWindow? FiveHour,
    RateWindow? Weekly,
    IReadOnlyList<RateWindow> OtherWindows,
    long? AvailableResetCredits,
    IReadOnlyList<ResetCredit> ResetCredits,
    UsageStats? Usage,
    DateTimeOffset FetchedAt,
    AccountSummary? Account = null,
    string? RateLimitPlanType = null,
    string? RateLimitName = null)
{
    public RateWindow? TightestWindow =>
        new[] { FiveHour, Weekly }
            .Concat(OtherWindows.Cast<RateWindow?>())
            .Where(window => window is not null)
            .MinBy(window => window!.RemainingPercent);

    public string TightestWindowName => TightestWindow switch
    {
        null => "无活动额度窗口",
        var window when ReferenceEquals(window, FiveHour) => "5 小时",
        var window when ReferenceEquals(window, Weekly) => "周额度",
        var window when IsMonth(window.DurationMinutes) => "月额度",
        _ => $"{TightestWindow.DurationMinutes} 分钟"
    };

    private static bool IsMonth(long minutes) => minutes is >= 40_320 and <= 44_640;
}

internal sealed class CodexAppServerClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private int _requestId;
    private bool _authFileChecked;
    private (DateTime LastWriteUtc, long Length)? _authFileStamp;
    private string? _accountKey;
    private AccountSummary? _cachedAccount;
    private UsageStats? _cachedUsage;
    private DateOnly _usageFetchedDay;
    private bool _forceAccountCheck;

    public string? ExecutablePath { get; private set; }

    public async Task<QuotaSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken, bool verifyAccount = false)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var currentAuthStamp = ReadAuthFileStamp();
            if (_authFileChecked && currentAuthStamp != _authFileStamp)
                StopProcess();
            _authFileStamp = currentAuthStamp;
            _authFileChecked = true;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                await EnsureStartedAsync(cancellationToken);
                using var rateResponse = await ReadRateLimitsWithRetryAsync(cancellationToken);

                var snapshot = QuotaParser.ParseRateLimits(GetResult(rateResponse.RootElement));
                if (_cachedUsage is null || _usageFetchedDay != DateOnly.FromDateTime(DateTime.Now))
                {
                    try
                    {
                        using var usageResponse = await RequestAsync("account/usage/read", cancellationToken);
                        _cachedUsage = QuotaParser.ParseUsage(GetResult(usageResponse.RootElement));
                        _usageFetchedDay = DateOnly.FromDateTime(DateTime.Now);
                    }
                    catch (CodexClientException)
                    {
                        // Usage is optional; retry on the next refresh.
                    }
                }
                snapshot = snapshot with { Usage = _cachedUsage };

                try
                {
                    if (_cachedAccount is null || verifyAccount || _forceAccountCheck)
                    {
                        using var accountResponse = await RequestAsync(
                            "account/read",
                            cancellationToken,
                            new { refreshToken = false });
                        _cachedAccount = QuotaParser.ParseAccount(GetResult(accountResponse.RootElement));
                        _forceAccountCheck = false;
                    }
                    snapshot = snapshot with { Account = _cachedAccount };
                }
                catch (CodexClientException)
                {
                    // Account metadata is optional and never blocks current quota display.
                }

                if (_accountKey is not null && snapshot.Account is { } account &&
                    account.StorageKey != _accountKey)
                {
                    StopProcess();
                    if (attempt > 0)
                        throw new CodexClientException("账号切换后刷新失败");
                    continue;
                }

                _accountKey = snapshot.Account?.StorageKey ?? _accountKey;
                return snapshot with { FetchedAt = DateTimeOffset.Now };
            }

            throw new CodexClientException("账号切换后刷新失败");
        }
        catch
        {
            StopProcess();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ConsumeResetAsync(string expectedAccountKey, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("幂等键不能为空", nameof(idempotencyKey));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is not { HasExited: false } || _cachedAccount?.StorageKey != expectedAccountKey ||
                ReadAuthFileStamp() != _authFileStamp)
                throw new CodexClientException("账号或登录状态已变化，请先刷新");
            using var response = await RequestAsync("account/rateLimitResetCredit/consume", cancellationToken,
                new ResetConsumeParameters(idempotencyKey), timeoutSeconds: 30);
            var result = GetResult(response.RootElement);
            return result.TryGetProperty("outcome", out var outcome) && outcome.ValueKind == JsonValueKind.String
                ? outcome.GetString() ?? "unknown" : "unknown";
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonDocument> ReadRateLimitsWithRetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RequestAsync(
                "account/rateLimits/read",
                cancellationToken,
                timeoutSeconds: 30);
        }
        catch (CodexClientException)
        {
            StopProcess();
            await Task.Delay(500, cancellationToken);
            await EnsureStartedAsync(cancellationToken);
            return await RequestAsync(
                "account/rateLimits/read",
                cancellationToken,
                timeoutSeconds: 30);
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
            return;

        ExecutablePath = DiscoverExecutable()
            ?? throw new CodexClientException("未找到可运行的 Codex CLI");

        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        _process = new Process { StartInfo = startInfo };
        try
        {
            if (!_process.Start())
                throw new CodexClientException("Codex app-server 启动失败");
        }
        catch (Exception exception) when (exception is not CodexClientException)
        {
            throw new CodexClientException("Codex app-server 无法启动", exception);
        }

        _input = _process.StandardInput;
        _output = _process.StandardOutput;
        _ = DiscardStandardErrorAsync(_process.StandardError);

        using var initialize = await RequestAsync(
            "initialize",
            cancellationToken,
            new
            {
                clientInfo = new { name = "codex-limit-show", version = "0.1.0" },
                capabilities = new { experimentalApi = true }
            });

        await SendAsync(new { method = "initialized", @params = new { } }, cancellationToken);
    }

    private async Task<JsonDocument> RequestAsync(
        string method,
        CancellationToken cancellationToken,
        object? parameters = null,
        int timeoutSeconds = 15)
    {
        var id = Interlocked.Increment(ref _requestId);
        await SendAsync(new { id, method, @params = parameters }, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        while (true)
        {
            string? line;
            try
            {
                line = await _output!.ReadLineAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CodexClientException("Codex app-server 响应超时");
            }

            if (line is null)
                throw new CodexClientException("Codex app-server 已退出");

            JsonDocument message;
            try
            {
                message = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var root = message.RootElement;

            if (!root.TryGetProperty("id", out var responseId) ||
                responseId.ValueKind != JsonValueKind.Number ||
                responseId.GetInt32() != id)
            {
                message.Dispose();
                continue;
            }

            if (root.TryGetProperty("error", out _))
            {
                message.Dispose();
                throw new CodexClientException($"{method} 请求失败");
            }

            return message;
        }
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await _input!.WriteLineAsync(json.AsMemory(), cancellationToken);
        await _input.FlushAsync(cancellationToken);
    }

    private static JsonElement GetResult(JsonElement response) =>
        response.TryGetProperty("result", out var result)
            ? result
            : throw new CodexClientException("Codex app-server 返回格式不受支持");

    internal static string? DiscoverExecutable()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var binDirectory = Path.Combine(localAppData, "OpenAI", "Codex", "bin");

        try
        {
            return Directory.EnumerateFiles(binDirectory, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    internal static void SelfTestDiscovery()
    {
        var binDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin");
        var expected = Directory.EnumerateFiles(binDirectory, "codex.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (!string.Equals(DiscoverExecutable(), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Codex executable discovery self-test failed.");
    }

    private static async Task DiscardStandardErrorAsync(StreamReader standardError)
    {
        try
        {
            while (await standardError.ReadLineAsync() is not null) { }
        }
        catch
        {
            // Never persist or display raw app-server diagnostics.
        }
    }

    private void StopProcess()
    {
        _accountKey = null;
        _cachedAccount = null;
        _cachedUsage = null;
        _usageFetchedDay = default;
        _forceAccountCheck = true;
        _input?.Dispose();
        _output?.Dispose();

        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { }
        }

        _process?.Dispose();
        _process = null;
        _input = null;
        _output = null;
    }

    public void Dispose() => StopProcess();

    private static (DateTime LastWriteUtc, long Length)? ReadAuthFileStamp()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex", "auth.json");
            var file = new FileInfo(path);
            return file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal static class QuotaParser
{
    public static QuotaSnapshot ParseRateLimits(JsonElement result)
    {
        JsonElement limits;
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) &&
            buckets.ValueKind == JsonValueKind.Object &&
            buckets.TryGetProperty("codex", out var codexBucket) && codexBucket.ValueKind == JsonValueKind.Object)
            limits = codexBucket;
        else if (result.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            limits = legacy;
        else throw new CodexClientException("额度响应缺少 Codex 额度视图");

        RateWindow? fiveHour = null;
        RateWindow? weekly = null;
        var other = new List<RateWindow>();

        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
                continue;

            var window = ParseWindow(value);
            if (IsFiveHour(window.DurationMinutes))
                fiveHour = window;
            else if (IsWeek(window.DurationMinutes))
                weekly = window;
            else
                other.Add(window);
        }

        if (!other.Any(window => window.DurationMinutes is >= 40_320 and <= 44_640) &&
            limits.TryGetProperty("individualLimit", out var individualLimit) &&
            individualLimit.ValueKind == JsonValueKind.Object &&
            individualLimit.TryGetProperty("remainingPercent", out var remainingValue) &&
            remainingValue.TryGetDouble(out var parsedRemaining))
        {
            var remaining = Math.Clamp((int)Math.Round(parsedRemaining), 0, 100);
            DateTimeOffset? resetsAt = null;
            if (individualLimit.TryGetProperty("resetsAt", out var resetValue) &&
                resetValue.TryGetInt64(out var resetSeconds))
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToLocalTime();
            other.Add(new RateWindow(43_200, 100 - remaining, resetsAt));
        }

        var planType = StringOrNull(limits, "planType");
        var limitName = StringOrNull(limits, "limitName");
        long? availableCount = null;
        var credits = new List<ResetCredit>();
        if (result.TryGetProperty("rateLimitResetCredits", out var resetCredits) &&
            resetCredits.ValueKind == JsonValueKind.Object)
        {
            if (resetCredits.TryGetProperty("availableCount", out var count) &&
                count.TryGetInt64(out var parsedCount))
                availableCount = parsedCount;

            if (resetCredits.TryGetProperty("credits", out var creditRows) &&
                creditRows.ValueKind == JsonValueKind.Array)
            {
                foreach (var credit in creditRows.EnumerateArray())
                {
                    if (!credit.TryGetProperty("grantedAt", out var granted) ||
                        !granted.TryGetInt64(out var grantedSeconds))
                        continue;

                    var title = StringOrNull(credit, "title") ?? "额度重置机会";
                    var status = StringOrNull(credit, "status") ?? "unknown";
                    DateTimeOffset? expiresAt = null;
                    if (credit.TryGetProperty("expiresAt", out var expires) &&
                        expires.TryGetInt64(out var expiresSeconds))
                        expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresSeconds).ToLocalTime();

                    credits.Add(new ResetCredit(
                        title,
                        status,
                        DateTimeOffset.FromUnixTimeSeconds(grantedSeconds).ToLocalTime(),
                        expiresAt));
                }
            }
        }

        return new QuotaSnapshot(
            fiveHour,
            weekly,
            other,
            availableCount,
            credits,
            null,
            DateTimeOffset.Now,
            RateLimitPlanType: planType,
            RateLimitName: limitName);
    }

    public static UsageStats ParseUsage(JsonElement result)
    {
        long? todayTokens = null;
        if (result.TryGetProperty("dailyUsageBuckets", out var buckets) &&
            buckets.ValueKind == JsonValueKind.Array)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (StringOrNull(bucket, "startDate") is not { } dateText ||
                    !DateOnly.TryParse(dateText, out var date) ||
                    date != today ||
                    !bucket.TryGetProperty("tokens", out var tokens) ||
                    !tokens.TryGetInt64(out var value))
                    continue;

                todayTokens = value;
                break;
            }
        }

        long? lifetimeTokens = null;
        if (result.TryGetProperty("summary", out var summary) &&
            summary.ValueKind == JsonValueKind.Object &&
            summary.TryGetProperty("lifetimeTokens", out var lifetime) &&
            lifetime.TryGetInt64(out var total))
            lifetimeTokens = total;

        return new UsageStats(todayTokens, lifetimeTokens);
    }

    public static AccountSummary? ParseAccount(JsonElement result)
    {
        if (!result.TryGetProperty("account", out var account) ||
            account.ValueKind != JsonValueKind.Object)
            return null;

        var type = StringOrNull(account, "type");
        var planType = StringOrNull(account, "planType") ?? type ?? "unknown";
        if (type == "chatgpt" && StringOrNull(account, "email") is { Length: > 0 } email)
        {
            var displayEmail = email.Trim();
            var normalized = displayEmail.ToLowerInvariant();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return new AccountSummary(
                Convert.ToHexString(hash.AsSpan(0, 12)),
                displayEmail,
                planType,
                null);
        }

        if (type == "apiKey")
        {
            var hash = SHA256.HashData("codex-limit-show:api-key"u8);
            return new AccountSummary(
                Convert.ToHexString(hash.AsSpan(0, 12)),
                "API Key",
                planType,
                null);
        }

        return null;
    }

    private static RateWindow ParseWindow(JsonElement value)
    {
        var used = value.TryGetProperty("usedPercent", out var usedValue) &&
                   usedValue.TryGetInt32(out var parsedUsed)
            ? Math.Clamp(parsedUsed, 0, 100)
            : throw new CodexClientException("额度窗口缺少 usedPercent");

        var duration = value.TryGetProperty("windowDurationMins", out var durationValue) &&
                       durationValue.TryGetInt64(out var parsedDuration)
            ? parsedDuration
            : 0;

        DateTimeOffset? resetsAt = null;
        if (value.TryGetProperty("resetsAt", out var resetValue) &&
            resetValue.TryGetInt64(out var resetSeconds))
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToLocalTime();

        return new RateWindow(duration, used, resetsAt);
    }

    private static bool IsFiveHour(long minutes) => minutes is >= 240 and <= 360;
    private static bool IsWeek(long minutes) => minutes is >= 9_900 and <= 10_260;

    private static string? StringOrNull(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static void SelfTest()
    {
        using var rateJson = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "limitName": "Normal",
                "planType": "prolite",
                "primary": null,
                "secondary": {
                  "usedPercent": 35,
                  "windowDurationMins": 10080,
                  "resetsAt": 1785000000
                },
                "individualLimit": {
                  "limit": "100",
                  "used": "25",
                  "remainingPercent": 75,
                  "resetsAt": 1786000000
                }
              },
              "rateLimitResetCredits": {
                "availableCount": 1,
                "credits": [{
                  "id": "must-not-be-copied",
                  "title": "Full reset",
                  "status": "available",
                  "grantedAt": 1784000000,
                  "expiresAt": 1786000000
                }]
              }
            }
            """);
        var snapshot = ParseRateLimits(rateJson.RootElement);
        Check(snapshot.FiveHour is null);
        Check(snapshot.Weekly?.RemainingPercent == 65);
        Check(snapshot.TightestWindow == snapshot.Weekly);
        Check(snapshot.OtherWindows is [{ DurationMinutes: 43200, RemainingPercent: 75 }]);
        Check(snapshot.RateLimitPlanType == "prolite");
        Check(snapshot.RateLimitName == "Normal");
        Check(snapshot.ResetCredits is [{ Title: "Full reset", Status: "available" }]);

        using var swappedJson = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "primary": {"usedPercent": 10, "windowDurationMins": 10080},
                "secondary": {"usedPercent": 80, "windowDurationMins": 300}
              }
            }
            """);
        var swapped = ParseRateLimits(swappedJson.RootElement);
        Check(swapped.FiveHour?.UsedPercent == 80);
        Check(swapped.Weekly?.UsedPercent == 10);
        Check(swapped.TightestWindow == swapped.FiveHour);

        using var accountJson = JsonDocument.Parse(
            """{"account":{"type":"chatgpt","email":"alice@example.com","planType":"plus"},"requiresOpenaiAuth":true}""");
        var account = ParseAccount(accountJson.RootElement);
        Check(account is { Label: "alice@example.com", PlanType: "plus", SubscriptionExpiresAt: null });
        Check(account?.StorageKey.Length == 24);

        using var multiJson = JsonDocument.Parse(
            """
            {"rateLimits":null,"rateLimitsByLimitId":{
              "codex":{"primary":{"usedPercent":70,"windowDurationMins":300},"secondary":null},
              "other":{"primary":{"usedPercent":2,"windowDurationMins":10080}}
            }}
            """);
        var multi = ParseRateLimits(multiJson.RootElement);
        Check(multi.FiveHour?.RemainingPercent == 30 && multi.Weekly is null);
        using var multiPreferred = JsonDocument.Parse(
            """{"rateLimits":{"primary":{"usedPercent":1,"windowDurationMins":300}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":65,"windowDurationMins":300}}}}""");
        Check(ParseRateLimits(multiPreferred.RootElement).FiveHour?.RemainingPercent == 35);
    }

    private static void Check(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Quota parser self-test failed.");
    }
}

internal sealed class CodexClientException(string message, Exception? innerException = null)
    : Exception(message, innerException);
