using System.Windows.Media;
using TrackPadLens.Models;

namespace TrackPadLens.Rendering;

/// <summary>
/// Manages permanent ink strokes, active drawing tool state, and bounded reversible history
/// (supporting single-stroke Undo and reversible Clear All without unbounded memory growth).
/// </summary>
public sealed class InkEngine
{
    public const int MaxUndoHistory = 128;

    private enum HistoryActionType { AddStroke, ClearAll }
    private sealed record HistoryEntry(HistoryActionType Type, Stroke? AddedStroke, List<Stroke>? ClearedStrokes);

    private readonly List<Stroke> _strokes = new();
    private readonly LinkedList<HistoryEntry> _undoHistory = new();

    public static readonly Color PenRed = Color.FromRgb(0xFF, 0x3B, 0x30);
    public static readonly Color PenBlue = Color.FromRgb(0x00, 0x7A, 0xFF);
    public static readonly Color HighlighterYellow = Color.FromRgb(0xFF, 0xCC, 0x00);
    public static readonly Color PenWhite = Colors.White;
    public static readonly Color PenBlack = Colors.Black;

    public DrawingTool ActiveTool { get; set; } = DrawingTool.Pen;
    public Color ActiveColor { get; set; } = PenRed;
    public StrokeThicknessPreset ThicknessPreset { get; set; } = StrokeThicknessPreset.Medium;

    public Stroke? ActiveStroke { get; private set; }
    public IReadOnlyList<Stroke> Strokes => _strokes;
    public int UndoDepth => _undoHistory.Count;

    public event Action<Stroke>? ActiveStrokeUpdated;
    public event Action<Stroke>? StrokeFinalized;
    public event Action? AllStrokesRebuilt;

    public void SelectPen(Color color)
    {
        ActiveTool = DrawingTool.Pen;
        ActiveColor = color;
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

    public bool Undo()
    {
        if (ActiveStroke != null)
        {
            ActiveStroke = null;
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
        else if (last.Type == HistoryActionType.ClearAll && last.ClearedStrokes != null)
        {
            _strokes.AddRange(last.ClearedStrokes);
        }

        AllStrokesRebuilt?.Invoke();
        return true;
    }

    /// <summary>
    /// Clears all visible ink while pushing a bounded snapshot onto the undo stack so
    /// an accidental Esc press can be reversed immediately with Ctrl+Z.
    /// </summary>
    public bool ClearAll()
    {
        if (ActiveStroke != null)
        {
            ActiveStroke = null;
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
}
