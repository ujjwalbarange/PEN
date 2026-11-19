using TrackPadLens.Models;
using TrackPadLens.Processing;
using Xunit;

namespace TrackPadLens.Tests;

public sealed class CoordinateMapperTests
{
    [Fact]
    public void Normalize_ClampsOutOfRangeRawCoordinatesToUnitInterval()
    {
        var bounds = new LogicalBounds(0, 1200, 0, 800);
        var (uLow, vLow) = CoordinateMapper.Normalize(-50, -20, in bounds);
        var (uHigh, vHigh) = CoordinateMapper.Normalize(1400, 999, in bounds);

        Assert.Equal(0.0, uLow);
        Assert.Equal(0.0, vLow);
        Assert.Equal(1.0, uHigh);
        Assert.Equal(1.0, vHigh);
    }

    [Fact]
    public void MapToLens_MapsCenterRawSampleToExactLensCenter()
    {
        var bounds = new LogicalBounds(100, 1100, 200, 1000);
        var lens = new LensRectDip(300, 150, 400, 250);

        var mapped = CoordinateMapper.MapToLens(600, 600, in bounds, in lens);

        Assert.Equal(0.5, mapped.NormalizedU, 6);
        Assert.Equal(0.5, mapped.NormalizedV, 6);
        Assert.Equal(500.0, mapped.ScreenXDip, 6);
        Assert.Equal(275.0, mapped.ScreenYDip, 6);
    }

    [Fact]
    public void Normalize_RespectsCalibrationActiveCropAndAxisInversion()
    {
        var bounds = new LogicalBounds(0, 1000, 0, 1000);
        var profile = new CalibrationProfile
        {
            ActiveLeft = 0.2,
            ActiveRight = 0.8,
            ActiveTop = 0.1,
            ActiveBottom = 0.9,
            InvertX = true,
            InvertY = false
        };

        // Raw X=200 corresponds to ActiveLeft -> u=0 before invert -> u=1 after InvertX
        var (u, v) = CoordinateMapper.Normalize(200, 500, in bounds, profile);
        Assert.Equal(1.0, u, 6);
        Assert.Equal(0.5, v, 6);
    }

    [Fact]
    public void PhysicalToDipAndBack_PreservesNegativeSecondaryMonitorCoordinatesUnder150PercentDpi()
    {
        double dpiScale = 1.5; // 144 DPI (150% Windows scaling)
        var (dipX, dipY) = CoordinateMapper.PhysicalToDip(-1920, -300, dpiScale, dpiScale);

        Assert.Equal(-1280.0, dipX, 6);
        Assert.Equal(-200.0, dipY, 6);

        var (pxX, pxY) = CoordinateMapper.DipToPhysical(dipX, dipY, dpiScale, dpiScale);
        Assert.Equal(-1920, pxX);
        Assert.Equal(-300, pxY);
    }

    [Fact]
    public void ComputeLensRect_ClampsInsideNegativeMultiMonitorVirtualScreenAndPreservesAspectRatio()
    {
        var virtualBounds = new LensRectDip(-1920, 0, 3840, 1080);
        var lens = CoordinateMapper.ComputeLensRect(
            cursorDipX: -1910,
            cursorDipY: 500,
            lensWidthDip: 400,
            aspectRatio: 1.6,
            anchorMode: AnchorMode.Centered,
            offsetX: 0,
            offsetY: 0,
            in virtualBounds);

        Assert.Equal(-1920.0, lens.Left, 6);
        Assert.Equal(400.0, lens.Width, 6);
        Assert.Equal(250.0, lens.Height, 6);
    }
}
