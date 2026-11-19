namespace TrackPadLens.Processing;

/// <summary>
/// 1-Euro Filter (Casiez, Roussel, Vogel — CHI 2012) for low-latency adaptive jitter reduction.
/// Uses timestamped input samples to handle varying HID report rates (60Hz–240Hz+) without
/// allocating objects during drawing.
/// </summary>
public sealed class OneEuroFilter
{
    private double _minCutoff;
    private double _beta;
    private double _dCutoff;

    private bool _initialized;
    private double _lastTimestamp;
    private double _lastRawValue;
    private double _lastFilteredValue;
    private double _lastFilteredDerivative;

    public double MinCutoff
    {
        get => _minCutoff;
        set => _minCutoff = Math.Max(0.01, value);
    }

    public double Beta
    {
        get => _beta;
        set => _beta = Math.Max(0.0, value);
    }

    public double DCutoff
    {
        get => _dCutoff;
        set => _dCutoff = Math.Max(0.01, value);
    }

    public OneEuroFilter(double minCutoff = 1.8, double beta = 0.025, double dCutoff = 1.0)
    {
        _minCutoff = Math.Max(0.01, minCutoff);
        _beta = Math.Max(0.0, beta);
        _dCutoff = Math.Max(0.01, dCutoff);
    }

    public void Reset()
    {
        _initialized = false;
        _lastTimestamp = 0;
        _lastRawValue = 0;
        _lastFilteredValue = 0;
        _lastFilteredDerivative = 0;
    }

    public double Filter(double value, double timestampSeconds)
    {
        if (!_initialized)
        {
            _initialized = true;
            _lastTimestamp = timestampSeconds;
            _lastRawValue = value;
            _lastFilteredValue = value;
            _lastFilteredDerivative = 0.0;
            return value;
        }

        double dt = timestampSeconds - _lastTimestamp;
        if (dt <= 1e-6)
        {
            // Guard against duplicate or out-of-order HID packet timestamps
            return _lastFilteredValue;
        }

        // Clamp abnormally large pauses (e.g., missed frames) to keep filter responsive
        if (dt > 0.25)
        {
            dt = 0.25;
        }

        _lastTimestamp = timestampSeconds;

        double dx = (value - _lastFilteredValue) / dt;
        _lastRawValue = value;

        double alphaD = ComputeAlpha(_dCutoff, dt);
        double edX = LowPass(dx, _lastFilteredDerivative, alphaD);
        _lastFilteredDerivative = edX;

        double cutoff = _minCutoff + _beta * Math.Abs(edX);
        double alpha = ComputeAlpha(cutoff, dt);
        double filtered = LowPass(value, _lastFilteredValue, alpha);
        _lastFilteredValue = filtered;

        return filtered;
    }

    private static double ComputeAlpha(double cutoffHz, double dtSeconds)
    {
        double tau = 1.0 / (2.0 * Math.PI * cutoffHz);
        return 1.0 / (1.0 + tau / dtSeconds);
    }

    private static double LowPass(double current, double previous, double alpha)
    {
        return alpha * current + (1.0 - alpha) * previous;
    }
}

/// <summary>
/// Paired 2D 1-Euro Filter for absolute X/Y pointer coordinates.
/// </summary>
public sealed class OneEuroFilter2D
{
    private readonly OneEuroFilter _x;
    private readonly OneEuroFilter _y;

    public OneEuroFilter2D(double minCutoff = 1.8, double beta = 0.025, double dCutoff = 1.0)
    {
        _x = new OneEuroFilter(minCutoff, beta, dCutoff);
        _y = new OneEuroFilter(minCutoff, beta, dCutoff);
    }

    public void UpdateParameters(double minCutoff, double beta, double dCutoff)
    {
        _x.MinCutoff = minCutoff;
        _x.Beta = beta;
        _x.DCutoff = dCutoff;
        _y.MinCutoff = minCutoff;
        _y.Beta = beta;
        _y.DCutoff = dCutoff;
    }

    public void Reset()
    {
        _x.Reset();
        _y.Reset();
    }

    public (double X, double Y) Filter(double x, double y, double timestampSeconds)
    {
        return (_x.Filter(x, timestampSeconds), _y.Filter(y, timestampSeconds));
    }
}
