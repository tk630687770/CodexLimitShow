using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private readonly DispatcherTimer _updateTimer = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _glintTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _clickTimer = new() { Interval = TimeSpan.FromMilliseconds(WinForms.SystemInformation.DoubleClickTime) };
    private readonly Drawing.Icon _appIcon = CreateAppIcon();
    private readonly WinForms.NotifyIcon _tray;
    private readonly TaskbarQuotaWindow _taskbar;
    private readonly HashSet<string> _subscriptionAttempted = [];
    private readonly HashSet<string> _expiryContradictionChecked = [];
    private readonly HashSet<string> _busyActions = [];
    private readonly Dictionary<string, (Button Control, string Label, string BusyLabel, Grid? Icon)> _actionButtons = [];
    private TextBlock? _updateReminder;
    private System.Windows.Shapes.Ellipse? _updateBadge;
    private Version? _availableUpdate;
    private QuotaSnapshot? _snapshot;
    private HistoryGlass? _history;
    private Border? _panel;
    private StackPanel? _panelBody;
    private UniformGrid? _panelActions;
    private ScrollViewer? _panelScroll;
    private TextBlock? _status;
    private bool _expanded;
    private bool _taskbarMode;
    private (bool Expanded, System.Windows.Point Position, DpiScale Dpi)? _taskbarPanelReturn;
    private bool _initialized;
    private bool _creditsExpanded;
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

    public GlassWidget(bool preview = false)
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
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => FitPanelToContent(), DispatcherPriority.Loaded);
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
        _taskbar = new TaskbarQuotaWindow(ShowFromTray, point =>
            _tray.ContextMenuStrip!.Show(new Drawing.Point((int)Math.Round(point.X), (int)Math.Round(point.Y))), ShowTaskbarPanel);
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _updateTimer.Tick += (_, _) => { _updateTimer.Stop(); _ = CheckForUpdateAutomaticallyAsync(); };
        IsVisibleChanged += (_, _) => UpdateUpgradeReminder();
        _glintTimer.Tick += (_, _) =>
        {
            _glint = (_glint + 0.03) % 1;
            _ring.GlintProgress = _glint;
            _ring.InvalidateVisual();
        };
        Loaded += (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            var area = WinForms.Screen.PrimaryScreen!.WorkingArea;
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Left = area.Right / scale - Width - 26;
            Top = area.Top / scale + 80;
            _collapsedLocation = new System.Windows.Point(Left, Top);
            if (preview) return;
            _refreshTimer.Start();
            _ = RefreshAsync();
            SetAvailableRelease(_releaseUpdater.CachedRelease);
            _ = CheckForUpdateAutomaticallyAsync();
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(CollapseIfOutside, DispatcherPriority.Background);
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _updateTimer.Stop();
            _lifetime.Cancel();
            _updateReminder?.BeginAnimation(OpacityProperty, null);
            _glintTimer.Stop();
            _clickTimer.Stop();
            _history?.Close();
            _taskbar.Dispose();
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
        _ring.GlintOpacity = IsVisible ? 1 : 0;
        if (IsVisible) _glintTimer.Start();
        try
        {
            var next = await _client.ReadSnapshotAsync(CancellationToken.None, forceAccount);
            var oldAccount = _snapshot?.Account?.StorageKey;
            if (oldAccount != next.Account?.StorageKey)
            {
                _subscriptionError = null;
                _creditsExpanded = false;
            }
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
            UpdateTaskbarData();

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
            UpdateTaskbarData();
            if (_status is not null) _status.Text = _snapshot is null
                ? $"读取失败 · {SafeError(ex)}" : $"上次数据 · {SafeError(ex)}";
        }
        finally
        {
            _refreshing = false;
            _ring.Refreshing = false;
            for (var frame = 0; frame < 8 && IsVisible; frame++)
            {
                await Task.Delay(25);
                _ring.GlintOpacity = 1 - (frame + 1) / 8.0;
                _ring.InvalidateVisual();
            }
            _glintTimer.Stop();
            _ring.GlintOpacity = 0;
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
        if (_taskbarPanelReturn is not null) { DismissTaskbarPanel(); return; }
        if (_expanded)
        {
            _history?.Hide();
            Content = _ring;
            _expanded = false;
            UpdateUpgradeReminder();
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
        Left = Math.Clamp(anchor.X - Width / 2, area.Left / dpi, area.Right / dpi - Width);
        Top = anchor.Y - 64;
        RebuildPanel();
        if (_history?.IsVisible == true) PositionHistory();
    }

    private void CollapseIfOutside()
    {
        if (IsVisible && _expanded && !IsActive && !_upgradeBusy &&
            _history?.IsActive != true && ConfirmGlass.OpenCount == 0)
        {
            if (_taskbarPanelReturn is not null) DismissTaskbarPanel();
            else if (!_taskbarMode) ToggleExpanded();
        }
    }

    private void RebuildPanel()
    {
        if (!_expanded || (_taskbarMode && _taskbarPanelReturn is null)) return;
        _updateReminder?.BeginAnimation(OpacityProperty, null);
        _updateBadge = null;
        var s = _snapshot;
        var body = new StackPanel { Margin = new Thickness(20, 15, 20, 6) };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var collapse = IconButton(_taskbarPanelReturn is not null ? "收起到任务栏" :
            _edge == DockEdge.None ? "收起为双圈" : "收起为停靠条",
            Symbol("\uE73F"), ToggleExpanded);
        top.Children.Add(collapse);
        var title = Text("CODEX  /  LIMITS", 11, C("#92A5AE"), FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(title, 1); top.Children.Add(title);
        var openCodex = BusyIconButton("open", "打开 Codex", "打开中…",
            new System.Windows.Controls.Image { Width = 18, Height = 18,
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/codex-logo.png")),
                Stretch = Stretch.Uniform, HorizontalAlignment = HA.Center, VerticalAlignment = VerticalAlignment.Center },
            OpenCodexAsync);
        Grid.SetColumn(openCodex, 2); top.Children.Add(openCodex);
        _updateReminder = Symbol("\uE898");
        var upgrade = BusyIconButton("upgrade", "检查升级", "检查中…", _updateReminder, UpgradeAsync);
        _updateBadge = new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(C("#70E9B0")),
            HorizontalAlignment = HA.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        ((Grid)upgrade.Content).Children.Add(_updateBadge);
        Grid.SetColumn(upgrade, 3); top.Children.Add(upgrade);
        var pin = IconButton(_pinned ? "取消固定" : "固定位置", Symbol("\uE718"),
            () => { _pinned = !_pinned; RebuildPanel(); });
        if (_pinned)
        {
            pin.Background = new SolidColorBrush(C("#865B507C"));
            pin.BorderBrush = new SolidColorBrush(C("#B6B699DC"));
        }
        System.Windows.Automation.AutomationProperties.SetHelpText(pin, _pinned ? "当前已固定" : "当前未固定");
        Grid.SetColumn(pin, 4); top.Children.Add(pin);
        top.Cursor = Cursors.SizeAll;
        top.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null) return;
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
                FitPanelToContent();
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
        var footer = new Grid { Margin = new Thickness(0, 13, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(Text(_ring.IsStale ? "数据已过期 · 保留上次结果" :
            s is null ? "等待读取额度" : $"更新于 {s.FetchedAt:HH:mm} · 每 30 秒刷新", 10,
            _ring.IsStale ? C("#E7B47E") : C("#8EA2AC"), FontWeights.Normal));
        var version = typeof(GlassWidget).Assembly.GetName().Version?.ToString(3) ?? "未知";
        var versionText = Text($"v{version}", 10, C("#8EA2AC"), FontWeights.Normal);
        Grid.SetColumn(versionText, 1); footer.Children.Add(versionText);
        body.Children.Add(footer);
        _status = Text("", 10, C("#E7B47E"), FontWeights.Normal, wrap: true);
        var statusStyle = new Style(typeof(TextBlock));
        var emptyStatus = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(TextBlock.Text))
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self) }, Value = "" };
        emptyStatus.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        statusStyle.Triggers.Add(emptyStatus);
        _status.Style = statusStyle;
        _status.SizeChanged += (sender, _) =>
        {
            if (IsLoaded && ReferenceEquals(sender, _status)) FitPanelToContent();
        };
        _status.IsVisibleChanged += (sender, _) =>
        {
            if (IsLoaded && ReferenceEquals(sender, _status)) FitPanelToContent();
        };
        body.Children.Add(_status);
        var actions = new UniformGrid { Columns = 3 };
        actions.Children.Add(Button("历史", ToggleHistory));
        var taskbarButton = Button("托盘", HideToTaskbar);
        taskbarButton.ToolTip = "隐藏悬浮组件，在任务栏显示额度";
        actions.Children.Add(taskbarButton);
        actions.Children.Add(Button("退出", Exit));
        actions.Margin = new Thickness(18, 6, 18, 16);
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        layout.Children.Add(scroll);
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
        _panelBody = body;
        _panelActions = actions;
        _panelScroll = scroll;
        Content = shell;
        FitPanelToContent();
        UpdateUpgradeReminder();
    }

    private Rect PanelWorkArea()
    {
        if (_taskbarPanelReturn is not null && !_taskbar.Bounds.IsEmpty)
        {
            var bar = _taskbar.Bounds;
            var screen = WinForms.Screen.FromRectangle(new Drawing.Rectangle((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height));
            var available = screen.WorkingArea;
            var atTop = bar.Top + bar.Height / 2 < screen.Bounds.Top + screen.Bounds.Height / 2;
            var top = atTop ? Math.Max(available.Top, bar.Bottom) : available.Top;
            var bottom = atTop ? available.Bottom : Math.Min(available.Bottom, bar.Top);
            var scale = _taskbar.DpiScale;
            return new Rect(available.Left / scale, top / scale, available.Width / scale, (bottom - top) / scale);
        }
        var area = WinForms.Screen.FromHandle(new Interop.WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
            area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    private void FitPanelToContent(Rect? workArea = null)
    {
        if (!_expanded || _panelBody is null || _panelActions is null || _panelScroll is null) return;
        _panelBody.Measure(new System.Windows.Size(Width - 8, double.PositiveInfinity));
        _panelActions.Measure(new System.Windows.Size(Width - 8, double.PositiveInfinity));
        var area = workArea ?? PanelWorkArea();
        var desiredHeight = Math.Ceiling(_panelBody.DesiredSize.Height + _panelActions.DesiredSize.Height + 8);
        var maximumHeight = area.Height - 16;
        Height = Math.Min(desiredHeight, maximumHeight);
        _panelScroll.VerticalScrollBarVisibility = desiredHeight > maximumHeight
            ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        Top = Math.Clamp(Top, area.Top + 8, area.Bottom - Height - 8);
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

    private Border CreditCard(QuotaSnapshot? s)
    {
        var credits = (s?.ResetCredits ?? []).OrderBy(c => c.Status.Equals("available", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue).ToArray();
        var earliestExpiry = credits.Where(c => c.Status.Equals("available", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.ExpiresAt).Min();
        var stack = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new Border { Width = 36, Height = 36, Margin = new Thickness(0, 0, 11, 0),
            CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(C("#3446A77F")),
            BorderBrush = new SolidColorBrush(C("#6679C6AA")), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "\uE81E", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18,
                Foreground = new SolidColorBrush(C("#78EFBE")), HorizontalAlignment = HA.Center, VerticalAlignment = VerticalAlignment.Center } });
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(Text("重置机会", 15, C("#D7F3E5"), FontWeights.SemiBold));
        label.Children.Add(Text(earliestExpiry is { } expiry ? $"最早已知到期 · {expiry:MM-dd HH:mm}" :
            s?.AvailableResetCredits == 0 ? "暂无可用机会" : "到期时间未提供", 10.5, C("#9DBEB2"), FontWeights.Normal, 3));
        Grid.SetColumn(label, 1); header.Children.Add(label);
        var count = Text(s?.AvailableResetCredits is { } n ? $"{n} 次" : "—", 25, C("#7FF2BA"), FontWeights.SemiBold);
        count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(count, 2); header.Children.Add(count);
        stack.Children.Add(header);
        if (_creditsExpanded)
        {
            foreach (var credit in credits)
            {
                stack.Children.Add(CreditDivider());
                var row = new Grid { ToolTip = $"类型：{credit.Title}\n获得 {credit.GrantedAt:yyyy-MM-dd HH:mm}\n到期 {credit.ExpiresAt?.ToString("yyyy-MM-dd HH:mm") ?? "未知"}" };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var details = new StackPanel();
                details.Children.Add(Text(credit.Title.StartsWith("Full reset", StringComparison.OrdinalIgnoreCase) ? "完整重置" : credit.Title,
                    12, C("#E0F1E9"), FontWeights.SemiBold, wrap: true));
                details.Children.Add(Text(credit.ExpiresAt is { } date ? $"{date:MM-dd HH:mm} 到期" : "到期时间未提供",
                    11, C("#AEC6BD"), FontWeights.Normal, 3));
                row.Children.Add(details);
                var (status, color) = credit.Status.ToLowerInvariant() switch
                {
                    "available" => ("可用", C("#70EFBA")),
                    "redeeming" => ("处理中", C("#FFBC68")),
                    "redeemed" => ("已使用", C("#9AADA7")),
                    "expired" => ("已到期", C("#9AADA7")),
                    _ => ("状态未知", C("#B6C3BF"))
                };
                var state = new StackPanel { Orientation = SO.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0) };
                state.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                state.Children.Add(Text(status, 11, color, FontWeights.Normal));
                Grid.SetColumn(state, 1); row.Children.Add(state);
                stack.Children.Add(row);
            }
        }
        stack.Children.Add(CreditDivider());
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var toggle = Button(credits.Length == 0 ? "暂无机会明细" : _creditsExpanded ? "收起明细" : $"查看 {credits.Length} 条明细", ToggleCreditDetails);
        if (credits.Length > 0)
        {
            var toggleContent = new StackPanel { Orientation = SO.Horizontal };
            toggleContent.Children.Add(new TextBlock { Text = _creditsExpanded ? "\uE70E" : "\uE70D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center });
            toggleContent.Children.Add(Text(_creditsExpanded ? "收起明细" : $"查看 {credits.Length} 条明细", 11, C("#CEE7DC"), FontWeights.Normal));
            toggle.Content = toggleContent;
        }
        toggle.Background = Brushes.Transparent;
        toggle.BorderThickness = new Thickness(0);
        toggle.BorderBrush = Brushes.Transparent;
        toggle.HorizontalAlignment = HA.Left;
        toggle.IsEnabled = credits.Length > 0;
        System.Windows.Automation.AutomationProperties.SetName(toggle,
            _creditsExpanded ? "收起明细" : credits.Length == 0 ? "暂无机会明细" : $"查看 {credits.Length} 条明细");
        actions.Children.Add(toggle);
        var reset = BusyButton("reset", "使用重置", "处理中…", UseResetAsync, 94);
        reset.BorderBrush = new SolidColorBrush(C("#8C7DCAA8"));
        Grid.SetColumn(reset, 1); actions.Children.Add(reset);
        stack.Children.Add(actions);
        return new Border { Child = stack, Padding = new Thickness(12), Margin = new Thickness(0, 2, 0, 0),
            Background = new LinearGradientBrush(C("#74305243"), C("#56213630"), 112), BorderBrush = new SolidColorBrush(C("#6A74A78B")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14) };
    }

    private static Border CreditDivider() => new() { Height = 1, Margin = new Thickness(0, 6, 0, 6),
        Background = new SolidColorBrush(C("#405F8E7C")) };

    private void ToggleCreditDetails()
    {
        _creditsExpanded = !_creditsExpanded;
        RebuildPanel();
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

    private static TextBlock Symbol(string glyph) => new()
    {
        Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
        Foreground = new SolidColorBrush(C("#E4F2EE")), HorizontalAlignment = HA.Center,
        VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
    };

    private static Button IconButton(string label, UIElement icon, Action action)
    {
        var button = Button("", action, 32);
        button.Width = 32; button.Height = 30;
        button.Content = icon; button.ToolTip = label;
        ToolTipService.SetShowOnDisabled(button, true);
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    private Button BusyIconButton(string key, string label, string busyLabel, UIElement icon, Func<Task> action)
    {
        var content = new Grid { Width = 26, Height = 24 };
        content.Children.Add(icon);
        content.Children.Add(new System.Windows.Controls.ProgressBar { Height = 2,
            VerticalAlignment = VerticalAlignment.Bottom, Minimum = 0, Maximum = 100,
            Foreground = new SolidColorBrush(C("#70E9B0")), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), IsHitTestVisible = false });
        var button = IconButton(label, content, () => _ = RunBusyActionAsync(key, action));
        button.Tag = key;
        _actionButtons[key] = (button, label, busyLabel, content);
        UpdateBusyVisual(key);
        return button;
    }

    private Button BusyButton(string key, string label, string busyLabel, Func<Task> action, double minWidth = 0)
    {
        var button = Button(label, () => _ = RunBusyActionAsync(key, action), minWidth);
        button.Tag = key;
        _actionButtons[key] = (button, label, busyLabel, null);
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
                _busyActions.Contains(key), key == "upgrade" ? _upgradeProgress : null, item.Icon);
        if (key == "upgrade") UpdateUpgradeReminder();
    }

    private void SetUpgradeVisual(string label, int? progress)
    {
        _upgradeBusyLabel = label;
        _upgradeProgress = progress;
        UpdateBusyVisual("upgrade");
    }

    private static void SetBusyVisual(Button button, string label, string busyLabel, bool busy, int? progress = null, Grid? icon = null)
    {
        button.IsEnabled = !busy;
        button.Background = new SolidColorBrush(C(busy ? "#80616E87" :
            button.Tag as string == "reset" ? "#70467B66" : "#76485867"));
        if (icon is not null)
        {
            var bar = (System.Windows.Controls.ProgressBar)icon.Children[1];
            bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            bar.IsIndeterminate = busy && progress is null;
            bar.Value = progress ?? 0;
            button.ToolTip = busy ? $"{label} · {busyLabel}{(progress is { } p ? $" {p}%" : "")}" : label;
            System.Windows.Automation.AutomationProperties.SetHelpText(button, busy ? (string)button.ToolTip : "");
            return;
        }
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
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
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
        var area = PanelWorkArea();
        _history.Height = Math.Min(Height, area.Height - 16);
        _history.Left = Left - _history.Width + 1 >= area.Left
            ? Left - _history.Width + 1 : Math.Max(area.Left, Math.Min(Left + Width - 1, area.Right - _history.Width));
        _history.Top = Math.Clamp(Top, area.Top + 8, area.Bottom - _history.Height - 8);
    }

    private void UpdateTaskbarData()
    {
        var longWindow = _snapshot?.Weekly ?? _snapshot?.OtherWindows.FirstOrDefault(w => w.DurationMinutes is >= 40_320 and <= 44_640);
        var longLabel = _snapshot?.Weekly is null && longWindow is not null ? "月" : "7d";
        static string Remaining(RateWindow? window) => window is null ? "—" : $"{window.RemainingPercent:0}%";
        _tray.Text = $"Codex · 5h {Remaining(_snapshot?.FiveHour)} · {longLabel} {Remaining(longWindow)}" +
            (_ring.IsStale ? " · 上次数据" : "");
        _taskbar.Update(_snapshot, _ring.IsStale);
    }

    private void HideToTaskbar()
    {
        if (_taskbarPanelReturn is not null) { DismissTaskbarPanel(); return; }
        if (_taskbarMode || _lifetime.IsCancellationRequested) return;
        try
        {
            if (!_taskbar.Show(_snapshot, _ring.IsStale))
            {
                if (_status is not null) _status.Text = "任务栏当前没有可用显示空间，悬浮组件已保留。";
                return;
            }
            _taskbarMode = true;
            _history?.Hide();
            _clickTimer.Stop();
            _glintTimer.Stop();
            _ring.GlintOpacity = 0;
            Hide();
            _tray.Visible = false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            ShowFromTray();
            if (_status is not null) _status.Text = "任务栏显示未就绪，悬浮组件已保留。";
        }
    }

    private void ShowFromTray()
    {
        if (_lifetime.IsCancellationRequested) return;
        DismissTaskbarPanel();
        _taskbarMode = false;
        _taskbar.Hide();
        _tray.Visible = true;
        Show();
        if (_expanded) RebuildPanel();
        Activate();
    }

    private void ShowTaskbarPanel()
    {
        if (!_taskbarMode || _lifetime.IsCancellationRequested || _taskbar.Bounds.IsEmpty) return;
        if (_taskbarPanelReturn is not null) { Activate(); return; }
        _taskbarPanelReturn = (_expanded, PointToScreen(new System.Windows.Point(0, 0)), VisualTreeHelper.GetDpi(this));
        _expanded = true;
        Width = 400;
        RebuildPanel();
        var area = PanelWorkArea();
        var bar = _taskbar.Bounds;
        var scale = _taskbar.DpiScale;
        var x = Math.Clamp((bar.Left + bar.Width / 2) / scale - Width / 2,
            area.Left + 8, Math.Max(area.Left + 8, area.Right - Width - 8));
        var screen = WinForms.Screen.FromRectangle(new Drawing.Rectangle((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height));
        var atTop = bar.Top + bar.Height / 2 < screen.Bounds.Top + screen.Bounds.Height / 2;
        var y = atTop ? area.Top + 8 : area.Bottom - Height - 8;
        SetWindowPos(new Interop.WindowInteropHelper(this).Handle, IntPtr.Zero,
            (int)Math.Round(x * scale), (int)Math.Round(y * scale),
            (int)Math.Ceiling(Width * scale), (int)Math.Ceiling(Height * scale), 0x14);
        Show();
        Activate();
    }

    private void DismissTaskbarPanel()
    {
        if (_taskbarPanelReturn is not { } saved) return;
        _history?.Hide();
        Hide();
        _taskbarPanelReturn = null;
        _expanded = saved.Expanded;
        if (!_expanded) { Content = _ring; SetRingEdge(_edge); }
        SetWindowPos(new Interop.WindowInteropHelper(this).Handle, IntPtr.Zero,
            (int)Math.Round(saved.Position.X), (int)Math.Round(saved.Position.Y),
            (int)Math.Ceiling(Width * saved.Dpi.DpiScaleX), (int)Math.Ceiling(Height * saved.Dpi.DpiScaleY), 0x14);
        UpdateUpgradeReminder();
    }

    internal async Task VerifyTaskbarAsync()
    {
        await TaskbarQuotaWindow.VerifyClicksAsync();
        static void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
        }
        _snapshot = new QuotaSnapshot(new RateWindow(300, 7, null), new RateWindow(10_080, 60, null),
            [], 0, [], null, DateTimeOffset.Now);
        _ring.Snapshot = _snapshot;
        var reports = new List<string>();
        foreach (var edge in new[] { DockEdge.None, DockEdge.Left })
        {
            _edge = edge;
            SetRingEdge(edge);
            Left = edge == DockEdge.None ? 200 : 0;
            Top = 120;
            var collapsed = new System.Windows.Point(Left, Top);
            ToggleExpanded();
            Activate();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var panelLocation = new System.Windows.Point(Left, Top);
            HideToTaskbar();
            Check(_taskbarMode && !IsVisible && !_tray.Visible,
                $"Taskbar mode must replace both floating and tray views. {_taskbar.LastFailure}");
            reports.Add(_taskbar.VerifyNativePlacement());
            await Task.Delay(4000);
            var bounds = _taskbar.Bounds;
            using (var bitmap = new Drawing.Bitmap((int)bounds.Width, (int)bounds.Height))
            {
                using var graphics = Drawing.Graphics.FromImage(bitmap);
                graphics.CopyFromScreen((int)bounds.X, (int)bounds.Y, 0, 0, bitmap.Size);
                bitmap.Save(Path.Combine(Environment.CurrentDirectory, "taskbar-live.png"), Drawing.Imaging.ImageFormat.Png);
                var coloredPixels = 0;
                for (var y = 0; y < bitmap.Height; y++)
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        if (pixel.G > pixel.R + 35 && pixel.G > pixel.B + 15) coloredPixels++;
                    }
                Check(coloredPixels > 10, "Taskbar card must be visibly rendered, not merely a valid native child HWND.");
            }
            Check(_expanded && _edge == edge, "Hiding must preserve expanded and docked states.");
            var taskbarHandle = _taskbar.Handle;
            ShowTaskbarPanel();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(IsVisible && _expanded && _taskbarMode && _taskbarPanelReturn is not null &&
                !_tray.Visible && _taskbar.Handle == taskbarHandle,
                "Single-click details must keep the existing taskbar card visible.");
            var panelBottom = PointToScreen(new System.Windows.Point(0, Height));
            Check(panelBottom.Y <= bounds.Top, "Taskbar details must not cover the quota card.");
            var previousPanel = _panel;
            RebuildPanel();
            Check(!ReferenceEquals(previousPanel, _panel) && _taskbar.Handle == taskbarHandle,
                "Quota updates must rebuild visible taskbar details without removing the card.");
            _history = new HistoryGlass(new SnapshotStore(Path.Combine(Path.GetTempPath(), $"CodexLimitShow-empty-history-{Guid.NewGuid():N}.json")))
                { Owner = this };
            PositionHistory();
            _history.Show(); _history.Activate();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            CollapseIfOutside();
            Check(IsVisible && _taskbarPanelReturn is not null, "History focus must not dismiss taskbar details.");
            _history.Close(); _history = null;
            Activate();
            var confirm = new ConfirmGlass("demo@example.com", 1) { Owner = this };
            confirm.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            CollapseIfOutside();
            Check(IsVisible && _taskbarPanelReturn is not null, "Confirmation focus must not dismiss taskbar details.");
            confirm.Close(); Activate();
            HideToTaskbar();
            Check(!IsVisible && _taskbarMode && _taskbarPanelReturn is null && _taskbar.Handle == taskbarHandle,
                "The tray button must dismiss only the temporary panel.");
            ShowTaskbarPanel(); ToggleExpanded();
            Check(!IsVisible && _taskbar.Handle == taskbarHandle, "The collapse button must retain the quota card.");
            ShowTaskbarPanel();
            var focusTarget = new Window { Width = 1, Height = 1, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Left = 0, Top = 0 };
            focusTarget.Show(); focusTarget.Activate();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            CollapseIfOutside();
            Check(!IsVisible && _taskbarPanelReturn is null && _taskbar.Handle == taskbarHandle,
                "External focus must dismiss details without removing the quota card.");
            focusTarget.Close();
            ShowTaskbarPanel();
            ShowFromTray();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(IsVisible && _tray.Visible && !_taskbarMode && _taskbar.Handle == IntPtr.Zero && _expanded &&
                new System.Windows.Point(Left, Top) == panelLocation, "Restoring must preserve panel position and clean up its taskbar child.");
            ToggleExpanded();
            Check(_edge == edge && new System.Windows.Point(Left, Top) == collapsed,
                "Collapsing after restore must return to the original ring or docked view.");
            HideToTaskbar();
            ShowTaskbarPanel();
            ShowFromTray();
            Check(!_expanded && _edge == edge && IsVisible && ReferenceEquals(Content, _ring) &&
                new System.Windows.Point(Left, Top) == collapsed,
                "A peek must not turn a previously collapsed component into a permanently expanded one.");
        }
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "taskbar-test.txt"),
            "PASS: click routing, details with card retained, focus dismissal, history/modal protection, live panel rebuild, native child, visible rendering, safe placement, handle recreation, original ring/dock/panel restoration. Offline synthetic data only.\n" +
            string.Join("\n", reports));
    }

    private void SetAvailableRelease(RemoteReleaseCandidate? release)
    {
        var current = Version.Parse(typeof(GlassWidget).Assembly.GetName().Version!.ToString(3));
        _availableUpdate = release?.Version > current ? release.Version : null;
        UpdateUpgradeReminder();
    }

    private void UpdateUpgradeReminder()
    {
        if (_updateReminder is null) return;
        _updateReminder.BeginAnimation(OpacityProperty, null);
        _updateReminder.Opacity = 1;
        var remind = _availableUpdate is not null && !_busyActions.Contains("upgrade");
        _updateReminder.Foreground = new SolidColorBrush(C(remind ? "#70E9B0" : "#E4F2EE"));
        if (_updateBadge is not null) _updateBadge.Visibility = remind ? Visibility.Visible : Visibility.Collapsed;
        if (!_busyActions.Contains("upgrade") && _actionButtons.TryGetValue("upgrade", out var button))
        {
            button.Control.ToolTip = remind ? $"发现新版 v{_availableUpdate} · 点击升级" : "检查升级 · 每 24 小时自动检查";
            System.Windows.Automation.AutomationProperties.SetHelpText(button.Control, (string)button.Control.ToolTip);
        }
        if (remind && _expanded && IsVisible && !_lifetime.IsCancellationRequested && SystemParameters.ClientAreaAnimation)
            _updateReminder.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromSeconds(1.2))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    private void ScheduleAutomaticUpdateCheck()
    {
        _updateTimer.Stop();
        if (_lifetime.IsCancellationRequested) return;
        var delay = _releaseUpdater.AutomaticCheckDelay;
        _updateTimer.Interval = delay > TimeSpan.FromSeconds(1) ? delay : TimeSpan.FromSeconds(1);
        _updateTimer.Start();
    }

    private async Task CheckForUpdateAutomaticallyAsync()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var release = await _releaseUpdater.CheckAutomaticallyAsync(timeout.Token);
            if (!_lifetime.IsCancellationRequested) SetAvailableRelease(release);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or
            System.Text.Json.JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // A failed background check must not disturb quota display or erase a known update.
            if (!_lifetime.IsCancellationRequested) SetAvailableRelease(_releaseUpdater.CachedRelease);
        }
        finally { ScheduleAutomaticUpdateCheck(); }
    }

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
                SetAvailableRelease(remote);
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
            ScheduleAutomaticUpdateCheck();
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
        var previewTime = new DateTimeOffset(2026, 9, 30, 9, 46, 0, TimeSpan.FromHours(8));
        _snapshot = new QuotaSnapshot(
            new RateWindow(300, 0, previewTime.AddHours(5)),
            new RateWindow(10080, 54, new DateTimeOffset(2026, 10, 4, 11, 32, 0, previewTime.Offset)), [], 3,
            [new ResetCredit("Full reset", "available", previewTime.AddDays(-7), new DateTimeOffset(2026, 10, 23, 3, 9, 0, previewTime.Offset)),
             new ResetCredit("Full reset", "available", previewTime.AddDays(-25), new DateTimeOffset(2026, 10, 5, 7, 51, 0, previewTime.Offset)),
             new ResetCredit("Full reset", "available", previewTime, new DateTimeOffset(2026, 10, 30, 2, 49, 0, previewTime.Offset))],
            new UsageStats(null, 4_420_000_000), previewTime,
            new AccountSummary("PREVIEW", "demo@example.com", "plus", new DateTimeOffset(2026, 10, 2, 23, 17, 0, previewTime.Offset)), "plus");
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
        var previewArea = new Rect(0, 0, 1920, 1400);
        _subscriptionError = "联网核验被拦截（403）；显示本地登录记录";
        RebuildPanel();
        FitPanelToContent(previewArea);
        var compactHeight = Height;
        Save((UIElement)Content, 400, (int)Math.Ceiling(Height), "preview-glass-panel.png");
        if (_panelScroll!.ComputedVerticalScrollBarVisibility != Visibility.Collapsed)
            throw new InvalidOperationException("A fitting compact panel must not show a scrollbar.");
        foreach (var key in new[] { "open", "upgrade" })
            if (_actionButtons[key].Control.Width != 32 || _actionButtons[key].Icon is null ||
                string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(_actionButtons[key].Control)))
                throw new InvalidOperationException("Header icon buttons must remain compact and accessible.");
        _availableUpdate = new Version(99, 0, 0);
        UpdateUpgradeReminder();
        Save((UIElement)Content, 400, (int)Math.Ceiling(Height), "preview-glass-panel-update.png");
        if (_updateBadge!.Visibility != Visibility.Visible || _updateReminder!.HasAnimatedProperties)
            throw new InvalidOperationException("Hidden panels must show a static update badge without running an animation.");
        Show();
        UpdateUpgradeReminder();
        if (SystemParameters.ClientAreaAnimation && !_updateReminder.HasAnimatedProperties)
            throw new InvalidOperationException("A visible new-version icon must animate when system animations are enabled.");
        var previousReminder = _updateReminder;
        RebuildPanel();
        if (previousReminder.HasAnimatedProperties || ReferenceEquals(previousReminder, _updateReminder))
            throw new InvalidOperationException("Panel rebuild must stop the old update reminder animation.");
        ToggleExpanded();
        if (_updateReminder!.HasAnimatedProperties)
            throw new InvalidOperationException("Collapsing to the compact view must stop the update reminder animation.");
        ToggleExpanded();
        _busyActions.Add("upgrade");
        UpdateBusyVisual("upgrade");
        if (_updateReminder.HasAnimatedProperties || _actionButtons["upgrade"].Control.IsEnabled)
            throw new InvalidOperationException("An active update operation must stop the reminder and reject duplicate clicks.");
        _busyActions.Remove("upgrade");
        UpdateBusyVisual("upgrade");
        Hide();
        if (_updateReminder.HasAnimatedProperties)
            throw new InvalidOperationException("Hiding the panel must stop the update reminder animation.");
        _availableUpdate = null;
        UpdateUpgradeReminder();
        ToggleCreditDetails();
        FitPanelToContent(previewArea);
        var expandedHeight = Height;
        Save((UIElement)Content, 400, (int)Math.Ceiling(Height), "preview-glass-panel-credits.png");
        Save((UIElement)Content, 400, (int)Math.Ceiling(Height), "preview-glass-panel-credits-125.png", 1.25);
        Save((UIElement)Content, 400, (int)Math.Ceiling(Height), "preview-glass-panel-credits-150.png", 1.5);
        Save((UIElement)Content, 400, (int)Math.Ceiling(Height), "preview-glass-panel-design-compare.png", 2.245);
        if (expandedHeight <= compactHeight || _panelScroll!.ComputedVerticalScrollBarVisibility != Visibility.Collapsed)
            throw new InvalidOperationException("Credit expansion must grow the panel without scrolling when it fits.");
        RebuildPanel();
        FitPanelToContent(previewArea);
        if (!_creditsExpanded || Height != expandedHeight)
            throw new InvalidOperationException("Panel refresh must preserve expanded credit details.");
        ToggleCreditDetails();
        FitPanelToContent(previewArea);
        if (Height != compactHeight)
            throw new InvalidOperationException("Collapsing credit details must restore the compact panel height.");
        var previewSnapshot = _snapshot;
        _snapshot = _snapshot with { AvailableResetCredits = 0, ResetCredits = [] };
        RebuildPanel();
        FitPanelToContent(previewArea);
        Save((UIElement)Content, 400, (int)Math.Ceiling(Height), "preview-glass-panel-empty.png");
        _snapshot = previewSnapshot with { AvailableResetCredits = 20,
            ResetCredits = Enumerable.Range(0, 20).Select(i => new ResetCredit("Full reset", "available",
                previewTime.AddDays(-1), previewTime.AddDays(4 + i))).ToArray() };
        ToggleCreditDetails();
        FitPanelToContent(new Rect(0, 0, 1920, 720));
        Save((UIElement)Content, 400, (int)Math.Ceiling(Height), "preview-glass-panel-overflow.png");
        if (Height != 704 || _panelScroll!.ComputedVerticalScrollBarVisibility != Visibility.Visible ||
            _panelScroll.ExtentHeight <= _panelScroll.ViewportHeight)
            throw new InvalidOperationException("Oversized details must scroll within the screen while keeping actions visible.");
        _snapshot = previewSnapshot;
        _creditsExpanded = false;
        _busyActions.Add("open");
        _busyActions.Add("subscription");
        _busyActions.Add("reset");
        RebuildPanel();
        FitPanelToContent(previewArea);
        Save((UIElement)Content, 400, (int)Math.Ceiling(Height), "preview-glass-panel-busy.png");
        _busyActions.Clear();
        _busyActions.Add("upgrade");
        SetUpgradeVisual("下载中", 42);
        RebuildPanel();
        FitPanelToContent(previewArea);
        Save((UIElement)Content, 400, (int)Math.Ceiling(Height), "preview-glass-panel-downloading.png");
        var downloadBar = (System.Windows.Controls.ProgressBar)_actionButtons["upgrade"].Icon!.Children[1];
        if (downloadBar.IsIndeterminate || downloadBar.Value != 42 || downloadBar.Visibility != Visibility.Visible ||
            !_actionButtons["upgrade"].Control.ToolTip.ToString()!.Contains("42%"))
            throw new InvalidOperationException("Icon-only downloads must retain real progress and a stage tooltip.");
        _busyActions.Clear();
        _upgradeBusyLabel = "检查中…";
        _upgradeProgress = null;
        UpdateBusyVisual("upgrade");
        if (downloadBar.IsIndeterminate || downloadBar.Visibility != Visibility.Collapsed ||
            !_actionButtons["upgrade"].Control.IsEnabled)
            throw new InvalidOperationException("The icon loading indicator must stop after an operation returns.");
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

        static void Save(UIElement element, int width, int height, string name, double scale = 1)
        {
            element.Measure(new System.Windows.Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
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
