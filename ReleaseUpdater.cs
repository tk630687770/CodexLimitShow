using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexLimitShow;

internal sealed record RemoteReleaseCandidate(Version Version, string DownloadUrl, string Sha256);
internal readonly record struct DownloadProgress(long Received, long? Total, bool Verifying)
{
    public int? Percent => Total is > 0 ? (int)Math.Clamp(Received * 100 / Total.Value, 0, 100) : null;
}

internal sealed class ReleaseUpdater
{
    public const string ExecutableName = "CodexLimitShow.exe";
    private const string Repository = "tk630687770/CodexLimitShow";
    private const long MaximumBytes = 250_000_000;
    private const int MaximumMetadataBytes = 1_048_576;
    private static readonly HttpClient Github = CreateGithubClient();
    private readonly Func<string, Version?> _readVersion;
    private readonly HttpClient _github;
    private readonly string _cachePath;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private DateTimeOffset? _lastAttemptUtc;
    private string? _etag;

    private sealed record UpdateCheckCache(DateTimeOffset LastAttemptUtc, string? ETag,
        string? Version, string? DownloadUrl, string? Sha256);

    public static TimeSpan AutomaticCheckInterval => TimeSpan.FromHours(24);
    public RemoteReleaseCandidate? CachedRelease { get; private set; }
    public TimeSpan AutomaticCheckDelay
    {
        get
        {
            var now = _utcNow();
            if (_lastAttemptUtc is null) return TimeSpan.Zero;
            if (_lastAttemptUtc > now)
            {
                // A clock rollback must not postpone checks indefinitely or cause restart retries.
                _lastAttemptUtc = now;
                SaveCheckCache();
            }
            var delay = _lastAttemptUtc.Value + AutomaticCheckInterval - now;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
    }

    public ReleaseUpdater(Func<string, Version?>? readVersion = null, HttpClient? github = null,
        string? cachePath = null, Func<DateTimeOffset>? utcNow = null)
    {
        _readVersion = readVersion ?? ReadVersion;
        _github = github ?? Github;
        _cachePath = cachePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLimitShow", "update-check.json");
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        LoadCheckCache();
    }

    public Version? CurrentVersion(string? executable) =>
        string.IsNullOrWhiteSpace(executable) ? null : _readVersion(executable);

    public Task<RemoteReleaseCandidate?> CheckRemoteAsync(CancellationToken cancellationToken) =>
        CheckAsync(automatic: false, cancellationToken);

    public Task<RemoteReleaseCandidate?> CheckAutomaticallyAsync(CancellationToken cancellationToken) =>
        CheckAsync(automatic: true, cancellationToken);

    private async Task<RemoteReleaseCandidate?> CheckAsync(bool automatic, CancellationToken cancellationToken)
    {
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (automatic && AutomaticCheckDelay > TimeSpan.Zero) return CachedRelease;
            _lastAttemptUtc = _utcNow();
            SaveCheckCache();
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Repository}/releases/latest");
            if (EntityTagHeaderValue.TryParse(_etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
            using var response = await _github.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified) return CachedRelease;
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                CachedRelease = null;
                _etag = null;
            }
            else
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumMetadataBytes)
                    throw new InvalidDataException("GitHub 版本信息超出大小限制。");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var payload = new MemoryStream();
                var buffer = new byte[16_384];
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (payload.Length + read > MaximumMetadataBytes)
                        throw new InvalidDataException("GitHub 版本信息超出大小限制。");
                    payload.Write(buffer, 0, read);
                }
                using var document = JsonDocument.Parse(payload.GetBuffer().AsMemory(0, (int)payload.Length));
                CachedRelease = ParseRemoteRelease(document.RootElement);
                _etag = response.Headers.ETag?.ToString();
            }
            SaveCheckCache();
            return CachedRelease;
        }
        finally { _checkGate.Release(); }
    }

    private void LoadCheckCache()
    {
        try
        {
            if (!File.Exists(_cachePath) || new FileInfo(_cachePath).Length > 16_384) return;
            var cache = JsonSerializer.Deserialize<UpdateCheckCache>(File.ReadAllText(_cachePath));
            if (cache is null) return;
            var now = _utcNow();
            _lastAttemptUtc = cache.LastAttemptUtc < DateTimeOffset.UnixEpoch || cache.LastAttemptUtc > now
                ? now : cache.LastAttemptUtc;
            if (cache.Version is null && cache.DownloadUrl is null && cache.Sha256 is null)
                _etag = EntityTagHeaderValue.TryParse(cache.ETag, out var emptyTag) ? emptyTag.ToString() : null;
            else if (Version.TryParse(cache.Version, out var version) &&
                cache.DownloadUrl == $"https://github.com/{Repository}/releases/download/v{version}/{ExecutableName}" &&
                cache.Sha256 is { Length: 64 } && cache.Sha256.All(Uri.IsHexDigit))
            {
                CachedRelease = new RemoteReleaseCandidate(version, cache.DownloadUrl, cache.Sha256);
                _etag = EntityTagHeaderValue.TryParse(cache.ETag, out var tag) ? tag.ToString() : null;
            }
            if (_lastAttemptUtc != cache.LastAttemptUtc) SaveCheckCache();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void SaveCheckCache()
    {
        if (_lastAttemptUtc is null) return;
        var temporary = _cachePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cachePath))!);
            var cache = new UpdateCheckCache(_lastAttemptUtc.Value, _etag, CachedRelease?.Version.ToString(),
                CachedRelease?.DownloadUrl, CachedRelease?.Sha256);
            File.WriteAllText(temporary, JsonSerializer.Serialize(cache));
            File.Move(temporary, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static RemoteReleaseCandidate? ParseRemoteRelease(JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString();
        if (tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) ||
            release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var expectedUrl = $"https://github.com/{Repository}/releases/download/{tag}/{ExecutableName}";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != ExecutableName ||
                asset.GetProperty("browser_download_url").GetString() != expectedUrl ||
                !asset.TryGetProperty("size", out var size) || size.GetInt64() is <= 0 or > MaximumBytes ||
                !asset.TryGetProperty("digest", out var digest)) continue;
            var hash = digest.GetString();
            if (hash is { Length: 71 } && hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                hash[7..].All(Uri.IsHexDigit))
                return new RemoteReleaseCandidate(version, expectedUrl, hash[7..]);
        }
        return null;
    }

    public async Task<string> DownloadAsync(RemoteReleaseCandidate remote, IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directory = UpdateDirectory();
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"CodexLimitShow-update-{Guid.NewGuid():N}.exe");
        try
        {
            using var response = await _github.GetAsync(remote.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var expectedBytes = response.Content.Headers.ContentLength;
            if (expectedBytes > MaximumBytes) throw new InvalidDataException("发布程序超出大小限制。");
            if (expectedBytes is <= 0) expectedBytes = null;
            progress?.Report(new DownloadProgress(0, expectedBytes, false));
            long total = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long lastReportedBytes = 0;
                var lastPercent = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaximumBytes) throw new InvalidDataException("发布程序超出大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    var current = new DownloadProgress(total, expectedBytes, false);
                    if ((current.Percent is { } percent && percent > lastPercent) ||
                        (expectedBytes is null && total - lastReportedBytes >= 1_048_576))
                    {
                        progress?.Report(current);
                        lastPercent = current.Percent ?? lastPercent;
                        lastReportedBytes = total;
                    }
                }
            }
            progress?.Report(new DownloadProgress(total, expectedBytes, true));
            if (!HashMatches(destination, remote.Sha256) || _readVersion(destination) != remote.Version)
                throw new InvalidDataException("GitHub 发布程序校验失败。");
            return destination;
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
    }

    public void Apply(string sourceExecutable, string targetExecutable, int oldProcessId, string expectedSha256)
    {
        var source = Path.GetFullPath(sourceExecutable);
        var target = Path.GetFullPath(targetExecutable);
        if (!Path.GetDirectoryName(source)!.Equals(UpdateDirectory(), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(source).StartsWith("CodexLimitShow-update-", StringComparison.Ordinal) ||
            !Path.GetFileName(source).EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            !HashMatches(source, expectedSha256))
            throw new InvalidOperationException("升级文件或目标路径校验失败。");
        var newVersion = _readVersion(source);
        var oldVersion = _readVersion(target);
        if (newVersion is null || oldVersion is null || newVersion <= oldVersion)
            throw new InvalidOperationException("升级版本必须高于当前程序。");

        try
        {
            using var oldProcess = Process.GetProcessById(oldProcessId);
            if (!oldProcess.WaitForExit(30_000)) throw new TimeoutException("旧版未退出，原程序保持不变。");
        }
        catch (ArgumentException) { /* The old process already exited. */ }

        if (_readVersion(target) != oldVersion)
            throw new InvalidOperationException("原程序在升级期间发生变化。");
        var staging = Path.Combine(Path.GetDirectoryName(target)!, $".CodexLimitShow-update-{Guid.NewGuid():N}.exe");
        try
        {
            File.Copy(source, staging);
            if (!HashMatches(staging, expectedSha256)) throw new InvalidDataException("暂存程序校验失败。");
            File.Replace(staging, target, null);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    public static void CleanupOldDownloads()
    {
        var directory = UpdateDirectory();
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "CodexLimitShow-update-*.exe"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1)) File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string UpdateDirectory() => Path.Combine(Path.GetTempPath(), "CodexLimitShow-updates");

    private static bool HashMatches(string path, string expected)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit) || !File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateGithubClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false
        }) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexLimitShow/1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version? ReadVersion(string executable)
    {
        if (!File.Exists(executable)) return null;
        var info = FileVersionInfo.GetVersionInfo(executable);
        if (!string.Equals(info.ProductName, "CodexLimitShow", StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(info.ProductVersion, out var product) ||
            !Version.TryParse(info.FileVersion, out var file) ||
            product.Major != file.Major || product.Minor != file.Minor || product.Build != file.Build) return null;
        return product;
    }

    public static void SelfTest()
    {
        var releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "v2.0.0", draft = false, prerelease = false,
            assets = new[] { new {
                name = ExecutableName,
                browser_download_url = $"https://github.com/{Repository}/releases/download/v2.0.0/{ExecutableName}",
                size = 70_000_000,
                digest = "sha256:" + new string('a', 64)
            } }
        });
        using var document = JsonDocument.Parse(releaseJson);
        if (ParseRemoteRelease(document.RootElement)?.Version != new Version(2, 0, 0))
            throw new Exception("GitHub single-file release parsing failed.");
        if (new DownloadProgress(50, 100, false).Percent != 50 ||
            new DownloadProgress(150, 100, false).Percent != 100 ||
            new DownloadProgress(50, null, false).Percent is not null)
            throw new Exception("Download progress calculation failed.");
        var root = Path.Combine(Path.GetTempPath(), $"CodexLimitShow-update-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            SelfTestMetadata(root, releaseJson);
            var source = Path.Combine(UpdateDirectory(), $"CodexLimitShow-update-{Guid.NewGuid():N}.exe");
            var target = Path.Combine(root, ExecutableName);
            Directory.CreateDirectory(UpdateDirectory());
            File.WriteAllText(source, "new");
            File.WriteAllText(target, "old");
            try
            {
                var service = new ReleaseUpdater(path => path.Equals(source, StringComparison.OrdinalIgnoreCase)
                    ? new Version(2, 0, 0) : new Version(1, 0, 0), cachePath: Path.Combine(root, "apply-cache.json"));
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
                var rejected = false;
                try { service.Apply(source, target, int.MaxValue, new string('0', 64)); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new Exception("Hash mismatch was accepted.");
                service.Apply(source, target, int.MaxValue, hash);
                if (File.ReadAllText(target) != "new") throw new Exception("Single-file upgrade failed.");
            }
            finally { if (File.Exists(source)) File.Delete(source); }
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
                    Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("CodexLimitShow-update-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("拒绝清理非测试目录。");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void SelfTestMetadata(string root, string releaseJson)
    {
        var now = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
        var calls = 0;
        using var github = new HttpClient(new MetadataTestHandler(request =>
        {
            if (request.RequestUri?.AbsoluteUri != $"https://api.github.com/repos/{Repository}/releases/latest")
                throw new Exception("Automatic update check attempted a non-metadata URL.");
            calls++;
            if (calls is 2 or 3 && request.Headers.IfNoneMatch.SingleOrDefault()?.ToString() != "\"release-1\"")
                throw new Exception("Update ETag was not reused.");
            if (calls == 3) throw new HttpRequestException("Simulated offline check.");
            var response = new HttpResponseMessage(calls == 1 ? HttpStatusCode.OK :
                calls == 2 ? HttpStatusCode.NotModified : HttpStatusCode.NotFound);
            if (calls == 1)
            {
                response.Content = new StringContent(releaseJson);
                response.Headers.ETag = new EntityTagHeaderValue("\"release-1\"");
            }
            return response;
        }));
        var cachePath = Path.Combine(root, "check-cache.json");
        var service = new ReleaseUpdater(github: github, cachePath: cachePath, utcNow: () => now);
        if (service.AutomaticCheckDelay != TimeSpan.Zero ||
            service.CheckAutomaticallyAsync(CancellationToken.None).GetAwaiter().GetResult()?.Version != new Version(2, 0, 0))
            throw new Exception("First automatic metadata check failed.");
        service.CheckAutomaticallyAsync(CancellationToken.None).GetAwaiter().GetResult();
        var restarted = new ReleaseUpdater(github: github, cachePath: cachePath, utcNow: () => now);
        if (calls != 1 || restarted.AutomaticCheckDelay != AutomaticCheckInterval ||
            restarted.CheckAutomaticallyAsync(CancellationToken.None).GetAwaiter().GetResult() != service.CachedRelease || calls != 1)
            throw new Exception("Restart or repeated automatic check bypassed the 24-hour interval.");
        now += AutomaticCheckInterval;
        if (restarted.CheckAutomaticallyAsync(CancellationToken.None).GetAwaiter().GetResult()?.Version != new Version(2, 0, 0) || calls != 2)
            throw new Exception("Conditional metadata check lost the cached release.");
        var failed = false;
        try { restarted.CheckRemoteAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (HttpRequestException) { failed = true; }
        var afterFailure = new ReleaseUpdater(github: github, cachePath: cachePath, utcNow: () => now);
        if (!failed || calls != 3 || afterFailure.CachedRelease is null ||
            afterFailure.AutomaticCheckDelay != AutomaticCheckInterval)
            throw new Exception("Failed check lost its candidate or retry timestamp.");
        afterFailure.CheckAutomaticallyAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (calls != 3) throw new Exception("Failed check was retried automatically too soon.");
        if (afterFailure.CheckRemoteAsync(CancellationToken.None).GetAwaiter().GetResult() is not null || calls != 4)
            throw new Exception("Manual checks were throttled or a missing release retained stale metadata.");

        File.WriteAllText(cachePath, JsonSerializer.Serialize(new UpdateCheckCache(now.AddYears(100), "\"bad\"",
            "2.0.0", "https://example.invalid/untrusted.exe", new string('a', 64))));
        var untrusted = new ReleaseUpdater(github: github, cachePath: cachePath, utcNow: () => now);
        if (untrusted.CachedRelease is not null || untrusted.AutomaticCheckDelay != AutomaticCheckInterval)
            throw new Exception("Untrusted cache or future timestamp was accepted.");
        now += AutomaticCheckInterval;
        if (untrusted.AutomaticCheckDelay != TimeSpan.Zero)
            throw new Exception("Future clock timestamp postponed checks indefinitely.");
        File.WriteAllText(cachePath, "not-json");
        var corrupt = new ReleaseUpdater(github: github, cachePath: cachePath, utcNow: () => now);
        if (corrupt.CachedRelease is not null || corrupt.AutomaticCheckDelay != TimeSpan.Zero)
            throw new Exception("Corrupt update cache was not ignored.");
        using var oversizedGithub = new HttpClient(new MetadataTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(new string(' ', MaximumMetadataBytes + 1)) }));
        var oversized = new ReleaseUpdater(github: oversizedGithub, cachePath: Path.Combine(root, "large-cache.json"), utcNow: () => now);
        var rejected = false;
        try { oversized.CheckRemoteAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new Exception("Oversized release metadata was accepted.");
    }

    private sealed class MetadataTestHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
