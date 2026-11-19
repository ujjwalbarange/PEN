using TrackPadLens.Processing;
using Xunit;

namespace TrackPadLens.Tests;

public sealed class OneEuroFilterTests
{
    [Fact]
    public void Filter_ReturnsExactFirstSampleWithoutLag()
    {
        var filter = new OneEuroFilter(minCutoff: 1.8, beta: 0.025, dCutoff: 1.0);
        double first = filter.Filter(125.5, 10.0);
        Assert.Equal(125.5, first, 8);
    }

    [Fact]
    public void Filter_SuppressesHighFrequencyJitterOnStationaryInput()
    {
        var filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.01, dCutoff: 1.0);
        double t = 0.0;
        double maxDeviation = 0.0;

        for (int i = 0; i < 80; i++)
        {
            double noisy = 500.0 + (i % 2 == 0 ? 4.0 : -4.0);
            double output = filter.Filter(noisy, t);
            if (i >= 40)
            {
                maxDeviation = Math.Max(maxDeviation, Math.Abs(output - 500.0));
            }
            t += 1.0 / 120.0; // 120 Hz HID report rate
        }

        Assert.True(maxDeviation < 1.5, $"Expected jitter suppression below 1.5 DIP, got {maxDeviation:F3}");
    }

    [Fact]
    public void Filter_TracksFastRampWithLowLatency()
    {
        var filter = new OneEuroFilter(minCutoff: 1.5, beta: 0.08, dCutoff: 1.0);
        double t = 0.0;
        double lastOutput = 0.0;
        double target = 0.0;

        for (int i = 0; i < 40; i++)
        {
            target = i * 15.0; // 1800 DIP/sec fast stroke
            lastOutput = filter.Filter(target, t);
            t += 1.0 / 120.0;
        }

        Assert.True(Math.Abs(target - lastOutput) < 12.0, $"Expected low fast-motion lag, delta was {Math.Abs(target - lastOutput):F2}");
    }

    [Fact]
    public void Filter_HandlesDuplicateOrOutOfOrderTimestampsSafely()
    {
        var filter = new OneEuroFilter();
        double a = filter.Filter(100.0, 1.0);
        double b = filter.Filter(200.0, 1.0); // duplicate timestamp
        double c = filter.Filter(300.0, 0.9); // out of order

        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.False(double.IsNaN(c));
    }
}
