using TrackPadLens.Hardware;
using TrackPadLens.Models;
using TrackPadLens.Native;
using TrackPadLens.Processing;
using TrackPadLens.Rendering;
using Xunit;

namespace TrackPadLens.Tests;

public sealed class StrokeProcessorTests
{
    private static TouchpadDevice CreateTestDevice()
    {
        var caps = new HardwareCapabilityReport
        {
            DeviceName = "Test Precision Touchpad",
            HasUsableAbsoluteCoordinates = true,
            LogicalMinX = 0,
            LogicalMaxX = 1000,
            LogicalMinY = 0,
            LogicalMaxY = 1000,
            PhysicalWidthMm = 120.0,
            PhysicalHeightMm = 75.0,
            HasReliablePhysicalDimensions = true,
            SupportedContacts = 5,
            SupportsContactId = true,
            SupportsContactCount = true,
            SupportsTipSwitch = true,
            SupportsContactArea = false
        };
        return new TouchpadDevice(IntPtr.Zero, "TEST", null, null, caps);
    }

    [Fact]
    public void ProcessTouchpadFrame_HardwareTestModeNeverCreatesPermanentStrokes()
    {
        using var device = CreateTestDevice();
        var ink = new InkEngine();
        var processor = new StrokeProcessor(new OneEuroFilter2D(), new PalmRejection(), ink)
        {
            IsDoodleModeEnabled = true,
            IsHardwareTestMode = true
        };

        var lens = new LensRectDip(100, 100, 400, 250);
        var cal = new CalibrationProfile();
        var contacts = new[] { new TouchContact { ContactId = 1, TipSwitch = true, Confidence = true, RawX = 500, RawY = 500 } };

        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, contacts, 1, 1, 1.0), in lens, cal);
        Assert.True(processor.CurrentHoverDot.Visible);
        Assert.Null(ink.ActiveStroke);
        Assert.Empty(ink.Strokes);
    }

    [Fact]
    public void InkEngine_BoundedUndoAndReversibleClearWorkReliably()
    {
        var ink = new InkEngine();

        for (int i = 0; i < InkEngine.MaxUndoHistory + 25; i++)
        {
            ink.BeginStroke(new StrokePoint(i, i, i * 0.01));
            ink.AppendPoint(new StrokePoint(i + 10, i + 10, i * 0.01 + 0.005));
            ink.EndStroke();
        }

        Assert.Equal(InkEngine.MaxUndoHistory, ink.UndoDepth);
        int totalBeforeClear = ink.Strokes.Count;

        // Clear all strokes cleanly, then restore entire batch with a single Undo
        Assert.True(ink.ClearAll());
        Assert.Empty(ink.Strokes);

        Assert.True(ink.Undo());
        Assert.Equal(totalBeforeClear, ink.Strokes.Count);

        // Next undo removes the last stroke
        Assert.True(ink.Undo());
        Assert.Equal(totalBeforeClear - 1, ink.Strokes.Count);
    }

    [Fact]
    public void ProcessTouchpadFrame_DrawsAndFinalizesStrokeOnTouchLift()
    {
        using var device = CreateTestDevice();
        var ink = new InkEngine();
        var processor = new StrokeProcessor(new OneEuroFilter2D(), new PalmRejection(), ink)
        {
            IsDoodleModeEnabled = true,
            IsHardwareTestMode = false
        };

        var lens = new LensRectDip(0, 0, 500, 500);
        var cal = new CalibrationProfile();

        var p1 = new[] { new TouchContact { ContactId = 1, TipSwitch = true, Confidence = true, RawX = 100, RawY = 100 } };
        var p2 = new[] { new TouchContact { ContactId = 1, TipSwitch = true, Confidence = true, RawX = 300, RawY = 300 } };
        var lift = new[] { new TouchContact { ContactId = 1, TipSwitch = false, Confidence = true, RawX = 300, RawY = 300 } };

        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, p1, 1, 1, 1.00), in lens, cal);
        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, p2, 1, 1, 1.01), in lens, cal);
        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, lift, 1, 0, 1.02), in lens, cal);

        Assert.Single(ink.Strokes);
        Assert.True(ink.Strokes[0].IsFinalized);
        Assert.True(ink.Strokes[0].Count >= 2);
        Assert.False(processor.CurrentHoverDot.Visible);
    }

    [Fact]
    public void WpfVisualHosts_ConstructWithoutNullReferenceException()
    {
        Exception? staException = null;
        var thread = new Thread(() =>
        {
            try
            {
                var inkRenderer = new InkRenderer();
                using var laserRenderer = new LaserTrailRenderer();
                using var overlay = new TrackPadLens.UI.OverlayWindow();
                overlay.ApplyMonitorScope(MonitorScope.PrimaryOnly);
                overlay.ApplyLensAppearance(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81), 0.75);
                overlay.UpdateLens(new LensRectDip(100, 100, 420, 260), isAnchorLocked: false);
                Assert.NotNull(inkRenderer);
                Assert.NotNull(laserRenderer);
            }
            catch (Exception ex)
            {
                staException = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(staException);
    }

    [Fact]
    public void StrokeEraser_ErasesEntireTouchedLineAndUndoRestoresIt()
    {
        using var device = CreateTestDevice();
        var ink = new InkEngine();
        var processor = new StrokeProcessor(new OneEuroFilter2D(), new PalmRejection(), ink)
        {
            IsDoodleModeEnabled = true,
            IsHardwareTestMode = false
        };

        // Draw two horizontal lines: y = 100 (line A) and y = 400 (line B)
        ink.BeginStroke(new StrokePoint(50, 100, 1.0));
        ink.AppendPoint(new StrokePoint(450, 100, 1.1));
        ink.EndStroke();

        ink.BeginStroke(new StrokePoint(50, 400, 2.0));
        ink.AppendPoint(new StrokePoint(450, 400, 2.1));
        ink.EndStroke();

        Assert.Equal(2, ink.Strokes.Count);

        // Switch to Eraser and cross ONLY line A at (250, 100)
        ink.SelectEraser();
        var lens = new LensRectDip(0, 0, 500, 500);
        var cal = new CalibrationProfile();

        var e1 = new[] { new TouchContact { ContactId = 1, TipSwitch = true, Confidence = true, RawX = 500, RawY = 160 } }; // (250, 80)
        var e2 = new[] { new TouchContact { ContactId = 1, TipSwitch = true, Confidence = true, RawX = 500, RawY = 240 } }; // (250, 120)
        var lift = new[] { new TouchContact { ContactId = 1, TipSwitch = false, Confidence = true, RawX = 500, RawY = 240 } };

        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, e1, 1, 1, 3.00), in lens, cal);
        Assert.True(processor.CurrentHoverDot.Visible);
        Assert.True(processor.CurrentHoverDot.IsEraser);
        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, e2, 1, 1, 3.01), in lens, cal);
        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, lift, 1, 0, 3.02), in lens, cal);

        // Entire line A erased; line B untouched
        Assert.Single(ink.Strokes);
        Assert.Equal(400, ink.Strokes[0].Points[0].Y, 1);

        // Ctrl+Z cleanly restores the erased line
        Assert.True(ink.Undo());
        Assert.Equal(2, ink.Strokes.Count);
    }

    [Fact]
    public void InkEngine_SelectOrCyclePen_RestoresLastPenColorThenCyclesThroughAllColors()
    {
        var ink = new InkEngine();
        ink.SelectPen(InkEngine.PenBlue);
        ink.SelectEraser();
        Assert.Equal(DrawingTool.Eraser, ink.ActiveTool);

        // First press of D from Eraser returns to last-used Pen color (Blue)
        var restored = ink.SelectOrCyclePen();
        Assert.Equal(DrawingTool.Pen, ink.ActiveTool);
        Assert.Equal(InkEngine.PenBlue, restored);

        // Subsequent presses of D while already on Pen cycle through PenColorCycle: Blue -> White -> Black -> Red -> Blue
        Assert.Equal(InkEngine.PenWhite, ink.SelectOrCyclePen());
        Assert.Equal(InkEngine.PenBlack, ink.SelectOrCyclePen());
        Assert.Equal(InkEngine.PenRed, ink.SelectOrCyclePen());
        Assert.Equal(InkEngine.PenBlue, ink.SelectOrCyclePen());
    }

    [Fact]
    public void ProcessTouchpadFrame_TwoFingerScrollLandingAndLiftOffNeverLeavesSingleDot()
    {
        using var device = CreateTestDevice();
        var ink = new InkEngine();
        var processor = new StrokeProcessor(new OneEuroFilter2D(), new PalmRejection(), ink)
        {
            IsDoodleModeEnabled = true,
            IsHardwareTestMode = false
        };

        var lens = new LensRectDip(0, 0, 500, 500);
        var cal = new CalibrationProfile();

        // 1. Finger 1 lands 20ms before Finger 2
        var finger1Only = new[] { new TouchContact { ContactId = 1, TipSwitch = true, Confidence = true, RawX = 500, RawY = 500 } };
        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, finger1Only, 1, 1, 1.000), in lens, cal);

        // 2. Finger 2 lands -> multi-finger scroll detected
        var twoFingers = new[]
        {
            new TouchContact { ContactId = 1, TipSwitch = true, Confidence = true, RawX = 500, RawY = 510 },
            new TouchContact { ContactId = 2, TipSwitch = true, Confidence = true, RawX = 620, RawY = 510 }
        };
        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, twoFingers, 2, 2, 1.020), in lens, cal);
        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, twoFingers, 2, 2, 1.150), in lens, cal);

        // 3. Finger 2 lifts 20ms before Finger 1 (trailing single contact during lift-off)
        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, finger1Only, 1, 1, 1.170), in lens, cal);

        // 4. Both fingers lifted cleanly
        var lifted = new[] { new TouchContact { ContactId = 1, TipSwitch = false, Confidence = true, RawX = 500, RawY = 510 } };
        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, lifted, 1, 0, 1.190), in lens, cal);

        Assert.Null(ink.ActiveStroke);
        Assert.Empty(ink.Strokes);

        // 5. Subsequent intentional 1-finger tap still works cleanly
        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, finger1Only, 1, 1, 2.000), in lens, cal);
        processor.ProcessTouchpadFrame(new TouchpadFrameEvent(device, lifted, 1, 0, 2.030), in lens, cal);
        Assert.Single(ink.Strokes);
    }
}
