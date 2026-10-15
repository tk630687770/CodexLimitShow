using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexLimitShow;

internal sealed record RemoteReleaseCandidate(Version Version, string DownloadUrl, string Sha256, string? Signature = null);
internal readonly record struct DownloadProgress(long Received, long? Total, bool Verifying, string? Status = null, bool Mirror = false)
{
    public int? Percent => Total is > 0 ? (int)Math.Clamp(Received * 100 / Total.Value, 0, 100) : null;
    public string Route => Mirror ? "备用线路" : "GitHub";
    public string Description => Verifying ? $"正在校验下载文件 · {Route}" : Total is > 0
        ? $"下载中 {Percent}% · {Received / 1_048_576} / {Total.Value / 1_048_576} MB · {Route}"
        : $"下载中 · 已接收 {Received / 1_048_576} MB · {Route}";
}

internal sealed class ReleaseUpdater
{
    public const string ExecutableName = "CodexLimitShow.exe";
    private const string Repository = "tk630687770/CodexLimitShow";
    private const long MaximumBytes = 250_000_000;
    private const int MaximumMetadataBytes = 1_048_576;
    public const string SigningPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAELnOFh9OIaXLiikxWGfiqxXxC2cPOUCUE419FlvKa/4rqa6B8HLMDIEesHHHzJG3qi14FLIFsitjFgEu3l6hX0g==";
    private const string MirrorPrefix = "https://gh-proxy.org/";
    private static readonly HttpClient Github = CreateGithubClient();
    private readonly Func<string, Version?> _readVersion;
    private readonly HttpClient _github;
    private readonly string _cachePath;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly byte[] _verificationKey;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private DateTimeOffset? _lastAttemptUtc;
    private string? _etag;

    private sealed record UpdateCheckCache(DateTimeOffset LastAttemptUtc, string? ETag,
        string? Version, string? DownloadUrl, string? Sha256, string? Signature = null);

    public static TimeSpan AutomaticCheckInterval => TimeSpan.FromHours(24);
    public RemoteReleaseCandidate? CachedRelease { get; private set; }
    public bool MetadataViaMirror { get; private set; }
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
        string? cachePath = null, Func<DateTimeOffset>? utcNow = null, byte[]? verificationKey = null)
    {
        _readVersion = readVersion ?? ReadVersion;
        _github = github ?? Github;
        _cachePath = cachePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLimitShow", "update-check.json");
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _verificationKey = verificationKey ?? Convert.FromBase64String(SigningPublicKey);
        LoadCheckCache();
    }

    public Version? CurrentVersion(string? executable) =>
        string.IsNullOrWhiteSpace(executable) ? null : _readVersion(executable);

    public Task<RemoteReleaseCandidate?> CheckRemoteAsync(CancellationToken cancellationToken, IProgress<string>? status = null) =>
        CheckAsync(automatic: false, cancellationToken, status);

    public Task<RemoteReleaseCandidate?> CheckAutomaticallyAsync(CancellationToken cancellationToken) =>
        CheckAsync(automatic: true, cancellationToken);

    private async Task<RemoteReleaseCandidate?> CheckAsync(bool automatic, CancellationToken cancellationToken,
        IProgress<string>? status = null)
    {
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (automatic && AutomaticCheckDelay > TimeSpan.Zero) return CachedRelease;
            _lastAttemptUtc = _utcNow();
            SaveCheckCache();
            try { await ReadMetadataAsync(mirror: false, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
            {
                status?.Report("GitHub 直连失败，正在切换备用线路检查版本…");
                await ReadMetadataAsync(mirror: true, cancellationToken).ConfigureAwait(false);
            }
            SaveCheckCache();
            return CachedRelease;
        }
        finally { _checkGate.Release(); }
    }

    private async Task ReadMetadataAsync(bool mirror, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            (mirror ? MirrorPrefix : "") + $"https://api.github.com/repos/{Repository}/releases/latest");
        // ETags belong to the direct origin; never let a mirror's 304 bypass signature verification.
        if (!mirror && EntityTagHeaderValue.TryParse(_etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
        using var response = await _github.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
            .ConfigureAwait(false);
        if (!mirror && response.StatusCode == HttpStatusCode.NotModified) { MetadataViaMirror = false; return; }
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
            await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
            using var payload = new MemoryStream();
            var buffer = new byte[16_384];
            int read;
            while ((read = await stream.ReadAsync(buffer, attempt.Token).ConfigureAwait(false)) > 0)
            {
                if (payload.Length + read > MaximumMetadataBytes)
                    throw new InvalidDataException("GitHub 版本信息超出大小限制。");
                payload.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(payload.GetBuffer().AsMemory(0, (int)payload.Length));
            CachedRelease = ParseRemoteRelease(document.RootElement, mirror, _verificationKey);
            _etag = mirror ? null : response.Headers.ETag?.ToString();
        }
        MetadataViaMirror = mirror;
    }

    private static bool IsNetworkFailure(Exception error, CancellationToken token) => !token.IsCancellationRequested &&
        (error is OperationCanceledException or HttpIOException ||
         error is HttpRequestException http && (http.StatusCode is null or HttpStatusCode.Forbidden or
             HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)http.StatusCode.Value >= 500));

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
                var remote = new RemoteReleaseCandidate(version, cache.DownloadUrl, cache.Sha256, cache.Signature);
                if (remote.Signature is not null && !VerifySignature(remote, _verificationKey)) return;
                CachedRelease = remote;
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
                CachedRelease?.DownloadUrl, CachedRelease?.Sha256, CachedRelease?.Signature);
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

    private static RemoteReleaseCandidate? ParseRemoteRelease(JsonElement release, bool requireSignature = false,
        byte[]? verificationKey = null)
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
            {
                var body = release.TryGetProperty("body", out var description) ? description.GetString() ?? "" : "";
                var signatures = Regex.Matches(body, @"<!-- CodexLimitShow-Signature:v1:([A-Za-z0-9+/=]+) -->");
                var signature = signatures.Count == 1 ? signatures[0].Groups[1].Value : null;
                var remote = new RemoteReleaseCandidate(version, expectedUrl, hash[7..], signature);
                if (requireSignature || body.Contains("CodexLimitShow-Signature", StringComparison.Ordinal))
                {
                    if (signature is null || !VerifySignature(remote, verificationKey ?? Convert.FromBase64String(SigningPublicKey)))
                        throw new InvalidDataException("发布签名缺失或无效，拒绝从备用线路升级。旧版需先从可直连电脑复制一次新版。");
                }
                return remote;
            }
        }
        return null;
    }

    private static byte[] SignaturePayload(RemoteReleaseCandidate remote) =>
        Encoding.UTF8.GetBytes($"{Repository}\nv{remote.Version}\n{remote.Sha256.ToLowerInvariant()}");

    private static bool VerifySignature(RemoteReleaseCandidate remote, byte[] publicKey)
    {
        try
        {
            if (remote.Signature is not { Length: 88 }) return false;
            var signature = Convert.FromBase64String(remote.Signature);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out _);
            return signature.Length == 64 && verifier.VerifyData(SignaturePayload(remote), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }

    public async Task<string> DownloadAsync(RemoteReleaseCandidate remote, IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (remote.DownloadUrl != $"https://github.com/{Repository}/releases/download/v{remote.Version}/{ExecutableName}" ||
            remote.Sha256.Length != 64 || !remote.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("拒绝下载非本仓库发布文件。");
        var directory = UpdateDirectory();
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"CodexLimitShow-update-{Guid.NewGuid():N}.exe");
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromMinutes(10));
            var mirror = MetadataViaMirror;
            try { await DownloadRouteAsync(remote, destination, mirror, progress, budget.Token).ConfigureAwait(false); }
            catch (Exception ex) when (!mirror && IsNetworkFailure(ex, budget.Token))
            {
                if (File.Exists(destination)) File.Delete(destination);
                progress?.Report(new DownloadProgress(0, null, false, "GitHub 下载失败，正在切换备用线路重新下载…", Mirror: true));
                await DownloadRouteAsync(remote, destination, true, progress, budget.Token).ConfigureAwait(false);
            }
            return destination;
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
    }

    private async Task DownloadRouteAsync(RemoteReleaseCandidate remote, string destination, bool mirror,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await _github.GetAsync((mirror ? MirrorPrefix : "") + remote.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead, attempt.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var expectedBytes = response.Content.Headers.ContentLength;
        if (expectedBytes > MaximumBytes) throw new InvalidDataException("发布程序超出大小限制。");
        if (expectedBytes is <= 0) expectedBytes = null;
        progress?.Report(new DownloadProgress(0, expectedBytes, false, Mirror: mirror));
        long total = 0;
        await using (var source = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long lastReportedBytes = 0;
            var lastPercent = 0;
            int read;
            while (true)
            {
                attempt.CancelAfter(TimeSpan.FromSeconds(30));
                read = await source.ReadAsync(buffer, attempt.Token).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > MaximumBytes) throw new InvalidDataException("发布程序超出大小限制。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                var current = new DownloadProgress(total, expectedBytes, false, Mirror: mirror);
                if ((current.Percent is { } percent && percent > lastPercent) ||
                    (expectedBytes is null && total - lastReportedBytes >= 1_048_576))
                {
                    progress?.Report(current);
                    lastPercent = current.Percent ?? lastPercent;
                    lastReportedBytes = total;
                }
            }
        }
        progress?.Report(new DownloadProgress(total, expectedBytes, true, Mirror: mirror));
        if (expectedBytes is not null && total != expectedBytes) throw new HttpRequestException("下载未完成。");
        if (!HashMatches(destination, remote.Sha256) || _readVersion(destination) != remote.Version)
            throw new InvalidDataException("GitHub 发布程序校验失败。");
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
        })
        { Timeout = TimeSpan.FromMinutes(10) };
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

    internal static async Task VerifyMirrorAsync(TextWriter output)
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"CodexLimitShow-mirror-probe-{Guid.NewGuid():N}.json");
        string? downloaded = null;
        try
        {
            using var client = new HttpClient(new MirrorProbeHandler());
            var service = new ReleaseUpdater(github: client, cachePath: cachePath);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var remote = await service.CheckRemoteAsync(timeout.Token) ?? throw new InvalidDataException("没有可验证的正式版。");
            if (!service.MetadataViaMirror || remote.Signature is null)
                throw new InvalidDataException("诊断必须使用签名验证通过的备用元数据。");
            output.WriteLine($"mirror metadata: v{remote.Version}; release signature verified; direct API and EXE routes simulated blocked");
            downloaded = await service.DownloadAsync(remote, new TestProgress<DownloadProgress>(value =>
            {
                if (value.Status is not null) output.WriteLine(value.Status);
                if (value.Verifying) output.WriteLine(value.Description + "; verifying hash and EXE product/file version");
            }), CancellationToken.None);
            output.WriteLine($"PASS: real mirror metadata and download v{remote.Version} verified with pinned signature, SHA-256 and EXE versions; not executed or installed");
        }
        finally
        {
            if (downloaded is not null) File.Delete(downloaded);
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
    }

    private sealed class MirrorProbeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host is "github.com" or "api.github.com")
                throw new HttpRequestException("Simulated blocked direct API/EXE routes for the read-only mirror probe.");
            return Github.GetAsync(request.RequestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
    }

    public static void SelfTest()
    {
        var releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "v2.0.0",
            draft = false,
            prerelease = false,
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
        if (!new DownloadProgress(50, 100, false, Mirror: true).Description.Contains("50%") ||
            !new DownloadProgress(50, 100, false, Mirror: true).Description.EndsWith("备用线路", StringComparison.Ordinal) ||
            !new DownloadProgress(50, null, false).Description.EndsWith("GitHub", StringComparison.Ordinal) ||
            !new DownloadProgress(100, 100, true, Mirror: true).Description.Contains("校验"))
            throw new Exception("Download status must retain its route during transfer and verification.");
        var root = Path.Combine(Path.GetTempPath(), $"CodexLimitShow-update-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            SelfTestMetadata(root, releaseJson);
            SelfTestFallback(root, releaseJson);
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
            if (request.RequestUri?.AbsoluteUri.StartsWith(MirrorPrefix, StringComparison.Ordinal) == true)
                throw new HttpRequestException("Simulated offline mirror.");
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
        restarted.MetadataViaMirror = true;
        if (restarted.CheckAutomaticallyAsync(CancellationToken.None).GetAwaiter().GetResult()?.Version != new Version(2, 0, 0) ||
            calls != 2 || restarted.MetadataViaMirror)
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

    private static void SelfTestFallback(string root, string releaseJson)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = signer.ExportSubjectPublicKeyInfo();
        var bytes = Encoding.UTF8.GetBytes("test release executable");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var remote = new RemoteReleaseCandidate(new Version(2, 0, 0),
            $"https://github.com/{Repository}/releases/download/v2.0.0/{ExecutableName}", hash);
        var signature = Convert.ToBase64String(signer.SignData(SignaturePayload(remote), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var json = System.Text.Json.Nodes.JsonNode.Parse(releaseJson)!;
        json["assets"]![0]!["digest"] = "sha256:" + hash;
        json["body"] = $"<!-- CodexLimitShow-Signature:v1:{signature} -->";
        var signedJson = json.ToJsonString();
        var badSignatureJson = System.Text.Json.Nodes.JsonNode.Parse(signedJson)!;
        badSignatureJson["body"] = $"<!-- CodexLimitShow-Signature:v1:{Convert.ToBase64String(new byte[64])} -->";
        using (var valid = JsonDocument.Parse(signedJson))
            if (!VerifySignature(ParseRemoteRelease(valid.RootElement, true, key)!, key))
                throw new Exception("Valid mirrored signature was rejected.");
        foreach (var tampered in new[] { signedJson.Replace("v2.0.0", "v2.0.1"),
            signedJson.Replace(hash, new string('b', 64)), releaseJson,
            badSignatureJson.ToJsonString() })
        {
            using var invalid = JsonDocument.Parse(tampered);
            var rejected = false;
            try { rejected = ParseRemoteRelease(invalid.RootElement, true, key) is null; }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("Unsigned or tampered mirrored release was accepted.");
        }

        var calls = new List<string>();
        var messages = new List<string>();
        var downloadEvents = new List<DownloadProgress>();
        var metadataUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
        var brokenDownload = false;
        var directMetadataFails = true;
        var mirrorDownloadOffline = false;
        using var client = new HttpClient(new MetadataTestHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            calls.Add(url);
            if (request.Headers.Authorization is not null || request.Headers.Contains("Cookie"))
                throw new Exception("Credentials were sent to an update endpoint.");
            if (url == metadataUrl)
            {
                if (directMetadataFails) throw new TaskCanceledException("Simulated direct connection timeout.");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signedJson) };
            }
            if (url == MirrorPrefix + metadataUrl)
            {
                if (request.Headers.IfNoneMatch.Any()) throw new Exception("Direct ETag was sent to a mirror.");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signedJson) };
            }
            if (url == remote.DownloadUrl)
            {
                var partial = new ByteArrayContent(bytes[..3]);
                partial.Headers.ContentLength = bytes.Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = partial };
            }
            if (url == MirrorPrefix + remote.DownloadUrl)
            {
                if (mirrorDownloadOffline) throw new HttpRequestException("Simulated mirror unavailable.");
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(brokenDownload ? Encoding.UTF8.GetBytes("tampered") : bytes) };
            }
            throw new Exception("Unexpected update URL: " + url);
        }));
        var cachePath = Path.Combine(root, "mirror-cache.json");
        var service = new ReleaseUpdater(_ => remote.Version, client, cachePath, verificationKey: key);
        var result = service.CheckRemoteAsync(CancellationToken.None, new TestProgress<string>(messages.Add)).GetAwaiter().GetResult();
        if (result?.Signature != signature || calls.Count != 2 || messages.Count != 1 || !service.MetadataViaMirror)
            throw new Exception("Metadata did not fall back once after a direct timeout.");
        var restarted = new ReleaseUpdater(github: client, cachePath: cachePath, verificationKey: key);
        restarted.CheckAutomaticallyAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (calls.Count != 2 || restarted.CachedRelease != result || restarted.MetadataViaMirror ||
            restarted.AutomaticCheckDelay < TimeSpan.FromHours(23))
            throw new Exception("Mirrored signed cache did not survive restart and throttle checks.");
        var destination = service.DownloadAsync(result!, new TestProgress<DownloadProgress>(downloadEvents.Add),
            CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            if (!File.ReadAllBytes(destination).SequenceEqual(bytes) || calls.Count != 3 ||
                downloadEvents.Any(e => e.Status is not null || !e.Mirror) || !downloadEvents[^1].Verifying)
                throw new Exception("A successful mirrored metadata check must avoid retrying the known-failed direct download route.");
        }
        finally { File.Delete(destination); }
        mirrorDownloadOffline = true;
        var beforeOfflineCalls = calls.Count;
        var mirrorFailureReported = false;
        try { service.DownloadAsync(result!, null, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (HttpRequestException) { mirrorFailureReported = true; }
        if (!mirrorFailureReported || calls.Count != beforeOfflineCalls + 1)
            throw new Exception("A failed selected mirror must not retry the already-failed direct route.");
        mirrorDownloadOffline = false;
        brokenDownload = true;
        var beforeBrokenCalls = calls.Count;
        var before = Directory.GetFiles(UpdateDirectory()).Order().ToArray();
        var badFileRejected = false;
        try { service.DownloadAsync(result!, null, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (InvalidDataException) { badFileRejected = true; }
        if (!badFileRejected || calls.Count != beforeBrokenCalls + 1 ||
            !Directory.GetFiles(UpdateDirectory()).Order().SequenceEqual(before))
            throw new Exception("Tampered mirror download was accepted or left a partial executable behind.");
        brokenDownload = false;
        directMetadataFails = false;
        service.CheckRemoteAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (service.MetadataViaMirror || calls[^1] != metadataUrl)
            throw new Exception("A recovered direct metadata check must restore direct download preference.");
        var beforePartialCalls = calls.Count;
        downloadEvents.Clear();
        destination = service.DownloadAsync(result!, new TestProgress<DownloadProgress>(downloadEvents.Add),
            CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            if (calls.Count != beforePartialCalls + 2 || downloadEvents.Count(e => e.Status is not null) != 1 ||
                !downloadEvents.Any(e => !e.Mirror) || !downloadEvents[^1].Mirror ||
                !File.ReadAllBytes(destination).SequenceEqual(bytes))
                throw new Exception("A partial direct download must still restart on the backup route with correct progress labels.");
        }
        finally { File.Delete(destination); }
        var wrongVersion = new ReleaseUpdater(_ => new Version(2, 0, 1), client,
            Path.Combine(root, "wrong-version.json"), verificationKey: key);
        var wrongVersionRejected = false;
        try { wrongVersion.DownloadAsync(result!, null, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (InvalidDataException) { wrongVersionRejected = true; }
        if (!wrongVersionRejected || !Directory.GetFiles(UpdateDirectory()).Order().SequenceEqual(before))
            throw new Exception("Mirror EXE with a mismatched product/file version was accepted.");
        var callCount = calls.Count;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { service.CheckRemoteAsync(cancelled.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        if (calls.Count != callCount) throw new Exception("Explicit cancellation triggered another update route.");
        var unexpectedRoutes = 0;
        using (var corruptDirect = new HttpClient(new MetadataTestHandler(_ =>
        {
            unexpectedRoutes++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 1, 2]) };
        })))
        {
            var corruptService = new ReleaseUpdater(_ => remote.Version, corruptDirect, Path.Combine(root, "corrupt-download.json"));
            var corruptRejected = false;
            try { corruptService.DownloadAsync(remote, null, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { corruptRejected = true; }
            if (!corruptRejected || unexpectedRoutes != 1)
                throw new Exception("An invalid direct download triggered an unsafe security fallback.");
        }

        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            var attempts = 0;
            using var failing = new HttpClient(new MetadataTestHandler(request =>
            {
                attempts++;
                return new HttpResponseMessage(attempts == 1 ? status : HttpStatusCode.OK)
                { Content = new StringContent(signedJson) };
            }));
            var failover = new ReleaseUpdater(github: failing, cachePath: Path.Combine(root, $"http-{status}.json"), verificationKey: key);
            if (failover.CheckRemoteAsync(CancellationToken.None).GetAwaiter().GetResult()?.Signature != signature || attempts != 2)
                throw new Exception("Retryable HTTP failure did not use the signed backup route.");
        }
        // A signature failure is not a network failure and must not trigger another route.
        if (IsNetworkFailure(new InvalidDataException(), CancellationToken.None) ||
            IsNetworkFailure(new IOException("Disk write failed."), CancellationToken.None))
            throw new Exception("Security validation or local disk failure was classified as retryable.");
        var cache = JsonSerializer.Deserialize<UpdateCheckCache>(File.ReadAllText(cachePath))!;
        File.WriteAllText(cachePath, JsonSerializer.Serialize(cache with { Signature = Convert.ToBase64String(new byte[64]) }));
        if (new ReleaseUpdater(github: client, cachePath: cachePath, verificationKey: key).CachedRelease is not null)
            throw new Exception("Tampered signed update cache was accepted.");
    }

    private sealed class TestProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class MetadataTestHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
