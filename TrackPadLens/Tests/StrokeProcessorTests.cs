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
}
