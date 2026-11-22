using System.Windows.Media;
using TrackPadLens.Models;

namespace TrackPadLens.Rendering;

/// <summary>
/// Manages permanent ink strokes, active drawing tool state (Pen, Highlighter, Laser, Stroke Eraser),
/// last-used pen memory with cycling, discardable rejected strokes, and bounded reversible history.
/// </summary>
public sealed class InkEngine
{
    public const int MaxUndoHistory = 128;

    private enum HistoryActionType { AddStroke, ClearAll, EraseStrokes }
    private sealed record HistoryEntry(HistoryActionType Type, Stroke? AddedStroke, List<Stroke>? BatchStrokes);

    private readonly List<Stroke> _strokes = new();
    private readonly LinkedList<HistoryEntry> _undoHistory = new();

    public static readonly Color PenRed = Color.FromRgb(0xFF, 0x3B, 0x30);
    public static readonly Color PenBlue = Color.FromRgb(0x00, 0x7A, 0xFF);
    public static readonly Color HighlighterYellow = Color.FromRgb(0xFF, 0xCC, 0x00);
    public static readonly Color PenWhite = Colors.White;
    public static readonly Color PenBlack = Colors.Black;

    public static readonly Color[] PenColorCycle = { PenRed, PenBlue, PenWhite, PenBlack };

    public DrawingTool ActiveTool { get; set; } = DrawingTool.Pen;
    public Color ActiveColor { get; set; } = PenRed;
    public Color LastPenColor { get; private set; } = PenRed;
    public StrokeThicknessPreset ThicknessPreset { get; set; } = StrokeThicknessPreset.Medium;

    public Stroke? ActiveStroke { get; private set; }
    public IReadOnlyList<Stroke> Strokes => _strokes;
    public int UndoDepth => _undoHistory.Count;

    public event Action<Stroke?>? ActiveStrokeUpdated;
    public event Action<Stroke>? StrokeFinalized;
    public event Action? AllStrokesRebuilt;

    public void SetInitialPenColor(Color color)
    {
        LastPenColor = color;
        if (ActiveTool == DrawingTool.Pen)
        {
            ActiveColor = color;
        }
    }

    public void SelectPen(Color color)
    {
        ActiveTool = DrawingTool.Pen;
        ActiveColor = color;
        LastPenColor = color;
    }

    /// <summary>
    /// D-key behavior: when coming from another tool (Eraser, Highlighter, Laser), restores the
    /// last-used Pen color; when already using Pen, cycles through all pen colors in order.
    /// </summary>
    public Color SelectOrCyclePen()
    {
        if (ActiveTool != DrawingTool.Pen)
        {
            SelectPen(LastPenColor);
            return LastPenColor;
        }

        int currentIdx = Array.IndexOf(PenColorCycle, ActiveColor);
        int nextIdx = currentIdx >= 0 ? (currentIdx + 1) % PenColorCycle.Length : 0;
        Color nextColor = PenColorCycle[nextIdx];
        SelectPen(nextColor);
        return nextColor;
    }

    public void SelectHighlighter()
    {
        ActiveTool = DrawingTool.Highlighter;
        ActiveColor = HighlighterYellow;
    }

    public void SelectLaser()
    {
        ActiveTool = DrawingTool.Laser;
    }

    public void SelectEraser()
    {
        ActiveTool = DrawingTool.Eraser;
    }

    public void BeginStroke(StrokePoint firstPoint)
    {
        if (ActiveStroke != null)
        {
            EndStroke();
        }

        bool isHighlighter = ActiveTool == DrawingTool.Highlighter;
        double opacity = isHighlighter ? 0.40 : 1.0;
        double baseThickness = (double)ThicknessPreset;
        double thickness = isHighlighter ? baseThickness * 3.5 : baseThickness;

        ActiveStroke = new Stroke(ActiveColor, opacity, thickness, ActiveTool);
        ActiveStroke.TryAddPoint(firstPoint);
        ActiveStrokeUpdated?.Invoke(ActiveStroke);
    }

    public void AppendPoint(StrokePoint point)
    {
        if (ActiveStroke == null) return;
        if (ActiveStroke.TryAddPoint(point))
        {
            ActiveStrokeUpdated?.Invoke(ActiveStroke);
        }
    }

    public void EndStroke()
    {
        if (ActiveStroke == null) return;

        var completed = ActiveStroke;
        completed.FinalizeStroke();
        ActiveStroke = null;

        _strokes.Add(completed);
        PushUndo(new HistoryEntry(HistoryActionType.AddStroke, completed, null));
        StrokeFinalized?.Invoke(completed);
    }

    /// <summary>
    /// Discards the currently in-progress stroke without committing it to permanent history
    /// (used when a 2-finger scroll or palm gesture triggers right after initial touch-down).
    /// </summary>
    public void DiscardActiveStroke()
    {
        if (ActiveStroke == null) return;
        ActiveStroke = null;
        ActiveStrokeUpdated?.Invoke(null);
    }

    /// <summary>
    /// Whole-stroke object eraser: removes every finalized stroke touched by the eraser segment
    /// (x0,y0)->(x1,y1) within eraserRadiusDip + half stroke thickness, pushing a reversible Undo entry.
    /// </summary>
    public int EraseStrokesIntersectingSegment(double x0, double y0, double x1, double y1, double eraserRadiusDip = 12.0)
    {
        if (_strokes.Count == 0) return 0;

        double segMinX = Math.Min(x0, x1);
        double segMaxX = Math.Max(x0, x1);
        double segMinY = Math.Min(y0, y1);
        double segMaxY = Math.Max(y0, y1);

        List<Stroke>? removed = null;

        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            var stroke = _strokes[i];
            double hitRadius = eraserRadiusDip + stroke.Thickness * 0.5;
            if (StrokeIntersectsSegment(stroke, x0, y0, x1, y1, segMinX, segMaxX, segMinY, segMaxY, hitRadius))
            {
                removed ??= new List<Stroke>();
                removed.Add(stroke);
                _strokes.RemoveAt(i);
            }
        }

        if (removed == null || removed.Count == 0)
        {
            return 0;
        }

        removed.Reverse();
        PushUndo(new HistoryEntry(HistoryActionType.EraseStrokes, null, removed));
        AllStrokesRebuilt?.Invoke();
        return removed.Count;
    }

    public bool Undo()
    {
        if (ActiveStroke != null)
        {
            ActiveStroke = null;
            ActiveStrokeUpdated?.Invoke(null);
        }

        if (_undoHistory.Count == 0)
        {
            return false;
        }

        var last = _undoHistory.Last!.Value;
        _undoHistory.RemoveLast();

        if (last.Type == HistoryActionType.AddStroke && last.AddedStroke != null)
        {
            _strokes.Remove(last.AddedStroke);
        }
        else if ((last.Type == HistoryActionType.ClearAll || last.Type == HistoryActionType.EraseStrokes) &&
                 last.BatchStrokes != null)
        {
            _strokes.AddRange(last.BatchStrokes);
        }

        AllStrokesRebuilt?.Invoke();
        return true;
    }

    /// <summary>
    /// Clears all visible ink while pushing a bounded snapshot onto the undo stack so
    /// an accidental Clear can be reversed immediately with Ctrl+Z.
    /// </summary>
    public bool ClearAll()
    {
        if (ActiveStroke != null)
        {
            ActiveStroke = null;
            ActiveStrokeUpdated?.Invoke(null);
        }

        if (_strokes.Count == 0)
        {
            return false;
        }

        var snapshot = new List<Stroke>(_strokes);
        _strokes.Clear();
        PushUndo(new HistoryEntry(HistoryActionType.ClearAll, null, snapshot));
        AllStrokesRebuilt?.Invoke();
        return true;
    }

    private void PushUndo(HistoryEntry entry)
    {
        _undoHistory.AddLast(entry);
        while (_undoHistory.Count > MaxUndoHistory)
        {
            _undoHistory.RemoveFirst();
        }
    }

    private static bool StrokeIntersectsSegment(
        Stroke stroke,
        double ax, double ay, double bx, double by,
        double segMinX, double segMaxX, double segMinY, double segMaxY,
        double hitRadius)
    {
        var pts = stroke.Points;
        int count = pts.Count;
        if (count == 0) return false;

        double hitRadiusSq = hitRadius * hitRadius;

        if (count == 1)
        {
            return PointToSegmentDistanceSq(pts[0].X, pts[0].Y, ax, ay, bx, by) <= hitRadiusSq;
        }

        for (int i = 0; i < count - 1; i++)
        {
            var p0 = pts[i];
            var p1 = pts[i + 1];

            // Fast AABB rejection before exact segment-segment distance
            if (Math.Max(p0.X, p1.X) + hitRadius < segMinX ||
                Math.Min(p0.X, p1.X) - hitRadius > segMaxX ||
                Math.Max(p0.Y, p1.Y) + hitRadius < segMinY ||
                Math.Min(p0.Y, p1.Y) - hitRadius > segMaxY)
            {
                continue;
            }

            if (SegmentToSegmentDistanceSq(ax, ay, bx, by, p0.X, p0.Y, p1.X, p1.Y) <= hitRadiusSq)
            {
                return true;
            }
        }

        return false;
    }

    private static double SegmentToSegmentDistanceSq(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        if (SegmentsIntersect(ax, ay, bx, by, cx, cy, dx, dy))
        {
            return 0.0;
        }

        double d1 = PointToSegmentDistanceSq(ax, ay, cx, cy, dx, dy);
        double d2 = PointToSegmentDistanceSq(bx, by, cx, cy, dx, dy);
        double d3 = PointToSegmentDistanceSq(cx, cy, ax, ay, bx, by);
        double d4 = PointToSegmentDistanceSq(dx, dy, ax, ay, bx, by);
        return Math.Min(Math.Min(d1, d2), Math.Min(d3, d4));
    }

    private static double PointToSegmentDistanceSq(double px, double py, double ax, double ay, double bx, double by)
    {
        double abx = bx - ax;
        double aby = by - ay;
        double lenSq = abx * abx + aby * aby;
        if (lenSq <= 1e-9)
        {
            double dx0 = px - ax;
            double dy0 = py - ay;
            return dx0 * dx0 + dy0 * dy0;
        }

        double t = Math.Clamp(((px - ax) * abx + (py - ay) * aby) / lenSq, 0.0, 1.0);
        double projX = ax + t * abx;
        double projY = ay + t * aby;
        double dx = px - projX;
        double dy = py - projY;
        return dx * dx + dy * dy;
    }

    private static bool SegmentsIntersect(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        double o1 = Cross(bx - ax, by - ay, cx - ax, cy - ay);
        double o2 = Cross(bx - ax, by - ay, dx - ax, dy - ay);
        double o3 = Cross(dx - cx, dy - cy, ax - cx, ay - cy);
        double o4 = Cross(dx - cx, dy - cy, bx - cx, by - cy);

        return ((o1 > 0 && o2 < 0) || (o1 < 0 && o2 > 0)) &&
               ((o3 > 0 && o4 < 0) || (o3 < 0 && o4 > 0));
    }

    private static double Cross(double x1, double y1, double x2, double y2) => x1 * y2 - y1 * x2;
}
