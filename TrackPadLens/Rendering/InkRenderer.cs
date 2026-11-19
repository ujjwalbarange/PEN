using System.Windows;
using System.Windows.Media;
using TrackPadLens.Models;

namespace TrackPadLens.Rendering;

/// <summary>
/// High-performance retained WPF DrawingVisual ink surface.
/// Each finalized stroke is stored in its own frozen DrawingVisual child so drawing a new stroke
/// ONLY updates a single active DrawingVisual incrementally (never redraws existing strokes).
/// </summary>
public sealed class InkRenderer : FrameworkElement
{
    private readonly VisualCollection _visuals;
    private readonly List<DrawingVisual> _finalizedStrokeVisuals = new();
    private readonly DrawingVisual _activeStrokeVisual = new();
    private readonly Dictionary<(Color, double, double, DrawingTool), Pen> _penCache = new();

    public InkRenderer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        _visuals = new VisualCollection(this) { _activeStrokeVisual };
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    public void UpdateActiveStroke(Stroke? stroke)
    {
        using DrawingContext dc = _activeStrokeVisual.RenderOpen();
        if (stroke != null)
        {
            DrawSmoothStroke(dc, stroke);
        }
    }

    public void CommitFinalizedStroke(Stroke stroke)
    {
        // Clear active visual
        using (DrawingContext activeDc = _activeStrokeVisual.RenderOpen()) { }

        // Render finalized stroke into its own retained DrawingVisual
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            DrawSmoothStroke(dc, stroke);
        }

        _finalizedStrokeVisuals.Add(visual);
        // Insert before _activeStrokeVisual so active stroke always renders on top
        _visuals.Insert(_visuals.Count - 1, visual);
    }

    public void RebuildFromStrokes(IReadOnlyList<Stroke> strokes)
    {
        foreach (var v in _finalizedStrokeVisuals)
        {
            _visuals.Remove(v);
        }
        _finalizedStrokeVisuals.Clear();

        using (DrawingContext activeDc = _activeStrokeVisual.RenderOpen()) { }

        foreach (var stroke in strokes)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                DrawSmoothStroke(dc, stroke);
            }
            _finalizedStrokeVisuals.Add(visual);
            _visuals.Insert(_visuals.Count - 1, visual);
        }
    }

    private void DrawSmoothStroke(DrawingContext dc, Stroke stroke)
    {
        int count = stroke.Count;
        if (count == 0) return;

        Pen pen = GetOrCreateFrozenPen(stroke.Color, stroke.Opacity, stroke.Thickness, stroke.Tool);

        if (count == 1)
        {
            var p = stroke.Points[0];
            double radius = stroke.Thickness * 0.5;
            dc.DrawEllipse(pen.Brush, null, new Point(p.X, p.Y), radius, radius);
            return;
        }

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (StreamGeometryContext ctx = geometry.Open())
        {
            var pts = stroke.Points;
            ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), isFilled: false, isClosed: false);

            if (count == 2)
            {
                ctx.LineTo(new Point(pts[1].X, pts[1].Y), isStroked: true, isSmoothJoin: true);
            }
            else
            {
                // Midpoint quadratic Bezier interpolation for silky-smooth C1 continuous ink
                for (int i = 1; i < count - 1; i++)
                {
                    var ctrl = pts[i];
                    var next = pts[i + 1];
                    double midX = (ctrl.X + next.X) * 0.5;
                    double midY = (ctrl.Y + next.Y) * 0.5;
                    ctx.QuadraticBezierTo(
                        new Point(ctrl.X, ctrl.Y),
                        new Point(midX, midY),
                        isStroked: true,
                        isSmoothJoin: true);
                }

                var last = pts[count - 1];
                ctx.LineTo(new Point(last.X, last.Y), isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private Pen GetOrCreateFrozenPen(Color color, double opacity, double thickness, DrawingTool tool)
    {
        var key = (color, opacity, thickness, tool);
        if (_penCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        byte alpha = (byte)Math.Clamp(Math.Round(opacity * 255.0), 1, 255);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();

        var cap = tool == DrawingTool.Highlighter ? PenLineCap.Flat : PenLineCap.Round;
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = cap,
            EndLineCap = cap,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        _penCache[key] = pen;
        return pen;
    }
}
