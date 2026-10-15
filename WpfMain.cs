using System.Windows;
using System.Diagnostics;

namespace CodexLimitShow;

internal static class WpfMain
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The WPF surface and WinForms tray must share the same process DPI context.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        if (args.Contains("--check-update-mirror", StringComparer.OrdinalIgnoreCase))
        {
            using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            try { ReleaseUpdater.VerifyMirrorAsync(output).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                output.WriteLine("备用升级诊断失败\n" + ReleaseUpdater.DescribeFailure(ex, "备用线路诊断"));
                Environment.ExitCode = 1;
            }
            return;
        }
        if (args.Length > 0 && args[0] == "--apply-update")
        {
            var stage = "校验并替换程序";
            try
            {
                if (args.Length != 5 || !int.TryParse(args[3], out var oldProcessId) ||
                    !Path.GetFullPath(Environment.ProcessPath ?? string.Empty).Equals(
                        Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("升级参数无效。");
                new ReleaseUpdater().Apply(args[1], args[2], oldProcessId, args[4]);
                stage = "启动新版程序";
                Process.Start(new ProcessStartInfo(args[2])
                    { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[2]))! });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"升级未完成\n\n{ReleaseUpdater.DescribeFailure(ex, stage)}\n\n如果程序已替换，可从 GitHub 历史 Release 取回旧版。",
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
            TaskbarQuotaWindow.SelfTest();
            SubscriptionLookup.SelfTest();
            SubscriptionBrowser.SelfTest();
            ReleaseUpdater.SelfTest();
            var resetJson = System.Text.Json.JsonSerializer.Serialize(new ResetConsumeParameters("test-key"));
            if (!resetJson.Contains("idempotencyKey") || resetJson.Contains("creditId"))
                throw new InvalidOperationException("Reset request must contain only the idempotency key.");
            return;
        }
        if (args.Contains("--check-web-subscription", StringComparer.OrdinalIgnoreCase))
        {
            using var client = new CodexAppServerClient();
            using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            AccountSummary? account;
            try { account = client.ReadCurrentAccountAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) { output.WriteLine("web-subscription: account check failed; " + ex.GetType().Name); return; }
            if (account is null) { output.WriteLine("web-subscription: account unavailable"); return; }
            var checkApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var browser = new SubscriptionBrowser(account.Label, checkOnly: true);
            output.WriteLine(browser.ShowDialog() == true && browser.Result is { } result
                ? $"web-subscription: account matched twice; date={result.Date:yyyy-MM-dd}; no snapshot written"
                : "web-subscription: not verified; no snapshot written; " + (browser.Failure ?? "window closed"));
            checkApp.Shutdown();
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
        var diagnoseSubscription = args.Contains("--diagnose-subscription", StringComparer.OrdinalIgnoreCase);
        if (diagnoseSubscription || args.Contains("--probe-subscription", StringComparer.OrdinalIgnoreCase))
        {
            using var client = new CodexAppServerClient();
            using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            try
            {
                var snapshot = client.ReadSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (snapshot.Account is null) throw new CodexClientException("当前账号不可用");
                var expiry = new SubscriptionLookup().ReadExpiryAsync(snapshot.Account, CancellationToken.None,
                    diagnoseSubscription ? output.WriteLine : null).GetAwaiter().GetResult();
                output.WriteLine(expiry is null ? "subscription: unavailable" : $"subscription-online: {expiry:yyyy-MM-dd HH:mm zzz}");
            }
            catch (Exception ex)
            {
                if (diagnoseSubscription && ex is HttpRequestException httpError)
                    output.WriteLine($"subscription-transport: error={httpError.HttpRequestError}; status={(httpError.StatusCode is { } status ? ((int)status).ToString() : "none")}");
                output.WriteLine($"subscription-error: {(!diagnoseSubscription && ex is CodexClientException ? ex.Message : ex.GetType().Name)}");
            }
            return;
        }
        if (args.Contains("--render-preview", StringComparer.OrdinalIgnoreCase))
        {
            var previewApp = new Application();
            var widget = new GlassWidget(preview: true);
            widget.RenderPreview();
            TaskbarQuotaWindow.RenderPreview(Environment.CurrentDirectory);
            widget.Close();
            return;
        }
        if (args.Contains("--taskbar-test", StringComparer.OrdinalIgnoreCase))
        {
            var testApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var widget = new GlassWidget(preview: true);
            var started = false;
            widget.Loaded += async (_, _) =>
            {
                if (started) return;
                started = true;
                var exitCode = 0;
                try { await widget.VerifyTaskbarAsync(); }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "taskbar-test.txt"), ex.ToString());
                    exitCode = 1;
                }
                finally { widget.Close(); testApp.Shutdown(exitCode); }
            };
            Environment.ExitCode = testApp.Run(widget);
            return;
        }
        ReleaseUpdater.CleanupOldDownloads();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new GlassWidget());
    }
}
