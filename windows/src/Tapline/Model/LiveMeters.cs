namespace Tapline.Model;

/// High-rate visual data (meters, spectrum), updated ~30 times a second.
sealed class LiveMeters
{
    private DateTime lastClip = DateTime.MinValue;
    private DateTime holdTimeL = DateTime.MinValue;
    private DateTime holdTimeR = DateTime.MinValue;

    public float PeakL { get; private set; }
    public float PeakR { get; private set; }
    public float RmsL { get; private set; }
    public float RmsR { get; private set; }
    public float HoldL { get; private set; }
    public float HoldR { get; private set; }
    public bool Clipping { get; private set; }
    public float[] Spectrum { get; set; } = [];
    public double[] SpectrumFrequencies { get; set; } = [];

    /// Applies meter ballistics (fast attack, ~300 ms fall, 1.5 s peak hold).
    public void Ingest(float peakL, float peakR, float rmsL, float rmsR, uint clipped, double dt)
    {
        var fall = (float)Math.Exp(-dt / 0.3);
        PeakL = Math.Max(peakL, PeakL * fall);
        PeakR = Math.Max(peakR, PeakR * fall);
        RmsL = Math.Max(rmsL, RmsL * fall);
        RmsR = Math.Max(rmsR, RmsR * fall);
        var now = DateTime.UtcNow;
        if (peakL >= HoldL || (now - holdTimeL).TotalSeconds > 1.5) { HoldL = peakL; holdTimeL = now; }
        if (peakR >= HoldR || (now - holdTimeR).TotalSeconds > 1.5) { HoldR = peakR; holdTimeR = now; }
        if (clipped > 0) lastClip = now;
        Clipping = (now - lastClip).TotalSeconds < 2;
    }

    public void Reset()
    {
        PeakL = PeakR = RmsL = RmsR = HoldL = HoldR = 0;
        Clipping = false;
        Spectrum = [];
    }
}
