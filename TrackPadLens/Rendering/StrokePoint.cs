namespace TrackPadLens.Rendering;

/// <summary>
/// Value-type ink point (zero per-point heap allocation inside stroke buffers).
/// </summary>
public readonly record struct StrokePoint(double X, double Y, double TimestampSeconds);
