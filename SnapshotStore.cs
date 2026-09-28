using System.Text.Json;

namespace CodexLimitShow;

internal sealed record SavedAccountSnapshot(
    string StorageKey,
    string AccountLabel,
    string PlanType,
    DateTimeOffset? SubscriptionExpiresAt,
    RateWindow? FiveHour,
    RateWindow? Weekly,
    IReadOnlyList<RateWindow> OtherWindows,
    long? AvailableResetCredits,
    IReadOnlyList<ResetCredit> ResetCredits,
    UsageStats? Usage,
    DateTimeOffset SavedAt,
    DateTimeOffset? SubscriptionCheckedAt = null)
{
    public static SavedAccountSnapshot From(QuotaSnapshot snapshot) => new(
        snapshot.Account!.StorageKey,
        snapshot.Account.Label,
        snapshot.RateLimitPlanType ?? snapshot.Account.PlanType,
        snapshot.Account.SubscriptionExpiresAt,
        snapshot.FiveHour,
        snapshot.Weekly,
        snapshot.OtherWindows,
        snapshot.AvailableResetCredits,
        snapshot.ResetCredits,
        snapshot.Usage,
        snapshot.FetchedAt,
        snapshot.Account.SubscriptionCheckedAt);
}

internal sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _filePath;

    public SnapshotStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLimitShow",
            "snapshots.json");
    }

    public bool Save(QuotaSnapshot snapshot)
    {
        if (snapshot.Account is null)
            return false;

        lock (_sync)
        {
            try
            {
                if (!TryLoad(out var entries))
                    return false;

                var old = entries.FirstOrDefault(entry => entry.StorageKey == snapshot.Account.StorageKey);
                entries.RemoveAll(entry => entry.StorageKey == snapshot.Account.StorageKey);
                var updated = SavedAccountSnapshot.From(snapshot);
                if (updated.SubscriptionExpiresAt is null && old?.SubscriptionExpiresAt is not null)
                    updated = updated with { SubscriptionExpiresAt = old.SubscriptionExpiresAt,
                        SubscriptionCheckedAt = old.SubscriptionCheckedAt };
                entries.Add(updated);
                entries.Sort((left, right) => right.SavedAt.CompareTo(left.SavedAt));

                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var temporaryPath = _filePath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
                File.Move(temporaryPath, _filePath, overwrite: true);
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return false;
            }
        }
    }

    public IReadOnlyList<SavedAccountSnapshot> Load()
    {
        lock (_sync)
            return TryLoad(out var entries) ? entries : [];
    }

    private bool TryLoad(out List<SavedAccountSnapshot> entries)
    {
        entries = [];
        if (!File.Exists(_filePath))
            return true;

        try
        {
            entries = JsonSerializer.Deserialize<List<SavedAccountSnapshot>>(
                File.ReadAllText(_filePath),
                JsonOptions) ?? [];
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    public static void SelfTest()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CodexLimitShow-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "snapshots.json");
        try
        {
            var store = new SnapshotStore(path);
            var snapshot = new QuotaSnapshot(
                new RateWindow(300, 40, null),
                new RateWindow(10080, 20, null),
                [],
                1,
                [new ResetCredit("Full reset", "available", DateTimeOffset.Now, null)],
                new UsageStats(123, 456),
                DateTimeOffset.Now,
                new AccountSummary("ABC123", "alice@example.com", "plus", null));

            Check(store.Save(snapshot));
            Check(store.Load() is [{ AccountLabel: "alice@example.com", PlanType: "plus" }]);
            var expiry = DateTimeOffset.Now.AddDays(7);
            Check(store.Save(snapshot with { Account = snapshot.Account! with { SubscriptionExpiresAt = expiry } }));
            Check(store.Save(snapshot));
            Check(store.Load()[0].SubscriptionExpiresAt == expiry);
            Check(!File.ReadAllText(path).Contains("\"id\"", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void Check(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Snapshot store self-test failed.");
    }
}
