using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TrackPadLens.Models;
using TrackPadLens.Native;
using TrackPadLens.Processing;
using TrackPadLens.Rendering;

namespace TrackPadLens.UI;

/// <summary>
/// Full-screen / multi-monitor transparent topmost overlay hosting:
/// - Retained DrawingVisual InkRenderer
/// - 1.5s decaying LaserTrailRenderer
/// - Crisp #00D2FF 1.5px Writing Lens outline + 4 corner alignment markers
/// - 4px translucent hover dot when finger touches down
/// - Auto-fading (500ms) top-right scale indicator (e.g., "1.2x")
/// Permanently click-through via WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
/// plus WM_NCHITTEST -> HTTRANSPARENT so PowerPoint and underlying apps never lose focus.
/// </summary>
public partial class OverlayWindow : Window, IDisposable
{
    private sealed class LensVisualHost : FrameworkElement
    {
        private readonly VisualCollection _visuals;
        private readonly DrawingVisual _visual = new();

        private static readonly Pen LensOutlinePen;
        private static readonly Pen CornerMarkerPen;
        private static readonly Pen AnchorLockedPen;
        private static readonly Brush HoverDotBrush;
        private static readonly Brush ScaleBadgeBgBrush;
        private static readonly Brush ScaleBadgeTextBrush;
        private static readonly Typeface BadgeTypeface = new("Segoe UI");

        static LensVisualHost()
        {
            var cyan = Color.FromRgb(0x00, 0xD2, 0xFF);
            var cyanBrush = new SolidColorBrush(cyan);
            cyanBrush.Freeze();

            LensOutlinePen = new Pen(cyanBrush, 1.5);
            LensOutlinePen.Freeze();

            CornerMarkerPen = new Pen(cyanBrush, 2.2);
            CornerMarkerPen.Freeze();

            var lockedBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xEC, 0xFF));
            lockedBrush.Freeze();
            AnchorLockedPen = new Pen(lockedBrush, 1.8) { DashStyle = DashStyles.Dash };
            AnchorLockedPen.Freeze();

            var hover = new SolidColorBrush(Color.FromArgb(185, 0x00, 0xD2, 0xFF));
            hover.Freeze();
            HoverDotBrush = hover;

            var badgeBg = new SolidColorBrush(Color.FromArgb(205, 15, 23, 42));
            badgeBg.Freeze();
            ScaleBadgeBgBrush = badgeBg;

            var badgeFg = new SolidColorBrush(Color.FromRgb(0xE2, 0xF8, 0xFF));
            badgeFg.Freeze();
            ScaleBadgeTextBrush = badgeFg;
        }

        public LensVisualHost()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
            _visuals = new VisualCollection(this) { _visual };
        }

        protected override int VisualChildrenCount => _visuals.Count;
        protected override Visual GetVisualChild(int index) => _visuals[index];

        public void RenderLens(
            bool showLens,
            in LensRectDip lens,
            bool isAnchorLocked,
            in HoverDotState hoverDot,
            string? scaleBadgeText,
            double pixelsPerDip)
        {
            using DrawingContext dc = _visual.RenderOpen();

            if (showLens && lens.Width >= 100 && lens.Height >= 40)
            {
                var rect = new Rect(lens.Left, lens.Top, lens.Width, lens.Height);
                dc.DrawRectangle(null, isAnchorLocked ? AnchorLockedPen : LensOutlinePen, rect);

                // Four crisp corner alignment markers
                const double m = 11.0;
                double l = rect.Left;
                double t = rect.Top;
                double r = rect.Right;
                double b = rect.Bottom;

                // Top-left
                dc.DrawLine(CornerMarkerPen, new Point(l, t + m), new Point(l, t));
                dc.DrawLine(CornerMarkerPen, new Point(l, t), new Point(l + m, t));
                // Top-right
                dc.DrawLine(CornerMarkerPen, new Point(r - m, t), new Point(r, t));
                dc.DrawLine(CornerMarkerPen, new Point(r, t), new Point(r, t + m));
                // Bottom-left
                dc.DrawLine(CornerMarkerPen, new Point(l, b - m), new Point(l, b));
                dc.DrawLine(CornerMarkerPen, new Point(l, b), new Point(l + m, b));
                // Bottom-right
                dc.DrawLine(CornerMarkerPen, new Point(r - m, b), new Point(r, b));
                dc.DrawLine(CornerMarkerPen, new Point(r, b), new Point(r, b - m));

                if (!string.IsNullOrEmpty(scaleBadgeText))
                {
                    var ft = new FormattedText(
                        scaleBadgeText,
                        CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        BadgeTypeface,
                        11.5,
                        ScaleBadgeTextBrush,
                        pixelsPerDip);

                    double badgeW = ft.Width + 12;
                    double badgeH = ft.Height + 4;
                    var badgeRect = new Rect(r - badgeW - 6, t + 6, badgeW, badgeH);
                    dc.DrawRoundedRectangle(ScaleBadgeBgBrush, null, badgeRect, 4, 4);
                    dc.DrawText(ft, new Point(badgeRect.Left + 6, badgeRect.Top + 2));
                }
            }

            if (hoverDot.Visible)
            {
                // Tiny translucent 4px hover dot (radius = 2.0 DIP)
                dc.DrawEllipse(HoverDotBrush, null, new Point(hoverDot.X, hoverDot.Y), 2.0, 2.0);
            }
        }
    }

    private readonly LensVisualHost _lensVisual = new();
    private readonly DispatcherTimer _scaleFadeTimer;
    private readonly long[] _frameTicks = new long[32];

    private LensRectDip _currentLens = new(240, 180, 420, 262.5);
    private HoverDotState _hoverDot = new(false, 0, 0);
    private bool _lensVisible;
    private bool _isAnchorLocked;
    private string? _scaleBadgeText;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;
    private int _frameTickIdx;
    private int _frameTickCount;
    private bool _disposed;

    public LensRectDip CurrentLens => _currentLens;
    public double DpiScaleX => _dpiScaleX;
    public double DpiScaleY => _dpiScaleY;
    public double MeasuredFps { get; private set; }

    public OverlayWindow()
    {
        InitializeComponent();
        LensCanvas.Children.Add(_lensVisual);

        _scaleFadeTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _scaleFadeTimer.Tick += (_, _) =>
        {
            _scaleFadeTimer.Stop();
            _scaleBadgeText = null;
            RedrawLensChrome();
        };

        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowStyles.ApplyClickThroughOverlayStyles(this);
        RefreshDpiScale();
    }

    public void ApplyMonitorScope(MonitorScope scope)
    {
        if (scope == MonitorScope.AllMonitors)
        {
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
        }
        else
        {
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
        }

        RefreshDpiScale();
        RedrawLensChrome();
    }

    public LensRectDip GetViewportBoundsDip() => new(0, 0, Math.Max(320, Width), Math.Max(240, Height));

    public void RefreshDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            _dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            _dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
        }
    }

    public void SetLensVisibility(bool visible)
    {
        _lensVisible = visible;
        RedrawLensChrome();
    }

    public void UpdateLens(in LensRectDip lens, bool isAnchorLocked)
    {
        _currentLens = lens;
        _isAnchorLocked = isAnchorLocked;
        RecordRenderTick();
        RedrawLensChrome();
    }

    public void ShowScaleIndicator(double multiplier)
    {
        _scaleBadgeText = $"{multiplier:F1}x";
        _scaleFadeTimer.Stop();
        _scaleFadeTimer.Start();
        RedrawLensChrome();
    }

    public void UpdateHoverDot(in HoverDotState state)
    {
        _hoverDot = state;
        RedrawLensChrome();
    }

    public void BindInkEngine(InkEngine engine)
    {
        engine.ActiveStrokeUpdated += stroke =>
        {
            RecordRenderTick();
            InkSurface.UpdateActiveStroke(stroke);
        };
        engine.StrokeFinalized += stroke =>
        {
            RecordRenderTick();
            InkSurface.CommitFinalizedStroke(stroke);
        };
        engine.AllStrokesRebuilt += () =>
        {
            RecordRenderTick();
            InkSurface.RebuildFromStrokes(engine.Strokes);
        };
    }

    public void AddLaserPoint(StrokePoint point)
    {
        RecordRenderTick();
        LaserSurface.AddPoint(point);
    }

    public void ClearLaser() => LaserSurface.Clear();

    public void SetTemporaryFrameHiddenForCapture(bool hidden)
    {
        _lensVisual.Visibility = hidden ? Visibility.Hidden : Visibility.Visible;
    }

    private void RedrawLensChrome()
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _lensVisual.RenderLens(_lensVisible, in _currentLens, _isAnchorLocked, in _hoverDot, _scaleBadgeText, ppd);
    }

    private void RecordRenderTick()
    {
        long now = Stopwatch.GetTimestamp();
        _frameTicks[_frameTickIdx] = now;
        _frameTickIdx = (_frameTickIdx + 1) % _frameTicks.Length;
        if (_frameTickCount < _frameTicks.Length) _frameTickCount++;

        if (_frameTickCount >= 4)
        {
            int oldestIdx = (_frameTickIdx - _frameTickCount + _frameTicks.Length) % _frameTicks.Length;
            double seconds = (now - _frameTicks[oldestIdx]) / (double)Stopwatch.Frequency;
            if (seconds > 0.005)
            {
                MeasuredFps = Math.Min(240.0, (_frameTickCount - 1) / seconds);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scaleFadeTimer.Stop();
        LaserSurface.Dispose();
    }
}
