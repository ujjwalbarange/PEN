using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace TrackPadLens.Rendering;

/// <summary>
/// Renders a red glowing laser pointer trail that automatically fades out after 1.5 seconds.
/// Uses a bounded ring buffer and subscribes to CompositionTarget.Rendering ONLY while
/// active trail samples exist, guaranteeing 0% CPU overhead when idle.
/// </summary>
public sealed class LaserTrailRenderer : FrameworkElement, IDisposable
{
    public const double TrailLifetimeSeconds = 1.5;
    private const int RingCapacity = 256;

    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _trailVisual = new();
    private readonly StrokePoint[] _ring = new StrokePoint[RingCapacity];

    private int _head;
    private int _count;
    private bool _renderingHooked;
    private bool _disposed;

    public LaserTrailRenderer()
    {
        IsHitTestVisible = false;
        _visuals = new VisualCollection(this);
        _visuals.Add(_trailVisual);
    }

    protected override int VisualChildrenCount => _visuals?.Count ?? 0;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    public int ActivePointCount => _count;

    public void AddPoint(StrokePoint point)
    {
        if (_disposed) return;

        int tail = (_head + _count) % RingCapacity;
        _ring[tail] = point;

        if (_count == RingCapacity)
        {
            _head = (_head + 1) % RingCapacity;
        }
        else
        {
            _count++;
        }

        if (!_renderingHooked)
        {
            _renderingHooked = true;
            CompositionTarget.Rendering += OnCompositionTargetRendering;
        }
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        using (DrawingContext dc = _trailVisual.RenderOpen()) { }
        DetachRenderingLoop();
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        double nowSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        PruneExpired(nowSec);

        using DrawingContext dc = _trailVisual.RenderOpen();
        if (_count == 0)
        {
            DetachRenderingLoop();
            return;
        }

        if (_count == 1)
        {
            var pt = _ring[_head];
            double life = Math.Clamp(1.0 - (nowSec - pt.TimestampSeconds) / TrailLifetimeSeconds, 0.0, 1.0);
            DrawLaserHead(dc, pt.X, pt.Y, life);
            return;
        }

        for (int i = 0; i < _count - 1; i++)
        {
            var a = _ring[(_head + i) % RingCapacity];
            var b = _ring[(_head + i + 1) % RingCapacity];

            // Avoid connecting disjoint strokes after a >120ms lift gap
            if (b.TimestampSeconds - a.TimestampSeconds > 0.12)
            {
                continue;
            }

            double age = nowSec - b.TimestampSeconds;
            double life = Math.Clamp(1.0 - age / TrailLifetimeSeconds, 0.0, 1.0);
            if (life <= 0.01) continue;

            // Soft outer glow halo
            byte glowAlpha = (byte)(life * life * 115);
            var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(glowAlpha, 255, 45, 45)), 12.0 * life + 3.0)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawLine(glowPen, new Point(a.X, a.Y), new Point(b.X, b.Y));

            // Bright core beam
            byte coreAlpha = (byte)(life * 255);
            var corePen = new Pen(new SolidColorBrush(Color.FromArgb(coreAlpha, 255, 195, 195)), 3.2 * life + 1.0)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawLine(corePen, new Point(a.X, a.Y), new Point(b.X, b.Y));
        }

        var latest = _ring[(_head + _count - 1) % RingCapacity];
        double headLife = Math.Clamp(1.0 - (nowSec - latest.TimestampSeconds) / TrailLifetimeSeconds, 0.0, 1.0);
        if (headLife > 0.05)
        {
            DrawLaserHead(dc, latest.X, latest.Y, headLife);
        }
    }

    private static void DrawLaserHead(DrawingContext dc, double x, double y, double life)
    {
        var center = new Point(x, y);
        byte outerAlpha = (byte)(life * 140);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(outerAlpha, 255, 40, 40)), null, center, 9.0 * life, 9.0 * life);
        byte innerAlpha = (byte)(life * 255);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(innerAlpha, 255, 240, 240)), null, center, 3.5 * life, 3.5 * life);
    }

    private void PruneExpired(double nowSec)
    {
        while (_count > 0)
        {
            var oldest = _ring[_head];
            if (nowSec - oldest.TimestampSeconds >= TrailLifetimeSeconds)
            {
                _head = (_head + 1) % RingCapacity;
                _count--;
            }
            else
            {
                break;
            }
        }
    }

    private void DetachRenderingLoop()
    {
        if (_renderingHooked)
        {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            _renderingHooked = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachRenderingLoop();
    }
}
