using System.Diagnostics;
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
    private static readonly HttpClient Github = CreateGithubClient();
    private readonly Func<string, Version?> _readVersion;

    public ReleaseUpdater(Func<string, Version?>? readVersion = null) =>
        _readVersion = readVersion ?? ReadVersion;

    public Version? CurrentVersion(string? executable) =>
        string.IsNullOrWhiteSpace(executable) ? null : _readVersion(executable);

    public async Task<RemoteReleaseCandidate?> CheckRemoteAsync(CancellationToken cancellationToken)
    {
        using var response = await Github.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseRemoteRelease(document.RootElement);
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
            using var response = await Github.GetAsync(remote.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
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
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
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
            var source = Path.Combine(UpdateDirectory(), $"CodexLimitShow-update-{Guid.NewGuid():N}.exe");
            var target = Path.Combine(root, ExecutableName);
            Directory.CreateDirectory(UpdateDirectory());
            File.WriteAllText(source, "new");
            File.WriteAllText(target, "old");
            try
            {
                var service = new ReleaseUpdater(path => path.Equals(source, StringComparison.OrdinalIgnoreCase)
                    ? new Version(2, 0, 0) : new Version(1, 0, 0));
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
}
