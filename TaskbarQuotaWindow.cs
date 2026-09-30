using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexLimitShow;

// Own child HWND only: never resize Explorer, inject code, or reparent the main window.
internal sealed class TaskbarQuotaWindow : IDisposable
{
    internal const double BarWidth = 180, BarHeight = 30;
    private readonly Action _restore;
    private readonly Action _showPanel;
    private readonly Action<Point> _contextMenu;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _clickTimer = new()
        { Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime) };
    private readonly Border _glass;
    private readonly TextBlock _fiveLabel, _fiveValue, _longLabel, _longValue;
    private readonly System.Windows.Shapes.Ellipse _staleDot;
    private HwndSource? _source;
    private IntPtr _parent;
    private Rect _bounds = Rect.Empty;
    private bool _requested, _disposed;
    private bool _leftPressed;
    private bool _noSafeSpace;
    private int _rebindRetries;
    private double _dpi;

    internal IntPtr Handle => _source?.Handle ?? IntPtr.Zero;
    internal Rect Bounds => _bounds;
    internal double DpiScale => _dpi > 0 ? _dpi : 1;
    internal string LastFailure { get; private set; } = string.Empty;

    public TaskbarQuotaWindow(Action restore, Action<Point> contextMenu, Action showPanel)
    {
        _restore = restore;
        _contextMenu = contextMenu;
        _showPanel = showPanel;
        var grid = new Grid { Margin = new Thickness(11, 0, 11, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        (_fiveLabel, _fiveValue) = AddQuota(grid, 0, "5h");
        (_longLabel, _longValue) = AddQuota(grid, 2, "7d");
        var separator = new Border
        {
            Width = 1, Height = 12, Background = Brush(Color.FromArgb(60, 181, 184, 222)),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HA.Center
        };
        Grid.SetColumn(separator, 1);
        grid.Children.Add(separator);
        _staleDot = new System.Windows.Shapes.Ellipse
        {
            Width = 4, Height = 4, Fill = Brush(Color.FromRgb(255, 198, 110)),
            HorizontalAlignment = HA.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, -6, 0), Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(_staleDot, 2);
        grid.Children.Add(_staleDot);
        _glass = new Border
        {
            Width = BarWidth, Height = BarHeight, CornerRadius = new CornerRadius(9),
            Background = GlassBrush(false), BorderBrush = Brush(Color.FromRgb(112, 112, 148)),
            BorderThickness = new Thickness(1), Child = grid, Cursor = Cursors.Hand,
            SnapsToDevicePixels = true
        };
        _glass.MouseEnter += (_, _) => _glass.Background = GlassBrush(true);
        _glass.MouseLeave += (_, _) => _glass.Background = GlassBrush(false);
        _glass.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            LeftClickDown(e.ClickCount);
        };
        _glass.MouseLeftButtonUp += (_, e) => { e.Handled = true; LeftClickUp(); };
        _glass.MouseRightButtonDown += (_, e) => { e.Handled = true; CancelClick(); };
        _glass.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            RightClick(_glass.PointToScreen(e.GetPosition(_glass)));
        };
        AutomationProperties.SetName(_glass, "Codex 剩余额度；单击展开详情，双击恢复悬浮组件");
        ToolTipService.SetInitialShowDelay(_glass, 450);
        _timer.Tick += (_, _) => MaintainPlacement();
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            if (_requested && !_disposed) _showPanel();
        };
        Update(null, false);
    }

    private void LeftClickDown(int count)
    {
        if (!_requested || _disposed) return;
        _leftPressed = count == 1;
        if (count < 2) return;
        CancelClick();
        _restore();
    }

    private void LeftClickUp()
    {
        if (!_leftPressed || !_requested || _disposed) return;
        _leftPressed = false;
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    private void RightClick(Point point)
    {
        CancelClick();
        if (_requested && !_disposed) _contextMenu(point);
    }

    private void CancelClick()
    {
        _clickTimer.Stop();
        _leftPressed = false;
    }

    public bool Show(QuotaSnapshot? snapshot, bool stale)
    {
        if (_disposed) return false;
        Update(snapshot, stale);
        _requested = true;
        if (!AttachAndPosition()) { Hide(); return false; }
        _timer.Start();
        return true;
    }

    public void Update(QuotaSnapshot? snapshot, bool stale)
    {
        var longWindow = LongWindow(snapshot);
        _longLabel.Text = snapshot?.Weekly is null && longWindow is not null ? "月" : "7d";
        UpdateValue(_fiveValue, snapshot?.FiveHour);
        UpdateValue(_longValue, longWindow);
        _staleDot.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
        var status = stale ? snapshot is null ? "读取失败 · 暂无数据" : "上次数据 · 本次刷新失败" :
            snapshot is null ? "正在等待额度数据" : $"更新于 {snapshot.FetchedAt:HH:mm:ss}";
        var tooltip = $"5 小时剩余 {Percent(snapshot?.FiveHour)} · {_longLabel.Text} 剩余 {Percent(longWindow)}\n{status}\n单击展开详情 · 双击恢复悬浮组件 · 右键打开菜单";
        _glass.ToolTip = tooltip;
        AutomationProperties.SetHelpText(_glass, tooltip);
    }

    public void Hide()
    {
        _requested = false;
        _rebindRetries = 0;
        _timer.Stop();
        DropSource();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Hide();
        _disposed = true;
    }

    private void MaintainPlacement()
    {
        if (!_requested) return;
        // Explorer restart is transient: preserve the mode until its new taskbar exists.
        var parent = FindWindow("Shell_TrayWnd", null);
        if (parent == IntPtr.Zero) { DropSource(); _rebindRetries = 0; return; }
        if (_source is not null && (_source.IsDisposed || _parent != parent || !IsWindow(_source.Handle))) DropSource();
        if (AttachAndPosition()) { _rebindRetries = 0; return; }
        // The new shell HWND may appear before its notification/accessibility tree is ready.
        // Retry only that transient case, never knowingly cover a full taskbar.
        if (!_noSafeSpace && _rebindRetries++ < 3) { DropSource(); return; }
        Hide();
        _glass.Dispatcher.BeginInvoke(_restore);
    }

    private bool AttachAndPosition()
    {
        try
        {
            LastFailure = string.Empty;
            _noSafeSpace = false;
            var attached = AttachAndPositionCore();
            if (!attached && LastFailure.Length == 0) LastFailure = "任务栏空间或嵌入条件不可用";
            return attached;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or
                                   UnauthorizedAccessException or Win32Exception or ArgumentException)
        { LastFailure = $"{ex.GetType().Name}: {ex.Message}"; DropSource(); return false; }
    }

    private bool AttachAndPositionCore()
    {
        var parent = FindWindow("Shell_TrayWnd", null);
        var tray = FindWindowEx(parent, IntPtr.Zero, "TrayNotifyWnd", null);
        if (parent == IntPtr.Zero || tray == IntPtr.Zero || !GetWindowRect(parent, out var barRect) ||
            !GetWindowRect(tray, out var trayRect)) return false;
        var dpi = GetDpiForWindow(parent) / 96d;
        if (dpi <= 0) return false;
        Rect? position;
        try
        {
            var buttons = AutomationElement.FromHandle(parent).FindAll(TreeScope.Descendants,
                new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)));
            var occupied = new List<Rect>();
            foreach (AutomationElement button in buttons)
            {
                var state = button.Current;
                if (!state.IsOffscreen && !state.BoundingRectangle.IsEmpty &&
                    state.BoundingRectangle.Width > 0 && state.BoundingRectangle.Height > 0)
                    occupied.Add(state.BoundingRectangle);
            }
            // A failed/empty accessibility tree is not permission to cover unknown shell controls.
            if (occupied.Count == 0) return false;
            position = FindPosition(barRect.ToRect(), trayRect.ToRect(), occupied,
                (int)Math.Ceiling(BarWidth * dpi), (int)Math.Ceiling(BarHeight * dpi), (int)Math.Ceiling(6 * dpi));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException)
        { LastFailure = $"{ex.GetType().Name}: {ex.Message}"; return false; }
        if (position is null) { _noSafeSpace = true; return false; }
        if (_source is null || _source.IsDisposed || _parent != parent || !IsWindow(_source.Handle))
        {
            DropSource();
            var parameters = new HwndSourceParameters("Codex taskbar quota")
            {
                ParentWindow = parent,
                WindowStyle = 0x40000000 | 0x04000000 | 0x02000000, // WS_CHILD | CLIPSIBLINGS | CLIPCHILDREN
                ExtendedWindowStyle = 0x08000000 | 0x00000080, // NOACTIVATE | TOOLWINDOW
                UsesPerPixelTransparency = true,
                Width = (int)position.Value.Width, Height = (int)position.Value.Height
            };
            try
            {
                _source = new HwndSource(parameters) { RootVisual = _glass, SizeToContent = SizeToContent.Manual };
                _source.AddHook(WindowProc);
                _source.CompositionTarget.BackgroundColor = Colors.Transparent;
                _parent = parent;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or COMException)
            { LastFailure = $"{ex.GetType().Name}: {ex.Message}"; DropSource(); return false; }
            if (GetParent(_source.Handle) != parent) { DropSource(); return false; }
        }
        if (_bounds != position.Value || _dpi != dpi)
        {
            var origin = new NativePoint { X = (int)position.Value.X, Y = (int)position.Value.Y };
            if (!ScreenToClient(parent, ref origin) || !SetWindowPos(_source.Handle, IntPtr.Zero, origin.X, origin.Y,
                (int)position.Value.Width, (int)position.Value.Height, 0x0010 | 0x0040)) return false;
            _bounds = position.Value;
            _dpi = dpi;
        }
        return IsWindowVisible(_source.Handle);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0021) { handled = true; return new IntPtr(3); } // WM_MOUSEACTIVATE: MA_NOACTIVATE
        return IntPtr.Zero;
    }

    private void DropSource()
    {
        CancelClick();
        if (_source is not null)
        {
            if (!_source.IsDisposed)
            {
                _source.RemoveHook(WindowProc);
                _source.Dispose();
            }
            _source = null;
        }
        _parent = IntPtr.Zero;
        _bounds = Rect.Empty;
        _dpi = 0;
    }

    internal static Rect? FindPosition(Rect taskbar, Rect tray, IReadOnlyList<Rect> occupied, int width, int height, int gap)
    {
        if (taskbar.Height < height || taskbar.Width < taskbar.Height || tray.Left <= taskbar.Left) return null;
        var right = Math.Min(tray.Left, taskbar.Right) - gap;
        foreach (var control in occupied.Where(r => r.IntersectsWith(taskbar) && r.Left < right)
                     .OrderByDescending(r => r.Right))
        {
            if (control.Right <= right - width) continue;
            right = Math.Min(right, control.Left - gap);
        }
        if (right - width < taskbar.Left + gap) return null;
        var candidate = new Rect(right - width, taskbar.Top + Math.Floor((taskbar.Height - height) / 2), width, height);
        return occupied.Any(r => r.IntersectsWith(candidate)) ? null : candidate;
    }

    // Read-only shell checks and recreation of our own HWND; suitable for an offline desktop smoke test.
    internal string VerifyNativePlacement()
    {
        void Check()
        {
            _glass.UpdateLayout();
            var parent = FindWindow("Shell_TrayWnd", null);
            if (_source is null || _source.IsDisposed || !_requested || parent == IntPtr.Zero ||
                GetParent(_source.Handle) != parent || (GetWindowLong(_source.Handle, -16) & 0x40000000) == 0 ||
                (GetWindowLong(_source.Handle, -20) & 0x08000000) == 0 || !IsWindowVisible(_source.Handle) ||
                !GetWindowRect(_source.Handle, out var actual) || !GetWindowRect(parent, out var taskbar))
                throw new InvalidOperationException("Taskbar child HWND/style/visibility verification failed.");
            var scale = GetDpiForWindow(parent) / 96d;
            var transform = _source.CompositionTarget.TransformToDevice;
            if (actual.Right - actual.Left != (int)Math.Ceiling(BarWidth * scale) ||
                actual.Bottom - actual.Top != (int)Math.Ceiling(BarHeight * scale) ||
                actual.ToRect() != _bounds || !taskbar.ToRect().Contains(actual.ToRect()) ||
                Math.Abs(transform.M11 - scale) > 0.01 || Math.Abs(transform.M22 - scale) > 0.01 ||
                _glass.ActualWidth != BarWidth || _glass.ActualHeight != BarHeight)
                throw new InvalidOperationException("Taskbar quota DPI/bounds verification failed.");
        }
        Check();
        var previous = _source;
        DropSource();
        MaintainPlacement();
        Check();
        if (ReferenceEquals(previous, _source)) throw new InvalidOperationException("Taskbar quota HWND was not recreated.");
        return $"parent=Shell_TrayWnd; WS_CHILD=true; NOACTIVATE=true; visible=true; bounds={_bounds}; dpi={_dpi * 96:0}; " +
            $"compositionScale={_source!.CompositionTarget.TransformToDevice.M11:0.##}; visual={_glass.ActualWidth:0}x{_glass.ActualHeight:0}; " +
            "own HWND recreated=true";
    }

    private static (TextBlock Label, TextBlock Value) AddQuota(Grid grid, int column, string label)
    {
        var row = new StackPanel { Orientation = SO.Horizontal, HorizontalAlignment = HA.Center, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = label, FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
            Foreground = Brush(Color.FromRgb(188, 196, 218)), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { Text = "—", FontFamily = new FontFamily("Segoe UI"), FontSize = 14,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(name);
        row.Children.Add(value);
        Grid.SetColumn(row, column);
        grid.Children.Add(row);
        return (name, value);
    }

    private static RateWindow? LongWindow(QuotaSnapshot? snapshot) => snapshot?.Weekly ??
        snapshot?.OtherWindows.FirstOrDefault(w => w.DurationMinutes is >= 40_320 and <= 44_640);
    private static string Percent(RateWindow? window) => window is null ? "—" : $"{window.RemainingPercent}%";
    private static void UpdateValue(TextBlock text, RateWindow? window)
    {
        text.Text = Percent(window);
        var color = window is null ? Color.FromRgb(174, 184, 202) : RingGeometry.ProgressColor(window.RemainingPercent);
        // Lift the dark-red tier for small text; desktop progress-bar colors stay unchanged.
        if (window?.RemainingPercent is < 25) color = Color.FromRgb(238, 135, 155);
        if (text.Foreground is not SolidColorBrush old || old.Color != color) text.Foreground = Brush(color);
    }
    private static SolidColorBrush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static LinearGradientBrush GlassBrush(bool hover)
    {
        var brush = new LinearGradientBrush(Color.FromRgb((byte)(hover ? 64 : 52), (byte)(hover ? 62 : 52), (byte)(hover ? 89 : 76)),
            Color.FromRgb(29, 35, 49), 90);
        brush.Freeze();
        return brush;
    }

    public static void SelfTest()
    {
        static void Check(bool valid) { if (!valid) throw new InvalidOperationException("Taskbar quota self-test failed."); }
        var bar = new Rect(0, 1032, 1920, 48);
        var tray = new Rect(1350, 1032, 570, 48);
        var placement = FindPosition(bar, tray, [new Rect(0, 1032, 543, 48)], 180, 30, 6);
        Check(placement is { X: 1164, Y: 1041, Width: 180, Height: 30 });
        Check(FindPosition(bar, tray, [new Rect(0, 1032, 1350, 48)], 180, 30, 6) is null);
        Check(FindPosition(new Rect(0, 0, 48, 1080), new Rect(0, 900, 48, 180), [], 180, 30, 6) is null);
        var scaled = FindPosition(new Rect(0, 1550, 2880, 72), new Rect(2025, 1550, 855, 72),
            [new Rect(0, 1550, 815, 72)], 270, 45, 9);
        Check(scaled is { Width: 270, Height: 45 } && scaled.Value.Right < 2025);
        Check(Percent(null) == "—" && Percent(new RateWindow(300, 7, null)) == "93%" &&
            Percent(new RateWindow(300, 100, null)) == "0%");
        var snapshot = new QuotaSnapshot(null, null, [new RateWindow(43_200, 38, null)], null, [], null, DateTimeOffset.Now);
        Check(LongWindow(snapshot)?.RemainingPercent == 62);
        Check(LongWindow(snapshot with { Weekly = new RateWindow(10_080, 60, null) })?.RemainingPercent == 40);
        var maximumText = new FormattedText("5h 100%", CultureInfo.InvariantCulture, FD.LeftToRight,
            new Typeface("Segoe UI"), 14, Brushes.White, 1);
        Check(maximumText.Width < (BarWidth - 22 - 17) / 2);
    }

    internal static async Task VerifyClicksAsync()
    {
        var single = 0;
        var restore = 0;
        var menu = 0;
        using var card = new TaskbarQuotaWindow(() => restore++, _ => menu++, () => single++);
        card._requested = true;
        static void Check(bool valid) { if (!valid) throw new InvalidOperationException("Taskbar click routing verification failed."); }
        async Task WaitForClick() => await Task.Delay(card._clickTimer.Interval + TimeSpan.FromMilliseconds(100));
        void Click() { card.LeftClickDown(1); card.LeftClickUp(); }

        Click();
        await WaitForClick();
        Check(single == 1 && restore == 0 && menu == 0 && card._requested);
        await WaitForClick();
        Check(single == 1);
        Click();
        card.LeftClickDown(2);
        card.LeftClickUp();
        await WaitForClick();
        Check(single == 1 && restore == 1);
        Click();
        card.RightClick(new Point());
        await WaitForClick();
        Check(single == 1 && restore == 1 && menu == 1);
        Click();
        card.DropSource();
        await WaitForClick();
        Check(single == 1);
        Click();
        card.Hide();
        await WaitForClick();
        Check(single == 1 && !card._requested);
        card._requested = true;
        Click();
        card.Dispose();
        await WaitForClick();
        Check(single == 1 && card._disposed);
    }

    internal static void RenderPreview(string directory)
    {
        Directory.CreateDirectory(directory);
        using var preview = new TaskbarQuotaWindow(() => { }, _ => { }, () => { });
        var sample = new QuotaSnapshot(new RateWindow(300, 7, null), new RateWindow(10_080, 60, null), [], null, [], null,
            new DateTimeOffset(2026, 9, 30, 15, 32, 0, TimeSpan.FromHours(8)));
        foreach (var state in new[] { "normal", "full", "empty", "stale", "low", "monthly" })
        {
            var data = state switch
            {
                "empty" => null,
                "full" => sample with { FiveHour = new RateWindow(300, 0, null), Weekly = new RateWindow(10_080, 0, null) },
                "low" => sample with { FiveHour = new RateWindow(300, 89, null), Weekly = new RateWindow(10_080, 76, null) },
                "monthly" => sample with { FiveHour = null, Weekly = null, OtherWindows = [new RateWindow(43_200, 38, null)] },
                _ => sample
            };
            preview.Update(data, state == "stale");
            preview._glass.Measure(new Size(BarWidth, BarHeight));
            preview._glass.Arrange(new Rect(0, 0, BarWidth, BarHeight));
            preview._glass.UpdateLayout();
            foreach (var scale in new[] { 1d, 1.25, 1.5 })
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(BarWidth * scale), (int)Math.Ceiling(BarHeight * scale),
                    96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(preview._glass);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, $"taskbar-{state}-{scale * 100:0}.png"));
                encoder.Save(output);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
