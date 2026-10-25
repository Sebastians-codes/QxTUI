namespace QxTui.Core;

/// <summary>
/// Sample-a-source-at-interval + exponential smoothing + render-time
/// interpolation, in one object — the pattern every qxtop panel hand-rolled
/// three times. Update() once per render; Value/Phase give the interpolated
/// readout and the sub-sample scroll fraction.
/// </summary>
public sealed class SmoothedSampler(Func<double> source, double intervalMs, double alpha = 0.35)
{
    private double _prev, _curr;
    private long _lastTs;
    private bool _init;

    /// <summary>Smoothed value at the last sample.</summary>
    public double Current => _curr;

    /// <summary>Interpolated value for "now" (call Update first).</summary>
    public double Value { get; private set; }

    /// <summary>0..1 position between the last two samples (sparkline phase).</summary>
    public float Phase { get; private set; }

    /// <summary>True when this call took a new sample from the source.</summary>
    public bool Update(long nowMs)
    {
        bool sampled = false;
        if (!_init)
        {
            _prev = _curr = source();
            _lastTs = nowMs;
            _init = true;
            sampled = true;
        }
        else if (nowMs - _lastTs >= intervalMs)
        {
            _prev = _curr;
            _curr += (source() - _curr) * alpha;
            _lastTs = nowMs;
            sampled = true;
        }

        float f = (float)Math.Clamp((nowMs - _lastTs) / intervalMs, 0, 1);
        Phase = f;
        Value = _prev + (_curr - _prev) * f;
        return sampled;
    }
}
