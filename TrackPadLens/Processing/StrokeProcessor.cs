using TrackPadLens.Hardware;
using TrackPadLens.Models;
using TrackPadLens.Rendering;

namespace TrackPadLens.Processing;

public readonly record struct HoverDotState(bool Visible, double X, double Y, bool IsEraser = false);

/// <summary>
/// Real-time input processing pipeline:
/// TouchpadFrame -> PalmRejection -> 1-Euro Filter -> Absolute Coordinate Mapping
/// -> Landing Confirmation Buffer (eliminates 2-finger scroll accidental dots)
/// -> Variable Point Spacing -> InkEngine (Pen/Highlighter/Stroke Eraser) / LaserTrail / HoverDot.
/// </summary>
public sealed class StrokeProcessor
{
    private const double MinSampleSpacingDip = 1.25;
    private const double StrokeConfirmDelaySeconds = 0.045;
    private const double StrokeConfirmMoveDip = 6.0;
    private const double RejectDiscardWindowSeconds = 0.140;
    private const double RejectDiscardMaxTravelDip = 14.0;
    private const double EraserRadiusDip = 12.0;

    private readonly OneEuroFilter2D _filter;
    private readonly PalmRejection _palmRejection;
    private readonly InkEngine _inkEngine;
    private readonly List<StrokePoint> _pendingPoints = new(16);

    private bool _isStrokeActive;
    private bool _isErasing;
    private bool _awaitingAllContactsLiftAfterRejection;
    private double _strokeStartTimestamp;
    private double _strokeStartX;
    private double _strokeStartY;
    private double _strokeMaxTravelSq;
    private double _lastEmittedX;
    private double _lastEmittedY;
    private double _lastEraseX;
    private double _lastEraseY;

    public bool IsDoodleModeEnabled { get; set; }
    public bool IsHardwareTestMode { get; set; }
    public HoverDotState CurrentHoverDot { get; private set; } = new(false, 0, 0, false);
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
            if (palmResult.Decision == PalmDecision.NoActiveContact)
            {
                if (_awaitingAllContactsLiftAfterRejection)
                {
                    // All fingers have now lifted cleanly after a multi-finger scroll or palm gesture
                    _awaitingAllContactsLiftAfterRejection = false;
                    _pendingPoints.Clear();
                    EndActiveInteraction(discardShortStroke: true, currentTimestamp: frame.TimestampSeconds);
                    return;
                }

                // Clean single-finger lift: commit an intentional tap or active stroke
                CommitPendingAsTapIfNeeded();
                EndActiveInteraction(discardShortStroke: false, currentTimestamp: frame.TimestampSeconds);
            }
            else
            {
                // Multi-finger scroll, palm, or low-confidence rejection:
                // discard any pending/just-started landing dot and stay suppressed until all fingers lift.
                _awaitingAllContactsLiftAfterRejection = true;
                _pendingPoints.Clear();
                EndActiveInteraction(discardShortStroke: true, currentTimestamp: frame.TimestampSeconds);
            }
            return;
        }

        if (_awaitingAllContactsLiftAfterRejection)
        {
            // Trailing single finger left briefly during 2-finger scroll lift-off: ignore until full lift
            return;
        }

        var contact = palmResult.PrimaryContact.Value;
        var mapped = CoordinateMapper.MapToLens(contact.RawX, contact.RawY, in logicalBounds, in activeLens, calibration);

        var (smoothX, smoothY) = _filter.Filter(mapped.ScreenXDip, mapped.ScreenYDip, frame.TimestampSeconds);

        bool isEraser = _inkEngine.ActiveTool == DrawingTool.Eraser;
        UpdateHoverDot(true, smoothX, smoothY, isEraser);

        if (!IsDoodleModeEnabled || IsHardwareTestMode)
        {
            _pendingPoints.Clear();
            EndStrokeOnly(discard: false);
            _isErasing = false;
            return;
        }

        if (_inkEngine.ActiveTool == DrawingTool.Laser)
        {
            _pendingPoints.Clear();
            EndStrokeOnly(discard: false);
            _isErasing = false;
            LaserPointAdded?.Invoke(new StrokePoint(smoothX, smoothY, frame.TimestampSeconds));
            return;
        }

        if (isEraser)
        {
            _pendingPoints.Clear();
            EndStrokeOnly(discard: false);
            if (!_isErasing)
            {
                _isErasing = true;
                _lastEraseX = smoothX;
                _lastEraseY = smoothY;
                _inkEngine.EraseStrokesIntersectingSegment(smoothX, smoothY, smoothX, smoothY, EraserRadiusDip);
            }
            else
            {
                _inkEngine.EraseStrokesIntersectingSegment(_lastEraseX, _lastEraseY, smoothX, smoothY, EraserRadiusDip);
                _lastEraseX = smoothX;
                _lastEraseY = smoothY;
            }
            return;
        }

        _isErasing = false;
        var pt = new StrokePoint(smoothX, smoothY, frame.TimestampSeconds);

        if (!_isStrokeActive)
        {
            if (_pendingPoints.Count == 0)
            {
                _strokeStartTimestamp = frame.TimestampSeconds;
                _strokeStartX = smoothX;
                _strokeStartY = smoothY;
                _strokeMaxTravelSq = 0.0;
                _pendingPoints.Add(pt);
                return;
            }

            double tdx = smoothX - _strokeStartX;
            double tdy = smoothY - _strokeStartY;
            double travelSq = tdx * tdx + tdy * tdy;
            if (travelSq > _strokeMaxTravelSq) _strokeMaxTravelSq = travelSq;

            var lastPending = _pendingPoints[_pendingPoints.Count - 1];
            double pdx = smoothX - lastPending.X;
            double pdy = smoothY - lastPending.Y;
            if (pdx * pdx + pdy * pdy >= MinSampleSpacingDip * MinSampleSpacingDip)
            {
                _pendingPoints.Add(pt);
            }

            double elapsed = frame.TimestampSeconds - _strokeStartTimestamp;
            if (elapsed >= StrokeConfirmDelaySeconds || _strokeMaxTravelSq >= StrokeConfirmMoveDip * StrokeConfirmMoveDip)
            {
                FlushPendingIntoActiveStroke();
            }
            return;
        }

        double sdx = smoothX - _strokeStartX;
        double sdy = smoothY - _strokeStartY;
        double curTravelSq = sdx * sdx + sdy * sdy;
        if (curTravelSq > _strokeMaxTravelSq) _strokeMaxTravelSq = curTravelSq;

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
        _awaitingAllContactsLiftAfterRejection = false;
        _pendingPoints.Clear();
        EndActiveInteraction(discardShortStroke: false, currentTimestamp: _strokeStartTimestamp + 1.0);
    }

    private void CommitPendingAsTapIfNeeded()
    {
        if (_pendingPoints.Count == 0 || !IsDoodleModeEnabled || IsHardwareTestMode)
        {
            _pendingPoints.Clear();
            return;
        }

        FlushPendingIntoActiveStroke();
    }

    private void FlushPendingIntoActiveStroke()
    {
        if (_pendingPoints.Count == 0) return;

        _inkEngine.BeginStroke(_pendingPoints[0]);
        for (int i = 1; i < _pendingPoints.Count; i++)
        {
            _inkEngine.AppendPoint(_pendingPoints[i]);
        }

        var last = _pendingPoints[_pendingPoints.Count - 1];
        _lastEmittedX = last.X;
        _lastEmittedY = last.Y;
        _pendingPoints.Clear();
        _isStrokeActive = true;
    }

    private void EndActiveInteraction(bool discardShortStroke, double currentTimestamp)
    {
        bool shouldDiscard = discardShortStroke && _isStrokeActive &&
                             ((currentTimestamp - _strokeStartTimestamp) <= RejectDiscardWindowSeconds ||
                              _strokeMaxTravelSq <= RejectDiscardMaxTravelDip * RejectDiscardMaxTravelDip);

        EndStrokeOnly(discard: shouldDiscard);
        _isErasing = false;
        _filter.Reset();
        UpdateHoverDot(false, 0, 0, false);
    }

    private void EndStrokeOnly(bool discard)
    {
        if (!_isStrokeActive) return;

        if (discard)
        {
            _inkEngine.DiscardActiveStroke();
        }
        else
        {
            _inkEngine.EndStroke();
        }
        _isStrokeActive = false;
    }

    private void UpdateHoverDot(bool visible, double x, double y, bool isEraser)
    {
        if (!visible && !CurrentHoverDot.Visible) return;
        CurrentHoverDot = new HoverDotState(visible, x, y, isEraser);
        HoverDotChanged?.Invoke(CurrentHoverDot);
    }
}
