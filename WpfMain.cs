using System.Windows;
using System.Diagnostics;

namespace CodexLimitShow;

internal static class WpfMain
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--apply-update")
        {
            try
            {
                if (args.Length != 5 || !int.TryParse(args[3], out var oldProcessId) ||
                    !Path.GetFullPath(Environment.ProcessPath ?? string.Empty).Equals(
                        Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("升级参数无效。");
                new ReleaseUpdater().Apply(args[1], args[2], oldProcessId, args[4]);
                Process.Start(new ProcessStartInfo(args[2])
                    { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[2]))! });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"升级失败；如果程序已替换，可从 GitHub 历史 Release 取回旧版。\n{ex.Message}",
                    "CodexLimitShow", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            CodexAppServerClient.SelfTestDiscovery();
            QuotaParser.SelfTest();
            SnapshotStore.SelfTest();
            RingGeometry.SelfTest();
            SubscriptionLookup.SelfTest();
            ReleaseUpdater.SelfTest();
            var resetJson = System.Text.Json.JsonSerializer.Serialize(new ResetConsumeParameters("test-key"));
            if (!resetJson.Contains("idempotencyKey") || resetJson.Contains("creditId"))
                throw new InvalidOperationException("Reset request must contain only the idempotency key.");
            return;
        }
        if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
        {
            using var client = new CodexAppServerClient();
            var snapshot = client.ReadSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
            using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            output.WriteLine($"5h={snapshot.FiveHour?.RemainingPercent.ToString() ?? "inactive"}%; weekly={snapshot.Weekly?.RemainingPercent.ToString() ?? "inactive"}%; credits={snapshot.AvailableResetCredits?.ToString() ?? "unknown"}");
            return;
        }
        if (args.Contains("--probe-subscription", StringComparer.OrdinalIgnoreCase))
        {
            using var client = new CodexAppServerClient();
            try
            {
                var snapshot = client.ReadSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (snapshot.Account is null) throw new CodexClientException("当前账号不可用");
                var expiry = new SubscriptionLookup().ReadExpiryAsync(snapshot.Account, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(expiry is null ? "subscription: unavailable" : $"subscription-local: {expiry:yyyy-MM-dd HH:mm zzz}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"subscription-error: {(ex is CodexClientException ? ex.Message : ex.GetType().Name)}");
            }
            return;
        }
        if (args.Contains("--render-preview", StringComparer.OrdinalIgnoreCase))
        {
            var previewApp = new Application();
            var widget = new GlassWidget(preview: true);
            widget.RenderPreview();
            widget.Close();
            return;
        }
        ReleaseUpdater.CleanupOldDownloads();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new GlassWidget());
    }
}
