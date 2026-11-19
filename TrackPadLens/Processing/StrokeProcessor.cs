using TrackPadLens.Hardware;
using TrackPadLens.Models;
using TrackPadLens.Rendering;

namespace TrackPadLens.Processing;

public readonly record struct HoverDotState(bool Visible, double X, double Y);

/// <summary>
/// Real-time input processing pipeline:
/// TouchpadFrame -> PalmRejection -> 1-Euro Filter -> Absolute Coordinate Mapping
/// -> Variable Point Spacing -> InkEngine / LaserTrail / HoverDot.
/// </summary>
public sealed class StrokeProcessor
{
    private const double MinSampleSpacingDip = 1.25;

    private readonly OneEuroFilter2D _filter;
    private readonly PalmRejection _palmRejection;
    private readonly InkEngine _inkEngine;

    private bool _isStrokeActive;
    private double _lastEmittedX;
    private double _lastEmittedY;

    public bool IsDoodleModeEnabled { get; set; }
    public bool IsHardwareTestMode { get; set; }
    public HoverDotState CurrentHoverDot { get; private set; } = new(false, 0, 0);
    public PalmDecision LastPalmDecision { get; private set; } = PalmDecision.NoActiveContact;

    public event Action<HoverDotState>? HoverDotChanged;
    public event Action<StrokePoint>? LaserPointAdded;

    public StrokeProcessor(OneEuroFilter2D filter, PalmRejection palmRejection, InkEngine inkEngine)
    {
        _filter = filter;
        _palmRejection = palmRejection;
        _inkEngine = inkEngine;
    }

    public void ApplySettings(AppSettings settings)
    {
        _filter.UpdateParameters(settings.OneEuroMinCutoff, settings.OneEuroBeta, settings.OneEuroDCutoff);
        _palmRejection.Enabled = settings.PalmRejectionEnabled;
        _palmRejection.MultiFingerRejectionEnabled = settings.MultiFingerRejectionEnabled;
        _palmRejection.MaxContactWidthLogical = settings.PalmMaxContactWidthLogical;
        _palmRejection.MaxContactHeightLogical = settings.PalmMaxContactHeightLogical;
        _palmRejection.MaxContactAreaFraction = settings.PalmMaxAreaFraction;
    }

    public void ProcessTouchpadFrame(
        in TouchpadFrameEvent frame,
        in LensRectDip activeLens,
        CalibrationProfile calibration)
    {
        var caps = frame.Device.Capabilities;
        var logicalBounds = new LogicalBounds(caps.LogicalMinX, caps.LogicalMaxX, caps.LogicalMinY, caps.LogicalMaxY);

        var span = frame.Contacts.AsSpan(0, frame.ContactLength);
        var palmResult = _palmRejection.Evaluate(
            span,
            frame.ReportedContactCount,
            in logicalBounds,
            caps.SupportsContactArea);

        LastPalmDecision = palmResult.Decision;

        if (palmResult.Decision != PalmDecision.Accepted || !palmResult.PrimaryContact.HasValue)
        {
            EndActiveInteraction();
            return;
        }

        var contact = palmResult.PrimaryContact.Value;
        var mapped = CoordinateMapper.MapToLens(contact.RawX, contact.RawY, in logicalBounds, in activeLens, calibration);

        var (smoothX, smoothY) = _filter.Filter(mapped.ScreenXDip, mapped.ScreenYDip, frame.TimestampSeconds);

        UpdateHoverDot(true, smoothX, smoothY);

        if (!IsDoodleModeEnabled || IsHardwareTestMode)
        {
            EndStrokeOnly();
            return;
        }

        if (_inkEngine.ActiveTool == DrawingTool.Laser)
        {
            EndStrokeOnly();
            LaserPointAdded?.Invoke(new StrokePoint(smoothX, smoothY, frame.TimestampSeconds));
            return;
        }

        var pt = new StrokePoint(smoothX, smoothY, frame.TimestampSeconds);

        if (!_isStrokeActive)
        {
            _inkEngine.BeginStroke(pt);
            _isStrokeActive = true;
            _lastEmittedX = smoothX;
            _lastEmittedY = smoothY;
            return;
        }

        // Variable point spacing: avoid redundant sub-pixel geometry vertices while preserving sharp corners
        double dx = smoothX - _lastEmittedX;
        double dy = smoothY - _lastEmittedY;
        if (dx * dx + dy * dy >= MinSampleSpacingDip * MinSampleSpacingDip)
        {
            _inkEngine.AppendPoint(pt);
            _lastEmittedX = smoothX;
            _lastEmittedY = smoothY;
        }
    }

    public void CancelActiveStroke()
    {
        EndActiveInteraction();
    }

    private void EndActiveInteraction()
    {
        EndStrokeOnly();
        _filter.Reset();
        UpdateHoverDot(false, 0, 0);
    }

    private void EndStrokeOnly()
    {
        if (_isStrokeActive)
        {
            _inkEngine.EndStroke();
            _isStrokeActive = false;
        }
    }

    private void UpdateHoverDot(bool visible, double x, double y)
    {
        if (!visible && !CurrentHoverDot.Visible) return;
        CurrentHoverDot = new HoverDotState(visible, x, y);
        HoverDotChanged?.Invoke(CurrentHoverDot);
    }
}
