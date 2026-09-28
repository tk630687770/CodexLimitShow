using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexLimitShow;

internal sealed record LocalReleaseCandidate(Version Version, string Directory, string Executable);
internal sealed record RemoteReleaseCandidate(Version Version, string DownloadUrl, string Sha256);

internal sealed class LocalRelease
{
    public const string ExecutableName = "CodexLimitShow.exe";
    public const string FormalDirectoryName = "正式版";
    private const string MarkerName = "formal.marker";
    private const string Repository = "tk630687770/CodexLimitShow";
    private static readonly HttpClient Github = CreateGithubClient();
    private readonly Func<string, Version?> _readVersion;

    public LocalRelease(Func<string, Version?>? readVersion = null) =>
        _readVersion = readVersion ?? ReadVersion;

    public bool IsFormal(string? executable)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executable)) return false;
            var path = Path.GetFullPath(executable);
            var directory = Path.GetDirectoryName(path)!;
            var marker = Path.Combine(directory, MarkerName);
            return Path.GetFileName(directory).Equals(FormalDirectoryName, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(path).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase) &&
                Version.TryParse(File.ReadAllText(marker).Trim(), out var marked) && _readVersion(path) == marked;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public (Version? Current, LocalReleaseCandidate? Candidate) Check(string executable)
    {
        if (!IsFormal(executable)) return (null, null);
        var current = _readVersion(executable)!;
        var formal = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var versions = Path.Combine(Path.GetDirectoryName(formal)!, "release", "versions");
        if (!Directory.Exists(versions)) return (current, null);
        LocalReleaseCandidate? best = null;
        foreach (var path in Directory.EnumerateDirectories(versions))
        {
            var candidate = Validate(path);
            if (candidate is not null && candidate.Version > current &&
                (best is null || candidate.Version > best.Version)) best = candidate;
        }
        return (current, best);
    }

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
        var fileName = $"CodexLimitShow-{version}-win-x64.zip";
        var expectedUrl = $"https://github.com/{Repository}/releases/download/{tag}/{fileName}";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != fileName ||
                asset.GetProperty("browser_download_url").GetString() != expectedUrl ||
                !asset.TryGetProperty("size", out var size) || size.GetInt64() is <= 0 or > 250_000_000 ||
                !asset.TryGetProperty("digest", out var digest)) continue;
            var hash = digest.GetString();
            if (hash is { Length: 71 } && hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                hash[7..].All(Uri.IsHexDigit))
                return new RemoteReleaseCandidate(version, expectedUrl, hash[7..]);
        }
        return null;
    }

    public async Task<LocalReleaseCandidate> DownloadAsync(RemoteReleaseCandidate remote, string formalDirectory,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(formalDirectory))!;
        var versions = Path.Combine(parent, "release", "versions");
        var target = Path.Combine(versions, $"CodexLimitShow-{remote.Version}-win-x64");
        if (Directory.Exists(target)) return Validate(target) ?? throw new InvalidDataException("本地同版本目录校验失败。");
        Directory.CreateDirectory(versions);
        var staging = Path.Combine(versions, $".download-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            var zip = Path.Combine(staging, "package.zip");
            using (var response = await Github.GetAsync(remote.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = File.Create(zip);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > 250_000_000) throw new InvalidDataException("发布包超出大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            await using (var input = File.OpenRead(zip))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken))
                    .Equals(remote.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("GitHub 发布包校验失败。");
            using (var archive = ZipFile.OpenRead(zip))
            {
                var folder = Path.GetFileName(target).Replace('\\', '/') + "/";
                if (archive.Entries.Count > 20 || archive.Entries.Any(entry =>
                    !entry.FullName.StartsWith(folder, StringComparison.Ordinal) || entry.Length > 250_000_000) ||
                    archive.Entries.Sum(entry => entry.Length) > 300_000_000)
                    throw new InvalidDataException("发布包内容不符合预期。");
                archive.ExtractToDirectory(staging);
            }
            var extracted = Path.Combine(staging, Path.GetFileName(target));
            var candidate = Validate(extracted) ?? throw new InvalidDataException("发布包内程序校验失败。");
            Directory.Move(extracted, target);
            return candidate with { Directory = target, Executable = Path.Combine(target, ExecutableName) };
        }
        finally
        {
            if (Path.GetDirectoryName(staging) == versions && Path.GetFileName(staging).StartsWith(".download-", StringComparison.Ordinal) &&
                Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static HttpClient CreateGithubClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexLimitShow/1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public void Apply(string sourceDirectory, string formalDirectory, int oldProcessId)
    {
        var formal = Path.GetFullPath(formalDirectory);
        var parent = Path.GetDirectoryName(formal)!;
        var source = Path.GetFullPath(sourceDirectory);
        if (!IsFormal(Path.Combine(formal, ExecutableName)) ||
            !Path.GetFullPath(Path.GetDirectoryName(source)!).Equals(
                Path.Combine(parent, "release", "versions"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("升级路径不是本项目正式版和本地版本目录。");
        var candidate = Validate(source) ?? throw new InvalidDataException("新版本校验失败。");
        if (candidate.Version <= _readVersion(Path.Combine(formal, ExecutableName)))
            throw new InvalidOperationException("新版本必须高于当前正式版。");

        try
        {
            using var oldProcess = Process.GetProcessById(oldProcessId);
            if (!oldProcess.WaitForExit(30_000)) throw new TimeoutException("旧版未退出，正式版保持不变。");
        }
        catch (ArgumentException) { /* The old process already exited. */ }

        var staging = Path.Combine(parent, $".正式版-暂存-{Guid.NewGuid():N}");
        var previous = Path.Combine(parent, $".正式版-旧版-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            File.Copy(candidate.Executable, Path.Combine(staging, ExecutableName));
            File.Copy(Path.Combine(source, "release.json"), Path.Combine(staging, "release.json"));
            File.WriteAllText(Path.Combine(staging, MarkerName), candidate.Version.ToString());
            Directory.Move(formal, previous);
            Directory.Move(staging, formal);
        }
        catch
        {
            if (!Directory.Exists(formal) && Directory.Exists(previous)) Directory.Move(previous, formal);
            DeleteOwnTemporaryDirectory(staging, parent);
            throw;
        }
        // The previous executable remains in its numbered release directory for rollback.
        try { DeleteOwnTemporaryDirectory(previous, parent); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private LocalReleaseCandidate? Validate(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var executable = Path.Combine(info.FullName, ExecutableName);
            var manifestPath = Path.Combine(info.FullName, "release.json");
            if (!File.Exists(executable) || !File.Exists(manifestPath) ||
                (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0) return null;
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(manifestPath).TrimStart('\uFEFF'));
            if (manifest is null || !Version.TryParse(manifest.Version, out var version) ||
                !info.Name.Equals($"CodexLimitShow-{version}-win-x64", StringComparison.OrdinalIgnoreCase) ||
                _readVersion(executable) != version || manifest.ExecutableSha256?.Length != 64) return null;
            using var stream = File.OpenRead(executable);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(manifest.ExecutableSha256, StringComparison.OrdinalIgnoreCase)
                ? new LocalReleaseCandidate(version, info.FullName, executable) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    private static Version? ReadVersion(string executable)
    {
        if (!File.Exists(executable)) return null;
        var info = FileVersionInfo.GetVersionInfo(executable);
        if (!Version.TryParse(info.ProductVersion, out var product) ||
            !Version.TryParse(info.FileVersion, out var file) ||
            product.Major != file.Major || product.Minor != file.Minor || product.Build != file.Build) return null;
        return product;
    }

    private static void DeleteOwnTemporaryDirectory(string path, string parent)
    {
        var resolved = Path.GetFullPath(path);
        if (!resolved.StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith(".正式版-", StringComparison.Ordinal))
            throw new InvalidOperationException("拒绝清理非本项目暂存目录。");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }

    private sealed class ReleaseManifest
    {
        public string? Version { get; set; }
        public string? ExecutableSha256 { get; set; }
    }

    public static void SelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CodexLimitShow-release-test-{Guid.NewGuid():N}");
        try
        {
            var formal = Path.Combine(root, FormalDirectoryName);
            var release = Path.Combine(root, "release", "versions", "CodexLimitShow-1.2.0-win-x64");
            Directory.CreateDirectory(formal);
            Directory.CreateDirectory(release);
            var formalExe = Path.Combine(formal, ExecutableName);
            var releaseExe = Path.Combine(release, ExecutableName);
            File.WriteAllText(formalExe, "old");
            File.WriteAllText(releaseExe, "new");
            var service = new LocalRelease(path => path.Equals(formalExe, StringComparison.OrdinalIgnoreCase)
                ? new Version(1, 1, 0) : new Version(1, 2, 0));
            File.WriteAllText(Path.Combine(release, "release.json"), JsonSerializer.Serialize(new ReleaseManifest
            {
                Version = "1.2.0", ExecutableSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(releaseExe)))
            }));
            if (service.IsFormal(formalExe)) throw new Exception("Unmarked install showed upgrade button.");
            File.WriteAllText(Path.Combine(formal, MarkerName), "1.1.0");
            if (service.Check(formalExe).Candidate?.Version != new Version(1, 2, 0) || service.IsFormal(releaseExe))
                throw new Exception("Formal release selection failed.");
            File.AppendAllText(releaseExe, "tampered");
            if (service.Check(formalExe).Candidate is not null) throw new Exception("Tampered release was accepted.");
            File.WriteAllText(releaseExe, "new");
            var remoteJson = JsonSerializer.Serialize(new
            {
                tag_name = "v1.3.0", draft = false, prerelease = false,
                assets = new[] { new {
                    name = "CodexLimitShow-1.3.0-win-x64.zip",
                    browser_download_url = $"https://github.com/{Repository}/releases/download/v1.3.0/CodexLimitShow-1.3.0-win-x64.zip",
                    size = 70_000_000,
                    digest = "sha256:" + new string('a', 64)
                } }
            });
            using var remoteDocument = JsonDocument.Parse(remoteJson);
            if (ParseRemoteRelease(remoteDocument.RootElement)?.Version != new Version(1, 3, 0))
                throw new Exception("GitHub release parsing failed.");
            service.Apply(release, formal, int.MaxValue);
            if (File.ReadAllText(formalExe) != "new" || File.ReadAllText(Path.Combine(formal, MarkerName)) != "1.2.0" ||
                !File.Exists(releaseExe)) throw new Exception("Formal upgrade did not preserve the release version.");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
                    Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("CodexLimitShow-release-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("拒绝清理非测试目录。");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
