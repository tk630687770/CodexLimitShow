using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SubscriptionBrowserProbe;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--self-test")) { ProbeWindow.SelfTest(); return; }
        new Application().Run(new ProbeWindow());
    }
}

internal sealed class ProbeWindow : Window
{
    private const string BillingUrl = "https://chatgpt.com/settings/billing";
    private readonly WebView2 _browser = new();
    private readonly TextBlock _address = new() { Text = "浏览器准备中…", Margin = new Thickness(12, 6, 12, 6) };
    private readonly TextBlock _result = new() { Text = "请自行登录，然后打开账单页并点击“读取订阅文字”。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
    private readonly Button _read = new() { Content = "读取订阅文字", IsEnabled = false, Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _billing = new() { Content = "打开账单页", IsEnabled = false, Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6) };
    private bool _closing;

    // Read rendered billing text only; never inspect authentication storage or send API requests.
    private const string ReadScript = """
        (() => {
          if (location.protocol !== 'https:' || location.hostname !== 'chatgpt.com' ||
              location.pathname.replace(/\/$/, '') !== '/settings/billing')
            return { allowed: false, lines: [] };
          const lines = [...document.querySelectorAll('p')]
            .filter(p => p.getClientRects().length && getComputedStyle(p).visibility !== 'hidden')
            .map(p => p.innerText.trim())
            .filter(text => /^(?:你的套餐|您的套餐|Your plan|Your subscription)\b/i.test(text) ||
                            /^(?:你的套餐|您的套餐)/.test(text))
            .filter(text => text.length <= 300 && !text.includes('@'));
          return { allowed: true, lines: [...new Set(lines)] };
        })()
        """;

    internal ProbeWindow()
    {
        Title = "订阅网页核验 · 独立验证工具（不写入组件）";
        Width = 1060; Height = 800; MinWidth = 720; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(new TextBlock
        {
            Text = "独立会话，可跨重启复用。请亲自登录／完成验证；不读取 Cookie、令牌或付款信息。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 10, 12, 4)
        });
        top.Children.Add(_address);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 4) };
        actions.Children.Add(_billing); actions.Children.Add(_read); top.Children.Add(actions);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(_result, Dock.Bottom); root.Children.Add(_result);
        root.Children.Add(_browser); Content = root;
        _billing.Click += (_, _) => _browser.CoreWebView2.Navigate(BillingUrl);
        _read.Click += async (_, _) => await ReadAsync();
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => { _closing = true; _browser.Dispose(); };
    }

    private async Task InitializeAsync()
    {
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexLimitShow", "SubscriptionBrowserProbe");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            if (_closing) return;
            await _browser.EnsureCoreWebView2Async(environment);
            if (_closing) return;
            var core = _browser.CoreWebView2;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.DownloadStarting += (_, e) => { e.Cancel = true; _result.Text = "验证工具不下载文件。"; };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https") core.Navigate(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                { e.Cancel = true; _result.Text = "仅允许 HTTPS 网页。"; return; }
                _read.IsEnabled = false;
                _address.Text = "正在打开：" + uri.GetLeftPart(UriPartial.Authority);
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (_closing) return;
                _read.IsEnabled = e.IsSuccess && IsBillingPage(core.Source);
                _address.Text = Uri.TryCreate(core.Source, UriKind.Absolute, out var uri)
                    ? "当前网站：" + uri.GetLeftPart(UriPartial.Authority) : "当前网站未知";
                if (!e.IsSuccess) _result.Text = "页面未加载成功：" + e.WebErrorStatus;
            };
            core.SourceChanged += (_, _) => _read.IsEnabled = IsBillingPage(core.Source);
            _billing.IsEnabled = true;
            core.Navigate(BillingUrl);
        }
        catch (Exception ex)
        {
            if (!_closing) _result.Text = "浏览器初始化失败：" + ex.GetType().Name + "。可能需要安装或更新 WebView2 Runtime。";
        }
    }

    private async Task ReadAsync()
    {
        if (!IsBillingPage(_browser.CoreWebView2.Source)) return;
        _read.IsEnabled = false;
        try
        {
            var json = await _browser.CoreWebView2.ExecuteScriptAsync(ReadScript);
            if (_closing) return;
            if (!IsBillingPage(_browser.CoreWebView2.Source)) { _result.Text = "页面已变化，未采用读取结果。"; return; }
            var text = ReadResult(json);
            _result.Text = text is null ? "没有读到唯一、带日期的订阅说明。请确认已登录且账单页加载完整。"
                : "网页可读：" + text + "\n此工具尚未核对组件账号，不会写入账号快照或修改组件显示。";
        }
        catch (Exception ex) { if (!_closing) _result.Text = "读取失败：" + ex.GetType().Name; }
        finally { if (!_closing) _read.IsEnabled = IsBillingPage(_browser.CoreWebView2.Source); }
    }

    private static bool IsBillingPage(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.TrimEnd('/') == "/settings/billing";

    private static string? ReadResult(string json)
    {
        using var result = JsonDocument.Parse(json);
        if (result.RootElement.ValueKind != JsonValueKind.Object ||
            !result.RootElement.TryGetProperty("allowed", out var allowed) || allowed.ValueKind != JsonValueKind.True ||
            !result.RootElement.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) return null;
        var values = lines.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()!).Distinct().ToArray();
        if (values.Length != 1 || values[0].Length > 300 || values[0].Contains('@')) return null;
        var text = values[0];
        return Regex.IsMatch(text, @"^(你的套餐|您的套餐|Your plan\b|Your subscription\b)", RegexOptions.IgnoreCase) &&
               Regex.IsMatch(text, @"\d{4}年\d{1,2}月\d{1,2}日|\b\d{4}-\d{2}-\d{2}\b|\b(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4}\b", RegexOptions.IgnoreCase)
            ? text : null;
    }

    internal static void SelfTest()
    {
        if (!IsBillingPage(BillingUrl) || IsBillingPage("https://chatgpt.com.evil.test/settings/billing") ||
            IsBillingPage("http://chatgpt.com/settings/billing") || IsBillingPage("https://chatgpt.com/c/private"))
            throw new InvalidOperationException("Billing origin restrictions failed.");
        if (ReadResult("""{"allowed":true,"lines":["你的套餐已取消，将不再续订。你可以继续使用 ChatGPT Plus，直至 2026年11月2日"]}""") is null ||
            ReadResult("""{"allowed":true,"lines":["Your plan will automatically renew on November 2, 2026"]}""") is null ||
            ReadResult("""{"allowed":false,"lines":["你的套餐 2026年11月2日"]}""") is not null ||
            ReadResult("""{"allowed":true,"lines":["你的套餐 alice@example.com 2026年11月2日"]}""") is not null ||
            ReadResult("""{"allowed":true,"lines":["你的套餐 2026年11月2日","你的套餐 2026年12月2日"]}""") is not null ||
            ReadResult("""{"allowed":true,"lines":["你的套餐尚未加载"]}""") is not null)
            throw new InvalidOperationException("Visible subscription text restrictions failed.");
    }
}
