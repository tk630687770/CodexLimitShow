using System.Windows;
using System.Windows.Media;

namespace CodexLimitShow;

internal enum DockEdge { None, Left, Right, Top, Bottom }

internal static class RingGeometry
{
    public const int FullSize = 112;
    public const int DockWidth = 172;
    public const int DockHeight = 64;
    public const int SideWidth = 72;
    public const int SideHeight = 148;

    public static Geometry Arc(Point center, double radiusX, double radiusY, double startDegrees, double sweepDegrees)
    {
        var start = PointAt(center, radiusX, radiusY, startDegrees);
        var end = PointAt(center, radiusX, radiusY, startDegrees + Math.Min(sweepDegrees, 359.999));
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radiusX, radiusY), 0,
            sweepDegrees > 180, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }

    public static Geometry Arc(Point center, double radius, double startDegrees, double sweepDegrees) =>
        Arc(center, radius, radius, startDegrees, sweepDegrees);

    public static Point Center(DockEdge edge) => edge switch
    {
        DockEdge.Left => new Point(0, SideHeight / 2d),
        DockEdge.Right => new Point(SideWidth, SideHeight / 2d),
        DockEdge.Top => new Point(DockWidth / 2d, 0),
        DockEdge.Bottom => new Point(DockWidth / 2d, DockHeight),
        _ => new Point(48, 48)
    };

    public static Size CanvasSize(DockEdge edge) => edge switch
    {
        DockEdge.Left or DockEdge.Right => new Size(SideWidth, SideHeight),
        DockEdge.Top or DockEdge.Bottom => new Size(DockWidth, DockHeight),
        _ => new Size(FullSize, FullSize)
    };

    internal static Point PointAt(Point c, double radiusX, double radiusY, double a)
    {
        var rad = a * Math.PI / 180;
        return new Point(c.X + radiusX * Math.Cos(rad), c.Y + radiusY * Math.Sin(rad));
    }

    internal static Color ProgressColor(double remainingPercent) => remainingPercent switch
    {
        >= 75 => Color.FromRgb(92, 242, 162),
        >= 50 => Color.FromRgb(255, 180, 84),
        >= 25 => Color.FromRgb(255, 112, 77),
        _ => Color.FromRgb(152, 47, 63)
    };

    public static void SelfTest()
    {
        foreach (var edge in Enum.GetValues<DockEdge>().Where(e => e != DockEdge.None))
        {
            var size = CanvasSize(edge);
            var center = Center(edge);
            var vertical = edge is DockEdge.Left or DockEdge.Right;
            if (size.Width != (vertical ? SideWidth : DockWidth) ||
                size.Height != (vertical ? SideHeight : DockHeight) ||
                center.X < 0 || center.X > size.Width || center.Y < 0 || center.Y > size.Height)
                throw new Exception("Docked bar geometry invalid");
        }
        if (ProgressColor(75) != Color.FromRgb(92, 242, 162) ||
            ProgressColor(50) != Color.FromRgb(255, 180, 84) ||
            ProgressColor(25) != Color.FromRgb(255, 112, 77) ||
            ProgressColor(24) != Color.FromRgb(152, 47, 63))
            throw new Exception("Progress color thresholds invalid");
    }
}

internal sealed class DualRing : FrameworkElement
{
    public QuotaSnapshot? Snapshot { get; set; }
    public DockEdge Edge { get; set; }
    public bool IsStale { get; set; }
    public bool Refreshing { get; set; }
    public double GlintOpacity { get; set; }
    public bool Previewing { get; set; }
    public double GlintProgress { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        if (Edge != DockEdge.None) { DrawDockedBar(dc); return; }
        dc.PushTransform(new ScaleTransform(RingGeometry.FullSize / 96d, RingGeometry.FullSize / 96d));
        var center = RingGeometry.Center(DockEdge.None);
        const double start = -90, sweep = 360;
        var outer = Snapshot?.Weekly ?? Snapshot?.OtherWindows.FirstOrDefault(w => w.DurationMinutes is >= 40_320 and <= 44_640);
        var inner = Snapshot?.FiveHour;

        var glass = new RadialGradientBrush();
        glass.GradientStops.Add(new GradientStop(Color.FromArgb(245, 24, 30, 48), 0));
        glass.GradientStops.Add(new GradientStop(Color.FromArgb(239, 43, 48, 76), 0.73));
        glass.GradientStops.Add(new GradientStop(Color.FromArgb(222, 102, 88, 153), 1));
        var border = new Pen(new SolidColorBrush(Color.FromArgb(210, 185, 172, 241)), 1.1);
        border.Freeze();
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(68, 141, 105, 225)), 4), center, 45.8, 45.8);
        dc.DrawEllipse(glass, border, center, 47, 47);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(125, 220, 208, 255)), 0.8), center, 44.8, 44.8);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(135, 247, 231, 255)), 1.3),
            RingGeometry.Arc(center, 46, 204, 112));
        dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(237, 21, 27, 45), Color.FromArgb(167, 43, 55, 81)),
            new Pen(new SolidColorBrush(Color.FromArgb(85, 164, 189, 226)), 0.8), center, 35.5, 35.5);
        DrawTrack(dc, center, 36, 36, start, sweep, outer);
        DrawTrack(dc, center, 27, 27, start, sweep, inner);

        if (GlintOpacity > 0)
        {
            dc.PushOpacity(GlintOpacity);
            var angle = start + GlintProgress * sweep;
            var point = RingGeometry.PointAt(center, 46.5, 46.5, angle);
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(210, 207, 255, 231), Color.FromArgb(0, 207, 255, 231)), null,
                point, 6.5, 6.5);
            dc.DrawEllipse(Brushes.White, null, point, 2.2, 2.2);
            dc.Pop();
        }
        var plan = Snapshot?.RateLimitPlanType ?? Snapshot?.Account?.PlanType ?? "CODEX";
        plan = plan.ToUpperInvariant() switch { "PRO" => "Pro", "PLUS" => "Plus", "PROLITE" => "Pro×5", "NORMAL" => "Normal", var p when p.Length <= 8 => p, _ => "Codex" };
        DrawEngravedText(dc, plan, 9.5, new Point(center.X, center.Y - 39));
        DrawText(dc, "5h", 8.8, new Point(center.X, center.Y - 20),
            Color.FromRgb(210, 209, 235), FontWeights.SemiBold);
        DrawText(dc, Percent(inner), 13, new Point(center.X, center.Y - 8), WindowColor(inner), FontWeights.Bold);
        const double dividerHalfWidth = 16;
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(106, 176, 180, 225)), 0.7),
            new Point(center.X - dividerHalfWidth, center.Y), new Point(center.X + dividerHalfWidth, center.Y));
        DrawText(dc, Snapshot?.Weekly is null ? "月" : "7d", 8.8,
            new Point(center.X, center.Y + 7), Color.FromRgb(210, 209, 235), FontWeights.SemiBold);
        DrawText(dc, Percent(outer), 13, new Point(center.X, center.Y + 18), WindowColor(outer), FontWeights.Bold);
        if (IsStale)
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 198, 110)), 1.4),
                RingGeometry.Arc(center, 46, 46, start, sweep - 0.01));
        dc.Pop();
    }

    private void DrawDockedBar(DrawingContext dc)
    {
        var outer = Snapshot?.Weekly ?? Snapshot?.OtherWindows.FirstOrDefault(w => w.DurationMinutes is >= 40_320 and <= 44_640);
        var inner = Snapshot?.FiveHour;
        var size = RingGeometry.CanvasSize(Edge);
        var vertical = Edge is DockEdge.Left or DockEdge.Right;
        var glass = new RadialGradientBrush();
        glass.GradientStops.Add(new GradientStop(Color.FromArgb(245, 24, 30, 48), 0));
        glass.GradientStops.Add(new GradientStop(Color.FromArgb(239, 43, 48, 76), 0.73));
        glass.GradientStops.Add(new GradientStop(Color.FromArgb(222, 102, 88, 153), 1));
        var frame = new Rect(2, 2, size.Width - 4, size.Height - 4);
        dc.DrawRoundedRectangle(glass, new Pen(new SolidColorBrush(Color.FromArgb(210, 185, 172, 241)), 1.1), frame, 14, 14);
        dc.DrawRoundedRectangle(new RadialGradientBrush(Color.FromArgb(237, 21, 27, 45), Color.FromArgb(167, 43, 55, 81)),
            new Pen(new SolidColorBrush(Color.FromArgb(85, 164, 189, 226)), 0.8),
            new Rect(5, 5, size.Width - 10, size.Height - 10), 11, 11);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(125, 220, 208, 255)), 0.8),
            new Point(16, 5), new Point(size.Width - 16, 5));

        var plan = Snapshot?.RateLimitPlanType ?? Snapshot?.Account?.PlanType ?? "CODEX";
        plan = plan.ToUpperInvariant() switch { "PRO" => "Pro", "PLUS" => "Plus", "PROLITE" => "Pro×5", "NORMAL" => "Normal", var p when p.Length <= 8 => p, _ => "Codex" };
        DrawEngravedText(dc, plan, 10, new Point(size.Width / 2, 13));
        if (vertical)
        {
            var outerX = Edge == DockEdge.Left ? 20d : 52d;
            var innerX = Edge == DockEdge.Left ? 52d : 20d;
            DrawVerticalBar(dc, "5h", inner, innerX);
            DrawVerticalBar(dc, Snapshot?.Weekly is null ? "月" : "7d", outer, outerX);
        }
        else
        {
            DrawBarRow(dc, "5h", inner, 30);
            DrawBarRow(dc, Snapshot?.Weekly is null ? "月" : "7d", outer, 49);
        }

        if (GlintOpacity > 0)
        {
            dc.PushOpacity(GlintOpacity);
            var width = size.Width - 4;
            var height = size.Height - 4;
            var t = GlintProgress * 2 * (width + height);
            var point = t < width ? new Point(2 + t, 2) : t < width + height ? new Point(size.Width - 2, 2 + t - width) :
                t < 2 * width + height ? new Point(size.Width - 2 - (t - width - height), size.Height - 2) :
                new Point(2, size.Height - 2 - (t - 2 * width - height));
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(210, 207, 255, 231), Color.FromArgb(0, 207, 255, 231)), null, point, 6.5, 6.5);
            dc.DrawEllipse(Brushes.White, null, point, 2.2, 2.2);
            dc.Pop();
        }
        if (Previewing)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(220, 159, 255, 204)), 2);
            switch (Edge)
            {
                case DockEdge.Left: dc.DrawLine(pen, new Point(3, 16), new Point(3, size.Height - 16)); break;
                case DockEdge.Right: dc.DrawLine(pen, new Point(size.Width - 3, 16), new Point(size.Width - 3, size.Height - 16)); break;
                case DockEdge.Top: dc.DrawLine(pen, new Point(20, 3), new Point(size.Width - 20, 3)); break;
                case DockEdge.Bottom: dc.DrawLine(pen, new Point(20, size.Height - 3), new Point(size.Width - 20, size.Height - 3)); break;
            }
        }
        if (IsStale)
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 198, 110)), 1.4),
                new Rect(4, 4, size.Width - 8, size.Height - 8), 12, 12);
    }

    private static void DrawBarRow(DrawingContext dc, string label, RateWindow? window, double y)
    {
        DrawText(dc, label, 11, new Point(22, y), Color.FromRgb(210, 209, 235), FontWeights.SemiBold);
        var from = new Point(43, y);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(135, 73, 87, 116)), 5.5)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, from, new Point(128, y));
        if (window is not null && window.RemainingPercent > 0)
        {
            var color = RingGeometry.ProgressColor(window.RemainingPercent);
            var to = new Point(43 + 85 * Math.Clamp(window.RemainingPercent, 0, 100) / 100, y);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(65, color.R, color.G, color.B)), 10)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, from, to);
            dc.DrawLine(new Pen(new SolidColorBrush(color), 5.5)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, from, to);
        }
        DrawText(dc, Percent(window), 11.5, new Point(150, y), WindowColor(window), FontWeights.Bold);
    }

    private static void DrawVerticalBar(DrawingContext dc, string label, RateWindow? window, double x)
    {
        DrawText(dc, label, 10.5, new Point(x, 35), Color.FromRgb(210, 209, 235), FontWeights.SemiBold);
        var bottom = new Point(x, 111);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(135, 73, 87, 116)), 5.5)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(x, 50), bottom);
        if (window is not null && window.RemainingPercent > 0)
        {
            var color = RingGeometry.ProgressColor(window.RemainingPercent);
            var top = new Point(x, 111 - 61 * Math.Clamp(window.RemainingPercent, 0, 100) / 100);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(65, color.R, color.G, color.B)), 10)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, bottom, top);
            dc.DrawLine(new Pen(new SolidColorBrush(color), 5.5)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, bottom, top);
        }
        DrawText(dc, Percent(window), 11, new Point(x, 130), WindowColor(window), FontWeights.Bold);
    }

    private static void DrawTrack(DrawingContext dc, Point center, double radiusX, double radiusY,
        double start, double sweep, RateWindow? window)
    {
        var track = new Pen(new SolidColorBrush(Color.FromArgb(135, 73, 87, 116)), 5.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, RingGeometry.Arc(center, radiusX, radiusY, start, sweep - 0.01));
        if (window is null || window.RemainingPercent == 0) return;
        var color = RingGeometry.ProgressColor(window.RemainingPercent);
        var progress = sweep * window.RemainingPercent / 100.0;
        var glow = new Pen(new SolidColorBrush(Color.FromArgb(65, color.R, color.G, color.B)), 10)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var pen = new Pen(new SolidColorBrush(color), 5.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (progress >= 359.9)
        {
            dc.DrawEllipse(null, glow, center, radiusX, radiusY);
            dc.DrawEllipse(null, pen, center, radiusX, radiusY);
        }
        else
        {
            var arc = RingGeometry.Arc(center, radiusX, radiusY, start, progress);
            dc.DrawGeometry(null, glow, arc);
            dc.DrawGeometry(null, pen, arc);
        }
    }

    private static string Percent(RateWindow? window) => window is null ? "—" : $"{window.RemainingPercent}%";
    private static Color WindowColor(RateWindow? window) => window is null
        ? Color.FromRgb(174, 184, 202) : RingGeometry.ProgressColor(window.RemainingPercent);

    private static void DrawEngravedText(DrawingContext dc, string value, double size, Point middle)
    {
        var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentUICulture,
            FD.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size, Brushes.White, 1);
        var shape = text.BuildGeometry(new Point(middle.X - text.Width / 2, middle.Y - text.Height / 2));
        dc.PushTransform(new TranslateTransform(0, 0.9));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(205, 13, 13, 40)), null, shape);
        dc.Pop();
        dc.PushTransform(new TranslateTransform(0, -0.65));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(165, 236, 224, 255)), null, shape);
        dc.Pop();
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(221, 207, 255)),
            new Pen(new SolidColorBrush(Color.FromArgb(85, 187, 154, 255)), 1.2), shape);
    }

    private static void DrawText(DrawingContext dc, string value, double size, Point middle, Color color, FontWeight weight)
    {
        var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentUICulture,
            FD.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, new SolidColorBrush(color), 1);
        dc.DrawText(text, new Point(middle.X - text.Width / 2, middle.Y - text.Height / 2));
    }
}
