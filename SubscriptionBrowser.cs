using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CodexLimitShow;

internal sealed record BrowserSubscription(DateOnly Date, bool? AutoRenews)
{
    internal AccountSummary Apply(AccountSummary account)
    {
        var localDate = Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return account with { SubscriptionExpiresAt = new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate)),
            SubscriptionCheckedAt = DateTimeOffset.Now, SubscriptionDateOnly = true, SubscriptionAutoRenews = AutoRenews };
    }
}

internal sealed class SubscriptionBrowser : Window
{
    private const string BillingUrl = "https://chatgpt.com/settings/billing";
    private readonly string _expectedEmail;
    private readonly WebView2 _browser = new();
    private readonly TextBlock _address = new() { Margin = new Thickness(14, 6, 14, 6), Text = "正在准备网页会话…" };
    private readonly TextBlock _status = new() { Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap };
    private readonly Button _sync = new() { Content = "核对账号并同步", IsEnabled = false, Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _billing = new() { Content = "打开账单页", IsEnabled = false, Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6) };
    private readonly CancellationTokenSource _closed = new();
    private bool _verifying;
    private readonly bool _checkOnly;
    private bool _checkStarted;
    internal BrowserSubscription? Result { get; private set; }
    internal string? Failure { get; private set; }

    private const string GoAccountScript = """
        (() => {
          if (location.protocol !== 'https:' || location.hostname !== 'chatgpt.com') return false;
          const targets = [...document.querySelectorAll('button,[role="button"],a')]
            .filter(e => e.getClientRects().length && /^(账户|Account)$/.test(e.innerText.trim()));
          if (targets.length !== 1) return false;
          targets[0].click(); return true;
        })()
        """;

    private const string AccountScript = """
        (() => {
          if (location.protocol !== 'https:' || location.hostname !== 'chatgpt.com' || !location.pathname.startsWith('/settings/')) return null;
          if (![...document.querySelectorAll('h1,h2,[role="heading"]')]
              .some(e => e.getClientRects().length && /^(账户|Account)$/.test(e.innerText.trim()))) return null;
          const emails = [...new Set((document.body.innerText.match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/gi) || []))];
          return emails.length === 1 ? emails[0] : null;
        })()
        """;

    private const string BillingScript = """
        (() => {
          if (location.protocol !== 'https:' || location.hostname !== 'chatgpt.com' ||
              location.pathname.replace(/\/$/, '') !== '/settings/billing') return { allowed: false, lines: [] };
          const lines = [...document.querySelectorAll('p')]
            .filter(p => p.getClientRects().length && getComputedStyle(p).visibility !== 'hidden')
            .map(p => p.innerText.trim())
            .filter(text => /^(你的套餐|您的套餐|Your plan\b|Your subscription\b)/i.test(text))
            .filter(text => text.length <= 300 && !text.includes('@'));
          return { allowed: true, lines: [...new Set(lines)] };
        })()
        """;

    internal SubscriptionBrowser(string expectedEmail, bool checkOnly = false)
    {
        _expectedEmail = expectedEmail;
        _checkOnly = checkOnly;
        Title = "订阅有效期 · 网页核验";
        Width = 1060; Height = 800; MinWidth = 720; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (checkOnly) WindowState = WindowState.Maximized;
        _status.Text = "请自行登录并完成网站验证，然后点击“核对账号并同步”。不会操作订阅或付款设置。";
        var root = new DockPanel();
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "当前组件账号：" + expectedEmail,
            Margin = new Thickness(14, 12, 14, 0), FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "独立持久化会话 · 只读取可见账号和订阅日期，不导出 Cookie／令牌",
            Margin = new Thickness(14, 4, 14, 0), TextWrapping = TextWrapping.Wrap });
        header.Children.Add(_address);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 6) };
        actions.Children.Add(_billing); actions.Children.Add(_sync); header.Children.Add(actions);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        root.Children.Add(_browser); Content = root;
        _billing.Click += (_, _) => _browser.CoreWebView2.Navigate(BillingUrl);
        _sync.Click += async (_, _) => await VerifyAsync();
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => { _closed.Cancel(); _browser.Dispose(); };
    }

    private static bool IsChatGpt(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase);
    private static bool IsBilling(string url) => IsChatGpt(url) && new Uri(url).AbsolutePath.TrimEnd('/') == "/settings/billing";

    private async Task InitializeAsync()
    {
        try
        {
            // Reuse the profile whose login and restart persistence the user already verified.
            var profile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexLimitShow", "SubscriptionBrowserProbe");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            if (_closed.IsCancellationRequested) return;
            await _browser.EnsureCoreWebView2Async(env);
            if (_closed.IsCancellationRequested) return;
            var core = _browser.CoreWebView2;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => { e.Cancel = true; _status.Text = "网页核验不下载文件。"; };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (!_verifying && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https") core.Navigate(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                { e.Cancel = true; return; }
                _sync.IsEnabled = false;
                _address.Text = "正在打开：" + uri.GetLeftPart(UriPartial.Authority);
            };
            core.NavigationCompleted += async (_, e) =>
            {
                if (_closed.IsCancellationRequested) return;
                _sync.IsEnabled = !_verifying && e.IsSuccess && IsChatGpt(core.Source);
                _address.Text = Uri.TryCreate(core.Source, UriKind.Absolute, out var uri)
                    ? "当前网站：" + uri.GetLeftPart(UriPartial.Authority) : "当前网站未知";
                if (!e.IsSuccess) _status.Text = "页面加载失败：" + e.WebErrorStatus;
                if (_checkOnly && !_checkStarted && e.IsSuccess && IsBilling(core.Source))
                {
                    _checkStarted = true;
                    await Task.Delay(1500);
                    if (!_closed.IsCancellationRequested) await VerifyAsync();
                }
            };
            core.SourceChanged += (_, _) => _sync.IsEnabled = !_verifying && IsChatGpt(core.Source);
            _billing.IsEnabled = true;
            core.Navigate(BillingUrl);
        }
        catch (Exception ex)
        {
            if (!_closed.IsCancellationRequested)
                _status.Text = "网页初始化失败：" + ex.GetType().Name + "。请确认 WebView2 Runtime 已安装；原订阅记录未改变。";
        }
    }

    private async Task<string> ReadAccountAsync()
    {
        if (!IsChatGpt(_browser.CoreWebView2.Source)) throw new CodexClientException("请先在 ChatGPT 网页完成登录。");
        var moved = await _browser.CoreWebView2.ExecuteScriptAsync(GoAccountScript);
        if (moved != "true") throw new CodexClientException("未找到唯一的账户入口。请打开账单页并展开左侧设置导航，再重试。");
        for (var i = 0; i < 30; i++)
        {
            _closed.Token.ThrowIfCancellationRequested();
            if (!IsChatGpt(_browser.CoreWebView2.Source)) throw new CodexClientException("网页已离开 ChatGPT，请重新登录后核验。");
            var value = await _browser.CoreWebView2.ExecuteScriptAsync(AccountScript);
            var email = JsonSerializer.Deserialize<string?>(value);
            if (!string.IsNullOrWhiteSpace(email)) return email;
            await Task.Delay(500, _closed.Token);
        }
        throw new CodexClientException("未能从账户页读取唯一邮箱。原订阅记录未改变。");
    }

    private async Task VerifyAsync()
    {
        if (_verifying) return;
        _verifying = true; _sync.IsEnabled = _billing.IsEnabled = false; _browser.IsHitTestVisible = false;
        _sync.Content = "核验中…";
        try
        {
            _status.Text = "正在核对网页登录账号…";
            if (!MatchesAccount(await ReadAccountAsync(), _expectedEmail))
                throw new CodexClientException("网页登录账号与组件账号不一致。请登录当前组件账号后重试；不会自动切换账号。");
            _status.Text = "账号已匹配，正在读取账单页订阅日期…";
            _browser.CoreWebView2.Navigate(BillingUrl);
            BrowserSubscription? data = null;
            for (var i = 0; i < 30; i++)
            {
                _closed.Token.ThrowIfCancellationRequested();
                if (IsBilling(_browser.CoreWebView2.Source))
                    data = ParseBilling(await _browser.CoreWebView2.ExecuteScriptAsync(BillingScript));
                if (data is not null) break;
                await Task.Delay(500, _closed.Token);
            }
            if (data is null) throw new CodexClientException("未读到唯一、有效的订阅日期。请完成网站验证后重试。");
            _status.Text = "正在再次核对账号，防止读取期间切换账号…";
            if (!MatchesAccount(await ReadAccountAsync(), _expectedEmail))
                throw new CodexClientException("读取期间网页登录账号变化，未采用订阅日期。");
            Result = data;
            DialogResult = true;
        }
        catch (OperationCanceledException) { }
        catch (CodexClientException ex) { if (!_closed.IsCancellationRequested) _status.Text = Failure = ex.Message; }
        catch (Exception ex) { if (!_closed.IsCancellationRequested) _status.Text = Failure = "网页核验失败：" + ex.GetType().Name + "；原记录未改变。"; }
        finally
        {
            if (_checkOnly && Failure is not null && !_closed.IsCancellationRequested) DialogResult = false;
            if (!_closed.IsCancellationRequested)
            {
                _verifying = false; _browser.IsHitTestVisible = true; _sync.Content = "核对账号并同步";
                _billing.IsEnabled = true; _sync.IsEnabled = IsChatGpt(_browser.CoreWebView2.Source);
            }
        }
    }

    internal static bool MatchesAccount(string actual, string expected) =>
        !string.IsNullOrWhiteSpace(expected) && string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static BrowserSubscription? ParseBilling(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("allowed", out var allowed) ||
            allowed.ValueKind != JsonValueKind.True || !root.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) return null;
        var values = lines.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Distinct().ToArray();
        if (values.Length != 1 || values[0].Length > 300 || values[0].Contains('@')) return null;
        var text = values[0];
        if (!Regex.IsMatch(text, @"^(你的套餐|您的套餐|Your plan\b|Your subscription\b)", RegexOptions.IgnoreCase)) return null;
        var matches = Regex.Matches(text, @"\d{4}年\d{1,2}月\d{1,2}日|\b\d{4}-\d{2}-\d{2}\b|\b(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4}\b", RegexOptions.IgnoreCase);
        if (matches.Count != 1 || !DateOnly.TryParseExact(matches[0].Value,
                ["yyyy年M月d日", "yyyy-MM-dd", "MMMM d, yyyy", "MMMM d yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            date.Year is < 2020 or > 2100) return null;
        bool? renews = text.Contains("取消", StringComparison.Ordinal) || Regex.IsMatch(text, "cancelled|canceled|not renew", RegexOptions.IgnoreCase)
            ? false : Regex.IsMatch(text, "自动续订|automatically renew", RegexOptions.IgnoreCase) ? true : null;
        return new BrowserSubscription(date, renews);
    }

    internal static void SelfTest()
    {
        static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Web subscription validation failed."); }
        static string Fixture(params string[] lines) => JsonSerializer.Serialize(new { allowed = true, lines });
        Check(IsBilling(BillingUrl) && !IsBilling("https://chatgpt.com.evil.test/settings/billing") && !IsBilling("http://chatgpt.com/settings/billing"));
        Check(MatchesAccount(" Alice@example.com ", "alice@example.com") && !MatchesAccount("bob@example.com", "alice@example.com"));
        var result = ParseBilling(Fixture("你的套餐已取消，将不再续订。你可以继续使用 ChatGPT Plus，直至 2026年11月2日"));
        Check(result is { Date: var d, AutoRenews: false } && d == new DateOnly(2026, 11, 2));
        Check(ParseBilling(Fixture("Your plan will automatically renew on November 2, 2026"))?.AutoRenews == true);
        Check(ParseBilling(Fixture("你的套餐 2026年2月30日")) is null && ParseBilling(Fixture("你的套餐 alice@example.com 2026年11月2日")) is null);
        Check(ParseBilling(Fixture("你的套餐 2026年11月2日", "你的套餐 2026年12月2日")) is null);
        Check(ParseBilling(Fixture("你的套餐 2026年11月2日 或 2026年12月2日")) is null);
        Check(ParseBilling("""{"allowed":false,"lines":["你的套餐 2026年11月2日"]}""") is null);
        var account = result!.Apply(new AccountSummary("test", "alice@example.com", "plus", null));
        Check(account.SubscriptionDateOnly && account.SubscriptionAutoRenews == false && account.SubscriptionCheckedAt is not null && account.SubscriptionDateText == "2026-11-02");
        Check((account with { SubscriptionExpiresAt = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.FromHours(14)) }).SubscriptionDateText == "2026-11-02");
        Check(SubscriptionLookup.MergeLocalExpiry(account, account.SubscriptionExpiresAt!.Value.AddHours(12)) == account);
        var newer = SubscriptionLookup.MergeLocalExpiry(account, account.SubscriptionExpiresAt.Value.AddMonths(1));
        Check(!newer.SubscriptionDateOnly && newer.SubscriptionAutoRenews is null && newer.SubscriptionCheckedAt is null);
    }
}
