using System.Windows.Media;
using TrackPadLens.Models;

namespace TrackPadLens.Rendering;

/// <summary>
/// Bounded stroke container storing points in a contiguous array-backed list to minimize GC pressure.
/// </summary>
public sealed class Stroke
{
    public const int MaxPointsPerStroke = 4096;

    private readonly List<StrokePoint> _points;

    public Guid Id { get; } = Guid.NewGuid();
    public Color Color { get; }
    public double Opacity { get; }
    public double Thickness { get; }
    public DrawingTool Tool { get; }
    public IReadOnlyList<StrokePoint> Points => _points;
    public int Count => _points.Count;
    public bool IsFinalized { get; private set; }

    public Stroke(Color color, double opacity, double thickness, DrawingTool tool, int initialCapacity = 64)
    {
        Color = color;
        Opacity = Math.Clamp(opacity, 0.05, 1.0);
        Thickness = Math.Clamp(thickness, 1.0, 32.0);
        Tool = tool;
        _points = new List<StrokePoint>(initialCapacity);
    }

    public bool TryAddPoint(StrokePoint point)
    {
        if (IsFinalized || _points.Count >= MaxPointsPerStroke)
        {
            return false;
        }
        _points.Add(point);
        return true;
    }

    public void FinalizeStroke()
    {
        IsFinalized = true;
    }
}
