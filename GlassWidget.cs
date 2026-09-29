using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using Interop = System.Windows.Interop;

namespace CodexLimitShow;

internal sealed class GlassWidget : Window
{
    private readonly CodexAppServerClient _client = new();
    private readonly SnapshotStore _store = new();
    private readonly SubscriptionLookup _subscriptions = new();
    private readonly ReleaseUpdater _releaseUpdater = new();
    private readonly DualRing _ring = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _glintTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _clickTimer = new() { Interval = TimeSpan.FromMilliseconds(WinForms.SystemInformation.DoubleClickTime) };
    private readonly Drawing.Icon _appIcon = CreateAppIcon();
    private readonly WinForms.NotifyIcon _tray;
    private readonly HashSet<string> _subscriptionAttempted = [];
    private readonly HashSet<string> _expiryContradictionChecked = [];
    private readonly HashSet<string> _busyActions = [];
    private readonly Dictionary<string, (Button Control, string Label, string BusyLabel)> _actionButtons = [];
    private QuotaSnapshot? _snapshot;
    private HistoryGlass? _history;
    private Border? _panel;
    private TextBlock? _status;
    private bool _expanded;
    private bool _pinned;
    private bool _refreshing;
    private bool _resetUncertain;
    private bool _dragging;
    private bool _upgradeBusy;
    private bool _upgradeDownloading;
    private string _upgradeBusyLabel = "检查中…";
    private int? _upgradeProgress;
    private DockEdge _edge;
    private DockEdge _previewEdge;
    private System.Windows.Point _dragCursor;
    private System.Windows.Point _dragWindow;
    private System.Windows.Point _collapsedLocation;
    private double _glint;
    private string? _subscriptionError;

    public GlassWidget()
    {
        Title = "Codex 用量";
        Width = RingGeometry.FullSize;
        Height = RingGeometry.FullSize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Icon = Interop.Imaging.CreateBitmapSourceFromHIcon(_appIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => HideFromAltTab();
        Content = _ring;
        _ring.MouseLeftButtonDown += RingDown;
        _ring.MouseMove += RingMove;
        _ring.MouseLeftButtonUp += RingUp;
        _ring.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { _clickTimer.Stop(); _ = RefreshAsync(forceAccount: true); }
        };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); if (!_dragging) ToggleExpanded(); };
        _tray = new WinForms.NotifyIcon
        {
            Text = "Codex 用量",
            Icon = _appIcon,
            Visible = true,
            ContextMenuStrip = new WinForms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("显示", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        _tray.ContextMenuStrip.Items.Add("打开 Codex", null, (_, _) => Dispatcher.Invoke(() => _ = OpenCodexAsync()));
        _tray.ContextMenuStrip.Items.Add("刷新额度", null, (_, _) => Dispatcher.Invoke(() => _ = RefreshAsync(true)));
        _tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Exit));
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _glintTimer.Tick += (_, _) =>
        {
            _glint = (_glint + 0.03) % 1;
            _ring.GlintProgress = _glint;
            _ring.InvalidateVisual();
        };
        Loaded += (_, _) =>
        {
            var area = WinForms.Screen.PrimaryScreen!.WorkingArea;
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Left = area.Right / scale - Width - 26;
            Top = area.Top / scale + 80;
            _collapsedLocation = new System.Windows.Point(Left, Top);
            _refreshTimer.Start();
            _ = RefreshAsync();
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(CollapseIfOutside, DispatcherPriority.Background);
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _glintTimer.Stop();
            _clickTimer.Stop();
            _history?.Close();
            _tray.Visible = false;
            _tray.Dispose();
            _appIcon.Dispose();
            _client.Dispose();
        };
    }

    private void HideFromAltTab()
    {
        var hwnd = new Interop.WindowInteropHelper(this).Handle;
        const int exStyle = -20, toolWindow = 0x80, appWindow = 0x40000;
        SetWindowLong(hwnd, exStyle, (GetWindowLong(hwnd, exStyle) | toolWindow) & ~appWindow);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x37);
    }

    private static Drawing.Icon CreateAppIcon()
    {
        using var bitmap = new Drawing.Bitmap(64, 64);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var glass = new Drawing.Drawing2D.LinearGradientBrush(new Drawing.Rectangle(4, 4, 56, 56),
                Drawing.Color.FromArgb(255, 31, 37, 59), Drawing.Color.FromArgb(255, 83, 67, 123), 45);
            g.FillEllipse(glass, 4, 4, 56, 56);
            using var rim = new Drawing.Pen(Drawing.Color.FromArgb(224, 200, 188, 244), 2);
            using var outerTrack = new Drawing.Pen(Drawing.Color.FromArgb(140, 80, 91, 116), 6);
            using var innerTrack = new Drawing.Pen(Drawing.Color.FromArgb(140, 80, 91, 116), 5);
            using var outer = new Drawing.Pen(Drawing.Color.FromArgb(255, 92, 242, 162), 6);
            using var inner = new Drawing.Pen(Drawing.Color.FromArgb(255, 255, 180, 84), 5);
            g.DrawEllipse(rim, 4, 4, 56, 56);
            g.DrawEllipse(outerTrack, 11, 11, 42, 42);
            g.DrawEllipse(innerTrack, 21, 21, 22, 22);
            g.DrawArc(outer, 11, 11, 42, 42, -90, 260);
            g.DrawArc(inner, 21, 21, 22, 22, -90, 210);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private async Task RefreshAsync(bool forceAccount = false)
    {
        if (_refreshing) return;
        _refreshing = true;
        _ring.Refreshing = true;
        _ring.GlintOpacity = 1;
        _glintTimer.Start();
        try
        {
            var next = await _client.ReadSnapshotAsync(CancellationToken.None, forceAccount);
            var oldAccount = _snapshot?.Account?.StorageKey;
            if (oldAccount != next.Account?.StorageKey) _subscriptionError = null;
            var saved = next.Account is null ? null : _store.Load().FirstOrDefault(x => x.StorageKey == next.Account.StorageKey);
            if (next.Account is not null && saved?.SubscriptionExpiresAt is not null)
                next = next with { Account = next.Account with { SubscriptionExpiresAt = saved.SubscriptionExpiresAt,
                    SubscriptionCheckedAt = saved.SubscriptionCheckedAt } };
            _snapshot = next;
            _store.Save(next);
            _ring.Snapshot = next;
            _ring.IsStale = false;
            _ring.InvalidateVisual();
            RebuildPanel();
            _history?.Reload();
            _tray.Text = next.TightestWindow is null ? "Codex · 无活动窗口" :
                $"Codex {next.TightestWindowName}剩余 {next.TightestWindow.RemainingPercent}%";

            if (next.Account is { } account &&
                (oldAccount != account.StorageKey || !_subscriptionAttempted.Contains(account.StorageKey)))
            {
                _subscriptionAttempted.Add(account.StorageKey);
                _ = VerifySubscriptionAsync(account.StorageKey);
            }
            else if (next.Account is { } current && current.SubscriptionExpiresAt <= DateTimeOffset.Now &&
                     _expiryContradictionChecked.Add(current.StorageKey) &&
                     !string.Equals(current.PlanType, "free", StringComparison.OrdinalIgnoreCase))
                _ = VerifySubscriptionAsync(current.StorageKey);
        }
        catch (Exception ex)
        {
            _ring.IsStale = true;
            _ring.InvalidateVisual();
            if (_status is not null) _status.Text = _snapshot is null
                ? $"读取失败 · {SafeError(ex)}" : $"上次数据 · {SafeError(ex)}";
        }
        finally
        {
            _refreshing = false;
            _ring.Refreshing = false;
            for (var frame = 0; frame < 8; frame++)
            {
                await Task.Delay(25);
                _ring.GlintOpacity = 1 - (frame + 1) / 8.0;
                _ring.InvalidateVisual();
            }
            _glintTimer.Stop();
            _ring.InvalidateVisual();
        }
    }

    private async Task VerifySubscriptionAsync(string expectedKey)
    {
        if (_snapshot?.Account is not { } account || account.StorageKey != expectedKey) return;
        try
        {
            var localExpiry = _subscriptions.ReadLocalExpiry(account);
            if (localExpiry is { } local && _snapshot?.Account is { } current &&
                current.StorageKey == expectedKey &&
                (current.SubscriptionExpiresAt is null ||
                 (current.SubscriptionCheckedAt is null && current.SubscriptionExpiresAt != local) ||
                 (current.SubscriptionExpiresAt < DateTimeOffset.Now && local > current.SubscriptionExpiresAt)))
            {
                _snapshot = _snapshot with { Account = current with
                    { SubscriptionExpiresAt = local, SubscriptionCheckedAt = null } };
                _store.Save(_snapshot);
                RebuildPanel();
            }
            var expiry = await _subscriptions.ReadExpiryAsync(account, CancellationToken.None);
            if (_snapshot?.Account?.StorageKey != expectedKey) return;
            if (expiry is null)
            {
                _subscriptionError = "服务端未返回可核对的到期时间";
            }
            else
            {
                _snapshot = _snapshot with { Account = _snapshot.Account with { SubscriptionExpiresAt = expiry,
                    SubscriptionCheckedAt = DateTimeOffset.Now } };
                _subscriptionError = null;
                _store.Save(_snapshot);
            }
        }
        catch (Exception ex)
        {
            _subscriptionError = ex is CodexClientException { Message: var message } && message.Contains("网页防护拦截", StringComparison.Ordinal)
                ? _snapshot?.Account?.SubscriptionExpiresAt is null ? "订阅网页防护拦截（403）；暂无可显示日期"
                    : _snapshot.Account.SubscriptionCheckedAt is null ? "联网核验被拦截（403）；显示本地登录记录"
                    : "订阅网页防护拦截（403）；已保留上次核验值"
                : SafeError(ex);
        }
        RebuildPanel();
    }

    private void RingDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1) return;
        _dragging = false;
        _previewEdge = _edge;
        _dragCursor = CursorDip();
        var fullCenter = RingGeometry.FullSize / 2d;
        var currentCenter = _edge == DockEdge.None ? new System.Windows.Point(fullCenter, fullCenter) : RingGeometry.Center(_edge);
        _dragWindow = new System.Windows.Point(Left + currentCenter.X - fullCenter, Top + currentCenter.Y - fullCenter);
        _ring.CaptureMouse();
    }

    private void RingMove(object sender, MouseEventArgs e)
    {
        if (!_ring.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed || _pinned) return;
        var cursor = CursorDip();
        var delta = cursor - _dragCursor;
        if (!_dragging && Math.Abs(delta.X) + Math.Abs(delta.Y) < 5) return;
        _dragging = true;
        var candidate = _dragWindow + delta;
        var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var bounds = new Rect(screen.Bounds.Left / dpi, screen.Bounds.Top / dpi,
            screen.Bounds.Width / dpi, screen.Bounds.Height / dpi);
        var cx = candidate.X + RingGeometry.FullSize / 2d;
        var cy = candidate.Y + RingGeometry.FullSize / 2d;
        var preview = cx <= bounds.Left ? DockEdge.Left : cx >= bounds.Right ? DockEdge.Right :
            cy <= bounds.Top ? DockEdge.Top : cy >= bounds.Bottom ? DockEdge.Bottom : DockEdge.None;
        if (preview != _previewEdge)
        {
            _previewEdge = preview;
            SetRingEdge(preview);
        }
        _ring.Previewing = preview != DockEdge.None;
        _ring.InvalidateVisual();
        if (preview == DockEdge.None)
        {
            var anchor = RingGeometry.Center(_edge);
            var size = RingGeometry.CanvasSize(_edge);
            Left = candidate.X + (_edge == DockEdge.None ? 0 : size.Width / 2 - anchor.X);
            Top = candidate.Y + (_edge == DockEdge.None ? 0 : size.Height / 2 - anchor.Y);
        }
        else
        {
            var size = RingGeometry.CanvasSize(preview);
            var center = RingGeometry.Center(preview);
            if (preview == DockEdge.Left) { Left = bounds.Left; Top = Math.Clamp(cy - center.Y, bounds.Top, bounds.Bottom - size.Height); }
            else if (preview == DockEdge.Right) { Left = bounds.Right - size.Width; Top = Math.Clamp(cy - center.Y, bounds.Top, bounds.Bottom - size.Height); }
            else if (preview == DockEdge.Top) { Left = Math.Clamp(cx - center.X, bounds.Left, bounds.Right - size.Width); Top = bounds.Top; }
            else { Left = Math.Clamp(cx - center.X, bounds.Left, bounds.Right - size.Width); Top = bounds.Bottom - size.Height; }
        }
    }

    private void RingUp(object sender, MouseButtonEventArgs e)
    {
        _ring.ReleaseMouseCapture();
        if (_dragging)
        {
            _edge = _previewEdge;
            _ring.Previewing = false;
            _ring.InvalidateVisual();
            _collapsedLocation = new System.Windows.Point(Left, Top);
            _dragging = false;
            return;
        }
        if (e.ClickCount == 1) _clickTimer.Start();
    }

    private void SetRingEdge(DockEdge edge)
    {
        _ring.Edge = edge;
        var size = RingGeometry.CanvasSize(edge);
        Width = size.Width;
        Height = size.Height;
        _ring.InvalidateVisual();
    }

    private System.Windows.Point CursorDip()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var p = WinForms.Cursor.Position;
        return new System.Windows.Point(p.X / scale, p.Y / scale);
    }

    private void ToggleExpanded()
    {
        if (_expanded)
        {
            _history?.Hide();
            Content = _ring;
            _expanded = false;
            SetRingEdge(_edge);
            Left = _collapsedLocation.X;
            Top = _collapsedLocation.Y;
            return;
        }
        _collapsedLocation = new System.Windows.Point(Left, Top);
        var anchor = new System.Windows.Point(Left + Width / 2, Top + Height / 2);
        _expanded = true;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
        Width = 400;
        Height = Math.Min(700, area.Height / dpi - 16);
        Left = Math.Clamp(anchor.X - Width / 2, area.Left / dpi, area.Right / dpi - Width);
        Top = Math.Clamp(anchor.Y - 64, area.Top / dpi + 8, area.Bottom / dpi - Height - 8);
        RebuildPanel();
        if (_history?.IsVisible == true) PositionHistory();
    }

    private void CollapseIfOutside()
    {
        if (_expanded && !IsActive && !_upgradeBusy && _history?.IsActive != true && ConfirmGlass.OpenCount == 0)
            ToggleExpanded();
    }

    private void RebuildPanel()
    {
        if (!_expanded) return;
        var s = _snapshot;
        var body = new StackPanel { Margin = new Thickness(20, 15, 20, 16) };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var collapse = Button("‹", ToggleExpanded, 26);
        collapse.Width = 26;
        collapse.ToolTip = _edge == DockEdge.None ? "收起为双圈" : "收起为停靠条";
        top.Children.Add(collapse);
        var title = Text("CODEX  /  LIMITS", 11, C("#92A5AE"), FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(title, 1); top.Children.Add(title);
        var openCodex = BusyButton("open", "打开 Codex", "打开中…", OpenCodexAsync, 84);
        Grid.SetColumn(openCodex, 2); top.Children.Add(openCodex);
        var upgrade = BusyButton("upgrade", "升级", "检查中…", UpgradeAsync, 52);
        Grid.SetColumn(upgrade, 3); top.Children.Add(upgrade);
        var pin = Button(_pinned ? "● 固定" : "◇ 固定", () => { _pinned = !_pinned; RebuildPanel(); }, 66);
        Grid.SetColumn(pin, 4); top.Children.Add(pin);
        top.Cursor = Cursors.SizeAll;
        top.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null) return;
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
                if (_history?.IsVisible == true) PositionHistory();
            }
        };
        body.Children.Add(top);
        body.Children.Add(AccountCard(s));
        var verify = BusyButton("subscription", "手动刷新有效期 ↗", "核验中…", () =>
        {
            return _snapshot?.Account is { } a ? VerifySubscriptionAsync(a.StorageKey) : Task.CompletedTask;
        }, 150);
        if (_snapshot?.Account is null) verify.IsEnabled = false;
        verify.HorizontalAlignment = HA.Left;
        verify.Margin = new Thickness(0, 7, 0, 13);
        body.Children.Add(verify);
        if (_subscriptionError is not null)
            body.Children.Add(Text($"有效期核验：{_subscriptionError}", 10, C("#E8BD88"), FontWeights.Normal, 0, true));
        body.Children.Add(QuotaCard("5 小时", s?.FiveHour));
        var longWindow = s?.Weekly ?? s?.OtherWindows.FirstOrDefault(w => w.DurationMinutes is >= 40_320 and <= 44_640);
        body.Children.Add(QuotaCard(s?.Weekly is null && longWindow is not null ? "月额度" : "周额度", longWindow));
        body.Children.Add(CreditCard(s));
        var tokenGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        tokenGrid.ColumnDefinitions.Add(new ColumnDefinition()); tokenGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var today = Tile("今日 TOKEN", FormatNumber(s?.Usage?.TodayTokens));
        var all = Tile("历史 TOKEN", FormatNumber(s?.Usage?.LifetimeTokens));
        Grid.SetColumn(all, 1);
        tokenGrid.Children.Add(today); tokenGrid.Children.Add(all);
        body.Children.Add(tokenGrid);
        body.Children.Add(Text("重置机会明细", 12, Colors.White, FontWeights.SemiBold, 13));
        if (s?.ResetCredits.Count > 0)
            foreach (var credit in s.ResetCredits.Take(3))
                body.Children.Add(Text($"{credit.Title}  ·  {credit.Status}\n获得 {credit.GrantedAt:MM-dd HH:mm}  ·  到期 {credit.ExpiresAt?.ToString("MM-dd HH:mm") ?? "未知"}",
                    10, C("#BBCAD3"), FontWeights.Normal, 5));
        else body.Children.Add(Text("未返回可用机会明细", 11, C("#94A5AF"), FontWeights.Normal, 6));
        body.Children.Add(Text(_ring.IsStale ? "数据已过期 · 保留上次成功结果" :
            $"更新于 {s?.FetchedAt:HH:mm:ss} · 每 30 秒刷新额度", 10,
            _ring.IsStale ? C("#E7B47E") : C("#8EA2AC"), FontWeights.Normal, 13));
        var version = typeof(GlassWidget).Assembly.GetName().Version?.ToString(3) ?? "未知";
        body.Children.Add(Text($"v{version} · 单文件版", 10, C("#8EA2AC"), FontWeights.Normal, 4));
        _status = Text("", 10, C("#E7B47E"), FontWeights.Normal);
        body.Children.Add(_status);
        var actions = new UniformGrid { Columns = 4, Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(Button("历史", ToggleHistory));
        actions.Children.Add(BusyButton("reset", "使用重置", "处理中…", UseResetAsync));
        actions.Children.Add(Button("托盘", () => { _history?.Hide(); Hide(); }));
        actions.Children.Add(Button("退出", Exit));
        actions.Margin = new Thickness(18, 8, 18, 16);
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetRow(actions, 1); layout.Children.Add(actions);
        _panel = new Border
        {
            CornerRadius = new CornerRadius(24),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(C("#75808E")),
            Background = new LinearGradientBrush(C("#EA303545"), C("#F3182029"), 112),
            Child = layout
        };
        var shell = new Border { Padding = new Thickness(3), Background = Brushes.Transparent,
            Child = _panel };
        Content = shell;
        body.Measure(new System.Windows.Size(Width - 8, double.PositiveInfinity));
        actions.Measure(new System.Windows.Size(Width - 8, double.PositiveInfinity));
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var area = WinForms.Screen.FromHandle(new Interop.WindowInteropHelper(this).Handle).WorkingArea;
        Height = Math.Min(Math.Ceiling(body.DesiredSize.Height + actions.DesiredSize.Height + 8), area.Height / dpi - 16);
        Top = Math.Clamp(Top, area.Top / dpi + 8, area.Bottom / dpi - Height - 8);
        if (_history?.IsVisible == true) PositionHistory();
    }

    private Border QuotaCard(string label, RateWindow? value)
    {
        var accent = value is null ? C("#AEB8CA") : RingGeometry.ProgressColor(value.RemainingPercent);
        var stack = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Border { Width = 27, Height = 27, Margin = new Thickness(0, 0, 9, 0), CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(45, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = label.StartsWith("5") ? "" : "", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13, Foreground = new SolidColorBrush(accent), HorizontalAlignment = HA.Center, VerticalAlignment = VerticalAlignment.Center } };
        header.Children.Add(icon);
        var title = Text(label, 12, Colors.White, FontWeights.SemiBold); title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1); header.Children.Add(title);
        var valueText = Text(value is null ? "未开始" : $"剩余 {value.RemainingPercent}%", 13, accent, FontWeights.Bold);
        valueText.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(valueText, 2); header.Children.Add(valueText);
        stack.Children.Add(header);
        var track = new Grid { Height = 6, Margin = new Thickness(0, 10, 0, 6), Background = new SolidColorBrush(C("#46505B")) };
        if (value is not null)
        {
            var fill = new Border { Background = new SolidColorBrush(accent), HorizontalAlignment = HA.Left };
            track.SizeChanged += (_, _) => fill.Width = Math.Max(0, track.ActualWidth * value.RemainingPercent / 100);
            track.Children.Add(fill);
        }
        stack.Children.Add(track);
        stack.Children.Add(Text(value is null ? "当前无活动窗口" :
            $"已用 {value.UsedPercent}%  ·  {ResetText(value.ResetsAt)}", 10, C("#AAB9C3"), FontWeights.Normal));
        return new Border { Child = stack, Margin = new Thickness(0, 0, 0, 9), Padding = new Thickness(12),
            Background = new SolidColorBrush(C("#703A4352")), CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(C("#556D7684")) };
    }

    private static Border CreditCard(QuotaSnapshot? s)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new StackPanel();
        label.Children.Add(Text("可用重置机会", 12, C("#C8F3D9"), FontWeights.SemiBold));
        label.Children.Add(Text("只在确认后使用", 10, C("#9BBAAA"), FontWeights.Normal));
        grid.Children.Add(label);
        var count = Text(s?.AvailableResetCredits?.ToString() ?? "—", 20, C("#9AF0B5"), FontWeights.Bold);
        Grid.SetColumn(count, 1); grid.Children.Add(count);
        return new Border { Child = grid, Padding = new Thickness(12), Margin = new Thickness(0, 2, 0, 0),
            Background = new SolidColorBrush(C("#69305243")), BorderBrush = new SolidColorBrush(C("#6A74A78B")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14) };
    }

    private static Border AccountCard(QuotaSnapshot? s)
    {
        var grid = new Grid { Margin = new Thickness(0, 11, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var avatar = new Border { Width = 38, Height = 38, Margin = new Thickness(0, 0, 11, 0), CornerRadius = new CornerRadius(19),
            Background = new SolidColorBrush(C("#5D4B5876")), BorderBrush = new SolidColorBrush(C("#7C7E91B3")), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18,
                Foreground = new SolidColorBrush(C("#DCE6FF")), HorizontalAlignment = HA.Center, VerticalAlignment = VerticalAlignment.Center } };
        grid.Children.Add(avatar);
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(Text(s?.Account?.Label ?? "账号信息未提供", 15, Colors.White, FontWeights.SemiBold));
        identity.Children.Add(Text($"订阅有效期  {ExpiryText(s)}", 10, C("#A7B6C9"), FontWeights.Normal, 3));
        Grid.SetColumn(identity, 1); grid.Children.Add(identity);
        var plan = new Border { Padding = new Thickness(9, 3, 9, 3), VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(C("#674D336F")),
            BorderBrush = new SolidColorBrush(C("#9A9B69C9")), BorderThickness = new Thickness(1),
            Child = Text(PlanText(s), 9.5, C("#E9D6FF"), FontWeights.SemiBold) };
        Grid.SetColumn(plan, 2); grid.Children.Add(plan);
        return new Border { Child = grid, Padding = new Thickness(11), CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(C("#4B30394A")), BorderBrush = new SolidColorBrush(C("#506D7B93")), BorderThickness = new Thickness(1) };
    }

    private static Border Tile(string title, string value)
    {
        var stack = new StackPanel(); stack.Children.Add(Text(title, 10, C("#9EB0BA"), FontWeights.Normal));
        stack.Children.Add(Text(value, 17, Colors.White, FontWeights.SemiBold, 4));
        return new Border { Child = stack, Margin = new Thickness(2), Padding = new Thickness(10),
            Background = new SolidColorBrush(C("#603A4553")), CornerRadius = new CornerRadius(12) };
    }

    private static TextBlock Text(string value, double size, System.Windows.Media.Color color, FontWeight weight,
        double top = 0, bool wrap = false) => new()
    {
        Text = value, FontFamily = new FontFamily("Segoe UI"), FontSize = size, FontWeight = weight,
        Foreground = new SolidColorBrush(color), Margin = new Thickness(0, top, 0, 0),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
    };

    private static Button Button(string label, Action action, double minWidth = 0)
    {
        var button = new Button { Content = label, MinWidth = minWidth, Height = 27, Margin = new Thickness(2),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 11, Foreground = new SolidColorBrush(C("#E4F2EE")),
            Background = new SolidColorBrush(C("#76485867")), BorderBrush = new SolidColorBrush(C("#787F96A7")),
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Template = GlassButtonTemplate() };
        button.Click += (_, _) => action();
        return button;
    }

    private Button BusyButton(string key, string label, string busyLabel, Func<Task> action, double minWidth = 0)
    {
        var button = Button(label, () => _ = RunBusyActionAsync(key, action), minWidth);
        _actionButtons[key] = (button, label, busyLabel);
        SetBusyVisual(button, label, key == "upgrade" ? _upgradeBusyLabel : busyLabel,
            _busyActions.Contains(key), key == "upgrade" ? _upgradeProgress : null);
        return button;
    }

    private async Task RunBusyActionAsync(string key, Func<Task> action)
    {
        if (!_busyActions.Add(key)) return;
        UpdateBusyVisual(key);
        try { await action(); }
        catch (Exception ex)
        {
            if (_status is not null) _status.Text = $"操作失败：{SafeError(ex)}";
        }
        finally
        {
            _busyActions.Remove(key);
            UpdateBusyVisual(key);
        }
    }

    private void UpdateBusyVisual(string key)
    {
        if (_actionButtons.TryGetValue(key, out var item))
            SetBusyVisual(item.Control, item.Label, key == "upgrade" ? _upgradeBusyLabel : item.BusyLabel,
                _busyActions.Contains(key), key == "upgrade" ? _upgradeProgress : null);
    }

    private void SetUpgradeVisual(string label, int? progress)
    {
        _upgradeBusyLabel = label;
        _upgradeProgress = progress;
        UpdateBusyVisual("upgrade");
    }

    private static void SetBusyVisual(Button button, string label, string busyLabel, bool busy, int? progress = null)
    {
        button.IsEnabled = !busy;
        button.Background = new SolidColorBrush(C(busy ? "#80616E87" : "#76485867"));
        if (!busy) { button.Content = label; return; }
        var content = new Grid { Width = Math.Max(button.MinWidth - 5, 40), Height = 22 };
        content.Children.Add(new TextBlock { Text = busyLabel, FontSize = 11, Foreground = new SolidColorBrush(C("#E4F2EE")),
            HorizontalAlignment = HA.Center, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new System.Windows.Controls.ProgressBar { IsIndeterminate = progress is null, Value = progress ?? 0,
            Minimum = 0, Maximum = 100, Height = 2, VerticalAlignment = VerticalAlignment.Bottom,
            Foreground = new SolidColorBrush(C("#70E9B0")), Background = Brushes.Transparent, IsHitTestVisible = false });
        button.Content = content;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T found) return found;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    internal static ControlTemplate GlassButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HA.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        border.Name = "chrome";
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = System.Windows.Controls.Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(C("#B7B5C9D7")), "chrome"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = System.Windows.Controls.Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(C("#B2788B9E")), "chrome"));
        template.Triggers.Add(pressed);
        return template;
    }

    private async Task UseResetAsync()
    {
        if (_resetUncertain) { if (_status is not null) _status.Text = "上次操作结果不明，请先核对机会状态；本次运行不再发起新消费。"; return; }
        if (_snapshot?.Account is not { } account || _snapshot.AvailableResetCredits is not > 0 ||
            DateTimeOffset.Now - _snapshot.FetchedAt > TimeSpan.FromSeconds(30) || _ring.IsStale)
        { if (_status is not null) _status.Text = "账号或额度尚未确认新鲜，请稍后重试。"; return; }
        var consumeStarted = false;
        try
        {
            var fresh = await _client.ReadSnapshotAsync(CancellationToken.None, verifyAccount: true);
            if (fresh.Account?.StorageKey != account.StorageKey || fresh.AvailableResetCredits is not > 0)
                throw new CodexClientException("账号或机会数量已变化");
            _snapshot = fresh with { Account = account };
            RebuildPanel();
            var confirm = new ConfirmGlass(account.Label, fresh.AvailableResetCredits.Value) { Owner = this };
            if (confirm.ShowDialog() != true) return;
            var idempotencyKey = Guid.NewGuid().ToString();
            consumeStarted = true;
            var outcome = await _client.ConsumeResetAsync(account.StorageKey, idempotencyKey, CancellationToken.None);
            if (outcome is "reset" or "alreadyRedeemed")
            {
                if (_status is not null) _status.Text = "已提交重置，正在重新读取额度…";
                while (_refreshing) await Task.Delay(100);
                await RefreshAsync(true);
                if (_status is not null) _status.Text = "重置请求已完成，额度已重新读取。";
            }
            else if (_status is not null) _status.Text = outcome switch
            {
                "noCredit" => "当前没有可用重置机会。",
                "nothingToReset" => "当前没有可重置的额度窗口。",
                _ => "返回结果未知，请核对当前额度。"
            };
        }
        catch (Exception ex)
        {
            _resetUncertain = consumeStarted;
            if (_status is not null) _status.Text = consumeStarted
                ? $"结果可能不确定：{SafeError(ex)}。不会自动重试。"
                : $"未提交重置：{SafeError(ex)}。";
        }
    }

    private void ToggleHistory()
    {
        if (_history?.IsVisible == true) { _history.Hide(); Activate(); return; }
        _history ??= new HistoryGlass(_store) { Owner = this };
        _history.Reload();
        PositionHistory();
        _history.Show();
        _history.Activate();
    }

    private void PositionHistory()
    {
        if (_history is null) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
        _history.Height = Height;
        _history.Left = Left - _history.Width + 1 >= area.Left / dpi
            ? Left - _history.Width + 1 : Math.Min(Left + Width - 1, area.Right / dpi - _history.Width);
        _history.Top = Math.Clamp(Top, area.Top / dpi, area.Bottom / dpi - _history.Height);
    }

    private void ShowFromTray() { Show(); Activate(); }
    private async Task UpgradeAsync()
    {
        if (_upgradeBusy) return;
        _upgradeBusy = true;
        try
        {
            var current = _releaseUpdater.CurrentVersion(Environment.ProcessPath) ??
                throw new InvalidOperationException("无法读取当前程序版本。");
            if (_status is not null) _status.Text = "正在检查 GitHub 正式版…";
            RemoteReleaseCandidate? remote = null;
            Exception? remoteError = null;
            try
            {
                using var checkTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                remote = await _releaseUpdater.CheckRemoteAsync(checkTimeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            { remoteError = ex; }
            if (remote is null || remote.Version <= current)
            {
                System.Windows.MessageBox.Show(this, remoteError is null
                    ? $"当前版本 {current}，GitHub 没有更高的单文件正式版。"
                    : $"GitHub 检查失败：{SafeError(remoteError)}", "检查升级",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (System.Windows.MessageBox.Show(this,
                    $"从 {current} 升级到 {remote.Version}？\n确认后从 GitHub 下载并校验程序，退出旧版后原位替换。历史账号快照不会删除。",
                    "升级程序", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _upgradeDownloading = true;
            SetUpgradeVisual("下载中", 0);
            if (_status is not null) _status.Text = "正在下载 GitHub 正式版…";
            var progress = new Progress<DownloadProgress>(value =>
            {
                if (!_upgradeBusy || !_upgradeDownloading) return;
                if (value.Verifying)
                {
                    SetUpgradeVisual("校验中", null);
                    if (_status is not null) _status.Text = "正在校验下载文件…";
                }
                else
                {
                    SetUpgradeVisual("下载中", value.Percent);
                    if (_status is not null)
                        _status.Text = value.Total is > 0
                            ? $"下载中 {value.Percent}% · {value.Received / 1_048_576} / {value.Total.Value / 1_048_576} MB"
                            : $"下载中 · 已接收 {value.Received / 1_048_576} MB";
                }
            });
            var downloaded = await _releaseUpdater.DownloadAsync(remote, progress, CancellationToken.None);
            _upgradeDownloading = false;
            SetUpgradeVisual("安装中", null);
            if (_status is not null) _status.Text = "正在安装新版…";
            var start = new ProcessStartInfo(downloaded) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(downloaded)! };
            start.ArgumentList.Add("--apply-update");
            start.ArgumentList.Add(downloaded);
            start.ArgumentList.Add(Environment.ProcessPath!);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(remote.Sha256);
            using var helper = Process.Start(start) ?? throw new InvalidOperationException("升级辅助程序无法启动。");
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"升级未开始：{ex.Message}", "升级正式版",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _upgradeDownloading = false;
            _upgradeBusy = false;
            _upgradeBusyLabel = "检查中…";
            _upgradeProgress = null;
            if (_status is not null && IsLoaded) _status.Text = "";
        }
    }

    private async Task OpenCodexAsync()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App")
                { UseShellExecute = true });
            var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
            while (!CodexDesktopHasWindow())
            {
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException();
                await Task.Delay(250);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException or TimeoutException)
        {
            var message = ex is TimeoutException ? "已请求打开 Codex，但 25 秒内未检测到窗口。" :
                "无法打开 Codex，请确认已安装 Codex Desktop。";
            if (_status is not null) _status.Text = message;
            _tray.ShowBalloonTip(3000, "Codex 用量", message, WinForms.ToolTipIcon.Warning);
        }
    }

    private static bool CodexDesktopHasWindow()
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero &&
                        process.MainModule?.FileName.Contains(@"\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) == true)
                        return true;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
            }
        }
        return false;
    }
    private void Exit() => Close();

    private static string ExpiryText(QuotaSnapshot? s) => s?.Account?.SubscriptionExpiresAt is { } date
        ? $"{date:yyyy-MM-dd HH:mm}  {(date < DateTimeOffset.Now ? "待核验" : s.Account.SubscriptionCheckedAt is { } checkedAt ? $"核验 {checkedAt:MM-dd}" : "本地记录·待核验")}" : "未核验";
    private static string PlanText(QuotaSnapshot? s) => (s?.RateLimitPlanType ?? s?.Account?.PlanType ?? "未知套餐").ToUpperInvariant();
    private static string ResetText(DateTimeOffset? date)
    {
        if (date is null) return "重置时间未知";
        var left = date.Value - DateTimeOffset.Now;
        return $"{date:MM-dd HH:mm} 重置 · {(left.TotalSeconds <= 0 ? "待更新" : $"{(int)left.TotalDays}天 {left.Hours}小时 {left.Minutes}分后")}";
    }
    private static string FormatNumber(long? n) => n is null ? "—" : n >= 1_000_000_000 ? $"{n / 1_000_000_000d:0.##}B" :
        n >= 1_000_000 ? $"{n / 1_000_000d:0.##}M" : n >= 1_000 ? $"{n / 1_000d:0.##}K" : n.ToString()!;
    private static System.Windows.Media.Color C(string hex) => (System.Windows.Media.Color)ColorConverter.ConvertFromString(hex);
    private static string SafeError(Exception ex) => ex is CodexClientException ? ex.Message : ex switch
    {
        TaskCanceledException => "请求超时", HttpRequestException => "网络请求失败",
        IOException => "本地文件不可用", UnauthorizedAccessException => "本地文件无法读取",
        _ => "读取失败"
    };

    internal void RenderPreview()
    {
        _snapshot = new QuotaSnapshot(
            new RateWindow(300, 38, DateTimeOffset.Now.AddHours(2)),
            new RateWindow(10080, 54, DateTimeOffset.Now.AddDays(3)), [], 2,
            [new ResetCredit("Full reset", "available", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(12)),
             new ResetCredit("Full reset", "available", DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddDays(14))],
            new UsageStats(null, 2_650_000_000), DateTimeOffset.Now,
            new AccountSummary("PREVIEW", "sample@example.com", "plus", DateTimeOffset.Now.AddMonths(1)), "plus");
        _ring.Snapshot = _snapshot;
        Save(_ring, RingGeometry.FullSize, RingGeometry.FullSize, "preview-glass-circle.png");
        _ring.GlintOpacity = 1; _ring.GlintProgress = 0.18; _ring.InvalidateVisual();
        Save(_ring, RingGeometry.FullSize, RingGeometry.FullSize, "preview-glass-refreshing.png");
        _ring.GlintOpacity = 0;
        _ring.Edge = DockEdge.Left; _ring.InvalidateVisual();
        Save(_ring, RingGeometry.SideWidth, RingGeometry.SideHeight, "preview-glass-bar-left.png");
        _ring.Edge = DockEdge.Right; _ring.InvalidateVisual();
        Save(_ring, RingGeometry.SideWidth, RingGeometry.SideHeight, "preview-glass-bar-right.png");
        _ring.Edge = DockEdge.Top; _ring.InvalidateVisual();
        Save(_ring, RingGeometry.DockWidth, RingGeometry.DockHeight, "preview-glass-bar-top.png");
        _ring.Edge = DockEdge.Bottom; _ring.InvalidateVisual();
        Save(_ring, RingGeometry.DockWidth, RingGeometry.DockHeight, "preview-glass-bar-bottom.png");
        _ring.Edge = DockEdge.None;
        _expanded = true;
        Width = 400; Height = 700;
        _subscriptionError = "联网核验被拦截（403）；显示本地登录记录";
        RebuildPanel();
        Save((UIElement)Content, 400, (int)Math.Ceiling(Height), "preview-glass-panel.png");
        _busyActions.Add("open");
        _busyActions.Add("subscription");
        _busyActions.Add("reset");
        RebuildPanel();
        Save((UIElement)Content, 400, (int)Math.Ceiling(Height), "preview-glass-panel-busy.png");
        _busyActions.Clear();
        _busyActions.Add("upgrade");
        SetUpgradeVisual("下载中", 42);
        RebuildPanel();
        Save((UIElement)Content, 400, (int)Math.Ceiling(Height), "preview-glass-panel-downloading.png");
        _busyActions.Clear();
        _upgradeBusyLabel = "检查中…";
        _upgradeProgress = null;
        var confirm = new ConfirmGlass("sample@example.com", 1);
        Save((UIElement)confirm.Content, 420, 285, "preview-glass-confirm.png");
        var previewDirectory = Path.Combine(Path.GetTempPath(), $"CodexLimitShow-preview-{Guid.NewGuid():N}");
        try
        {
            var previewStore = new SnapshotStore(Path.Combine(previewDirectory, "snapshots.json"));
            previewStore.Save(_snapshot);
            previewStore.Save(_snapshot with
            {
                Account = new AccountSummary("PREVIEW2", "second@example.com", "normal", null),
                RateLimitPlanType = "normal", FiveHour = null,
                Weekly = new RateWindow(43_200, 72, DateTimeOffset.Now.AddDays(18)), FetchedAt = DateTimeOffset.Now.AddMinutes(-8)
            });
            var history = new HistoryGlass(previewStore);
            history.Reload();
            Save((UIElement)history.Content, 560, 700, "preview-glass-history.png");
        }
        finally
        {
            if (Directory.Exists(previewDirectory)) Directory.Delete(previewDirectory, true);
        }

        static void Save(UIElement element, int width, int height, string name)
        {
            element.Measure(new System.Windows.Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(Environment.CurrentDirectory, name));
            encoder.Save(file);
        }
    }
}

internal sealed class ConfirmGlass : Window
{
    public static int OpenCount { get; private set; }
    public ConfirmGlass(string account, long count)
    {
        Title = "确认使用重置机会"; Width = 420; Height = 285; WindowStyle = WindowStyle.None;
        AllowsTransparency = true; Background = Brushes.Transparent; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; Topmost = true;
        var stack = new StackPanel { Margin = new Thickness(22, 19, 22, 18) };
        var title = new Grid { Cursor = Cursors.SizeAll };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); title.ColumnDefinitions.Add(new ColumnDefinition());
        var warningIcon = new Border { Width = 36, Height = 36, Margin = new Thickness(0, 0, 12, 0), CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(C("#504E3A25")), BorderBrush = new SolidColorBrush(C("#D7FFB553")), BorderThickness = new Thickness(1.5),
            Child = new TextBlock { Text = "!", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(C("#FFC15C")),
                HorizontalAlignment = HA.Center, VerticalAlignment = VerticalAlignment.Center } };
        title.Children.Add(warningIcon);
        var titleText = new TextBlock { Text = "使用重置机会？", FontSize = 20, Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(titleText, 1); title.Children.Add(titleText);
        title.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        stack.Children.Add(title);

        var accountRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        accountRow.ColumnDefinitions.Add(new ColumnDefinition()); accountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        accountRow.Children.Add(new TextBlock { Text = $"当前账号\n{account}", Foreground = new SolidColorBrush(C("#D6E0EC")), FontSize = 11 });
        var countText = new TextBlock { Text = $"可用机会  {count} 次", FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(C("#E9F1FF")), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(countText, 1); accountRow.Children.Add(countText);
        stack.Children.Add(new Border { Child = accountRow, Padding = new Thickness(12), CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(C("#4C354052")), BorderBrush = new SolidColorBrush(C("#61798BA6")), BorderThickness = new Thickness(1) });
        stack.Children.Add(new Border { Margin = new Thickness(0, 12, 0, 14), Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(C("#454A3A29")), BorderBrush = new SolidColorBrush(C("#93C28B55")), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "确认后会立即消费 1 次机会并重新读取额度。此操作不可撤销，也不会自动重试。",
                Foreground = new SolidColorBrush(C("#F2D5A7")), TextWrapping = TextWrapping.Wrap, FontSize = 11 } });
        var buttons = new UniformGrid { Columns = 2 };
        var cancel = new Button { Content = "取消", Height = 38, Margin = new Thickness(0, 0, 7, 0),
            Background = new SolidColorBrush(C("#5A445165")), Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(C("#778499AA")), Template = GlassWidget.GlassButtonTemplate() };
        var confirm = new Button { Content = "确认使用", Height = 38, Margin = new Thickness(7, 0, 0, 0),
            Background = new LinearGradientBrush(C("#D5A56B2B"), C("#DB6E4D26"), 90),
            Foreground = Brushes.White, BorderBrush = new SolidColorBrush(C("#FFFFC061")), Template = GlassWidget.GlassButtonTemplate() };
        cancel.Click += (_, _) => DialogResult = false; confirm.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); stack.Children.Add(buttons);
        Content = new Border { Child = stack, Background = new LinearGradientBrush(
            C("#F1333D53"), C("#F3192131"), 112), BorderBrush = new SolidColorBrush(C("#A6A9A8DC")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(22) };
        Loaded += (_, _) => OpenCount++;
        Closed += (_, _) => OpenCount--;
    }
    private static Color C(string value) => (Color)ColorConverter.ConvertFromString(value);
}

internal sealed class HistoryGlass : Window
{
    private readonly SnapshotStore _store;
    private readonly StackPanel _accountList = new();
    private readonly ContentControl _details = new();
    private IReadOnlyList<SavedAccountSnapshot> _entries = [];
    private int _selectedIndex;

    public HistoryGlass(SnapshotStore store)
    {
        _store = store; Title = "历史账号快照"; Width = 560; Height = 720;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; WindowStartupLocation = WindowStartupLocation.Manual;
        Content = BuildShell();
    }

    private UIElement BuildShell()
    {
        var root = new Grid { Margin = new Thickness(20, 16, 20, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var header = new Grid { Margin = new Thickness(0, 0, 0, 14), Cursor = Cursors.SizeAll };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(T("历史账号快照", 17, Colors.White, FontWeights.SemiBold));
        heading.Children.Add(T("每个账号只保留最后一次成功状态 · 只读", 10, C("#9FB0C5"), FontWeights.Normal, 3));
        header.Children.Add(heading);
        var close = new Button { Content = "×", Width = 30, Height = 30, Background = new SolidColorBrush(C("#63526376")),
            Foreground = Brushes.White, BorderBrush = new SolidColorBrush(C("#738FA0B8")), Template = GlassWidget.GlassButtonTemplate() };
        close.Click += (_, _) => Hide(); Grid.SetColumn(close, 1); header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindButton(source) is not null) return;
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        };
        root.Children.Add(header);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        var accounts = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(C("#5A1A2231")), BorderBrush = new SolidColorBrush(C("#536F7E98")), BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = _accountList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        Grid.SetColumn(accounts, 0); columns.Children.Add(accounts);
        var seam = new Border { Width = 1, Background = new LinearGradientBrush(C("#006C7E9B"), C("#A58CA5D4"), 90) };
        Grid.SetColumn(seam, 1); columns.Children.Add(seam);
        var detailScroll = new ScrollViewer { Content = _details, Margin = new Thickness(14, 0, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(detailScroll, 2); columns.Children.Add(detailScroll);
        Grid.SetRow(columns, 1); root.Children.Add(columns);

        return new Border { Child = root, Background = new LinearGradientBrush(C("#F22E3547"), C("#F218202D"), 112),
            BorderBrush = new SolidColorBrush(C("#9A9AA8C6")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(24) };
    }

    public void Reload()
    {
        _entries = _store.Load();
        _selectedIndex = _entries.Count == 0 ? -1 : Math.Clamp(_selectedIndex, 0, _entries.Count - 1);
        RebuildAccounts();
        ShowSelected();
    }

    private void RebuildAccounts()
    {
        _accountList.Children.Clear();
        if (_entries.Count == 0)
        {
            _accountList.Children.Add(T("暂无历史快照", 11, C("#93A5B8"), FontWeights.Normal, 8));
            return;
        }
        for (var i = 0; i < _entries.Count; i++)
        {
            var index = i;
            var entry = _entries[i];
            var copy = new StackPanel();
            copy.Children.Add(T(entry.AccountLabel, 11, Colors.White, FontWeights.SemiBold));
            copy.Children.Add(T($"{entry.PlanType.ToUpperInvariant()}  ·  {entry.SavedAt:MM-dd HH:mm}", 9, C("#A8B5C8"), FontWeights.Normal, 3));
            var item = new Border { Child = copy, Padding = new Thickness(10, 9, 8, 9), Margin = new Thickness(0, 0, 0, 6),
                CornerRadius = new CornerRadius(11), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(C(i == _selectedIndex ? "#8A4B5371" : "#382B3446")),
                BorderBrush = new SolidColorBrush(C(i == _selectedIndex ? "#A08DA8D7" : "#3D718097")), BorderThickness = new Thickness(1) };
            item.MouseLeftButtonDown += (_, _) => { _selectedIndex = index; RebuildAccounts(); ShowSelected(); };
            _accountList.Children.Add(item);
        }
    }

    private void ShowSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _entries.Count)
        {
            _details.Content = T("暂无历史快照", 12, C("#93A5B8"), FontWeights.Normal);
            return;
        }
        var x = _entries[_selectedIndex];
        var stack = new StackPanel();
        var accountRow = new Grid { Margin = new Thickness(0, 2, 0, 12) };
        accountRow.ColumnDefinitions.Add(new ColumnDefinition()); accountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identity = new StackPanel();
        identity.Children.Add(T(x.AccountLabel, 15, Colors.White, FontWeights.SemiBold));
        identity.Children.Add(T($"快照于 {x.SavedAt:yyyy-MM-dd HH:mm:ss}", 9.5, C("#95A7BD"), FontWeights.Normal, 3));
        accountRow.Children.Add(identity);
        var plan = new Border { Padding = new Thickness(9, 3, 9, 3), CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(C("#674D336F")), BorderBrush = new SolidColorBrush(C("#9A9B69C9")), BorderThickness = new Thickness(1),
            Child = T(x.PlanType.ToUpperInvariant(), 9.5, C("#E9D6FF"), FontWeights.SemiBold) };
        Grid.SetColumn(plan, 1); accountRow.Children.Add(plan); stack.Children.Add(accountRow);

        stack.Children.Add(InfoCard("订阅有效期", x.SubscriptionExpiresAt?.ToString("yyyy-MM-dd HH:mm") ?? "未核验",
            x.SubscriptionCheckedAt is null ? x.SubscriptionExpiresAt is null ? "没有日期记录" : "本地登录记录，未联网核验"
                : $"最后核验 {x.SubscriptionCheckedAt:yyyy-MM-dd HH:mm}"));
        stack.Children.Add(QuotaSnapshotCard("5 小时额度", x.FiveHour));
        var longWindow = x.Weekly ?? x.OtherWindows.FirstOrDefault(w => w.DurationMinutes is >= 40_320 and <= 44_640);
        stack.Children.Add(QuotaSnapshotCard(x.Weekly is null && longWindow is not null ? "月额度" : "7 天额度", longWindow));

        var credit = new Grid(); credit.ColumnDefinitions.Add(new ColumnDefinition()); credit.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        credit.Children.Add(T("可用重置机会\n只读快照", 11, C("#C9EEDD"), FontWeights.SemiBold));
        var creditCount = T(x.AvailableResetCredits?.ToString() ?? "—", 22, C("#65F1A9"), FontWeights.Bold);
        Grid.SetColumn(creditCount, 1); credit.Children.Add(creditCount);
        stack.Children.Add(Card(credit, C("#59304C43"), C("#6675A88D")));

        var tokens = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        tokens.ColumnDefinitions.Add(new ColumnDefinition()); tokens.ColumnDefinitions.Add(new ColumnDefinition());
        tokens.Children.Add(TokenTile("今日 TOKEN", Format(x.Usage?.TodayTokens)));
        var lifetime = TokenTile("历史 TOKEN", Format(x.Usage?.LifetimeTokens)); Grid.SetColumn(lifetime, 1); tokens.Children.Add(lifetime);
        stack.Children.Add(tokens);
        stack.Children.Add(T("重置机会明细", 11, Colors.White, FontWeights.SemiBold, 14));
        if (x.ResetCredits.Count == 0) stack.Children.Add(T("没有机会明细", 10, C("#91A3B8"), FontWeights.Normal, 6));
        foreach (var c in x.ResetCredits)
            stack.Children.Add(T($"{c.Title}  ·  {c.Status}\n获得 {c.GrantedAt:MM-dd HH:mm}  ·  到期 {c.ExpiresAt?.ToString("MM-dd HH:mm") ?? "未知"}",
                9.5, C("#B8C6D7"), FontWeights.Normal, 7));
        _details.Content = stack;
    }

    private static Border QuotaSnapshotCard(string title, RateWindow? window)
    {
        var accent = window is null ? C("#AEB8CA") : RingGeometry.ProgressColor(window.RemainingPercent);
        var stack = new StackPanel();
        var head = new Grid(); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(T(title, 11, Colors.White, FontWeights.SemiBold));
        var remaining = T(window is null ? "未开始" : $"{window.RemainingPercent}%", 13, accent, FontWeights.Bold);
        Grid.SetColumn(remaining, 1); head.Children.Add(remaining); stack.Children.Add(head);
        var track = new Grid { Height = 5, Margin = new Thickness(0, 9, 0, 6), Background = new SolidColorBrush(C("#45526370")) };
        if (window is not null)
        {
            var fill = new Border { Background = new SolidColorBrush(accent), HorizontalAlignment = HA.Left };
            track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * window.RemainingPercent / 100;
            track.Children.Add(fill);
        }
        stack.Children.Add(track);
        stack.Children.Add(T(window is null ? "当前无活动窗口" : $"已用 {window.UsedPercent}%  ·  {window.ResetsAt?.ToString("MM-dd HH:mm") ?? "重置未知"}",
            9, C("#9EADC0"), FontWeights.Normal));
        return Card(stack, C("#5C30394B"), C("#526E7B92"));
    }

    private static Border InfoCard(string title, string value, string note)
    {
        var stack = new StackPanel(); stack.Children.Add(T(title, 9.5, C("#94A7BC"), FontWeights.Normal));
        stack.Children.Add(T(value, 13, Colors.White, FontWeights.SemiBold, 4));
        stack.Children.Add(T(note, 9, C("#91A4B8"), FontWeights.Normal, 3));
        return Card(stack, C("#50323A4C"), C("#4B6E7A90"));
    }

    private static Border TokenTile(string title, string value)
    {
        var stack = new StackPanel(); stack.Children.Add(T(title, 9, C("#94A7BC"), FontWeights.Normal));
        stack.Children.Add(T(value, 16, Colors.White, FontWeights.SemiBold, 4));
        return new Border { Child = stack, Padding = new Thickness(10), Margin = new Thickness(2), CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(C("#50313A4A")), BorderBrush = new SolidColorBrush(C("#3D6D7B91")), BorderThickness = new Thickness(1) };
    }

    private static Border Card(UIElement child, Color background, Color border) => new()
    {
        Child = child, Padding = new Thickness(11), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(13),
        Background = new SolidColorBrush(background), BorderBrush = new SolidColorBrush(border), BorderThickness = new Thickness(1)
    };

    private static TextBlock T(string text, double size, Color color, FontWeight weight, double top = 0) => new()
    {
        Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = size, FontWeight = weight,
        Foreground = new SolidColorBrush(color), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0)
    };
    private static Button? FindButton(DependencyObject? node)
    {
        while (node is not null) { if (node is Button button) return button; node = VisualTreeHelper.GetParent(node); }
        return null;
    }
    private static string Format(long? n) => n is null ? "—" : n >= 1_000_000_000 ? $"{n / 1_000_000_000d:0.##}B" :
        n >= 1_000_000 ? $"{n / 1_000_000d:0.##}M" : n >= 1_000 ? $"{n / 1_000d:0.##}K" : n.ToString()!;
    private static Color C(string value) => (Color)ColorConverter.ConvertFromString(value);
}
