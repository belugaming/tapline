namespace Tapline.Core;

/// <summary>Normalized biquad coefficients (a0 == 1), RBJ Audio EQ Cookbook designs.</summary>
public readonly record struct BiquadCoefficients
{
    public double B0 { get; init; }
    public double B1 { get; init; }
    public double B2 { get; init; }
    public double A1 { get; init; }
    public double A2 { get; init; }

    public static readonly BiquadCoefficients Identity = new(1, 0, 0, 0, 0);

    public BiquadCoefficients(double b0, double b1, double b2, double a1, double a2)
    {
        B0 = b0;
        B1 = b1;
        B2 = b2;
        A1 = a1;
        A2 = a2;
    }

    public BiquadCoefficients(EqBand band, double sampleRate)
    {
        this = Design(band.Type, band.Frequency, band.Gain, band.Q, sampleRate);
    }

    public static BiquadCoefficients Design(FilterType type, double frequency, double gainDb, double q, double sampleRate)
    {
        double nyquist = sampleRate / 2;
        double f0 = Math.Min(Math.Max(frequency, 1), nyquist * 0.995);
        q = Math.Max(q, 0.01);
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * f0 / sampleRate;
        double cosW = Math.Cos(w0);
        double sinW = Math.Sin(w0);
        double alpha = sinW / (2 * q);

        double b0 = 1, b1 = 0, b2 = 0, a0 = 1, a1 = 0, a2 = 0;
        switch (type)
        {
            case FilterType.Peak:
                b0 = 1 + alpha * a;
                b1 = -2 * cosW;
                b2 = 1 - alpha * a;
                a0 = 1 + alpha / a;
                a1 = -2 * cosW;
                a2 = 1 - alpha / a;
                break;
            case FilterType.LowShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - (a - 1) * cosW + s);
                b1 = 2 * a * ((a - 1) - (a + 1) * cosW);
                b2 = a * ((a + 1) - (a - 1) * cosW - s);
                a0 = (a + 1) + (a - 1) * cosW + s;
                a1 = -2 * ((a - 1) + (a + 1) * cosW);
                a2 = (a + 1) + (a - 1) * cosW - s;
                break;
            }
            case FilterType.HighShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) + (a - 1) * cosW + s);
                b1 = -2 * a * ((a - 1) + (a + 1) * cosW);
                b2 = a * ((a + 1) + (a - 1) * cosW - s);
                a0 = (a + 1) - (a - 1) * cosW + s;
                a1 = 2 * ((a - 1) - (a + 1) * cosW);
                a2 = (a + 1) - (a - 1) * cosW - s;
                break;
            }
            case FilterType.LowPass:
                b0 = (1 - cosW) / 2;
                b1 = 1 - cosW;
                b2 = (1 - cosW) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cosW;
                a2 = 1 - alpha;
                break;
            case FilterType.HighPass:
                b0 = (1 + cosW) / 2;
                b1 = -(1 + cosW);
                b2 = (1 + cosW) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cosW;
                a2 = 1 - alpha;
                break;
            case FilterType.Notch:
                b0 = 1;
                b1 = -2 * cosW;
                b2 = 1;
                a0 = 1 + alpha;
                a1 = -2 * cosW;
                a2 = 1 - alpha;
                break;
            case FilterType.BandPass:
                b0 = alpha;
                b1 = 0;
                b2 = -alpha;
                a0 = 1 + alpha;
                a1 = -2 * cosW;
                a2 = 1 - alpha;
                break;
        }
        return new BiquadCoefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>Magnitude response |H(e^jw)| at the given frequency.</summary>
    public double Magnitude(double frequency, double sampleRate)
    {
        double w = 2 * Math.PI * frequency / sampleRate;
        double cw = Math.Cos(w), c2w = Math.Cos(2 * w);
        double num = B0 * B0 + B1 * B1 + B2 * B2 + 2 * (B0 * B1 + B1 * B2) * cw + 2 * B0 * B2 * c2w;
        double den = 1 + A1 * A1 + A2 * A2 + 2 * (A1 + A1 * A2) * cw + 2 * A2 * c2w;
        return Math.Sqrt(Math.Max(num, 0) / Math.Max(den, 1e-30));
    }

    public double MagnitudeDb(double frequency, double sampleRate) =>
        20 * Math.Log10(Math.Max(Magnitude(frequency, sampleRate), 1e-9));
}

public static class ResponseCurve
{
    /// <summary>Log-spaced frequencies between <paramref name="low"/> and <paramref name="high"/>.</summary>
    public static double[] LogFrequencies(int count, double low = 20, double high = 20_000)
    {
        if (count <= 1) return [low];
        double l = Math.Log10(low), h = Math.Log10(high);
        var result = new double[count];
        for (int i = 0; i < count; i++) result[i] = Math.Pow(10, l + (h - l) * i / (count - 1));
        return result;
    }

    /// <summary>Summed response in dB of all bands at the given frequencies. With <paramref name="channel"/>
    /// (Left or Right), only the bands that process that channel count.</summary>
    public static double[] ResponseDb(IEnumerable<EqBand> bands, IReadOnlyList<double> frequencies, double sampleRate, EqChannels? channel = null)
    {
        var coeffs = bands
            .Where(b => b.IsEnabled && (channel is not { } c || b.Channels.Includes(c)))
            .Select(b => new BiquadCoefficients(b, sampleRate))
            .ToArray();
        var result = new double[frequencies.Count];
        for (int i = 0; i < result.Length; i++)
        {
            double sum = 0;
            foreach (var k in coeffs) sum += k.MagnitudeDb(frequencies[i], sampleRate);
            result[i] = sum;
        }
        return result;
    }

    public static double[] ResponseDb(EqBand band, IReadOnlyList<double> frequencies, double sampleRate)
    {
        var c = new BiquadCoefficients(band, sampleRate);
        var result = new double[frequencies.Count];
        for (int i = 0; i < result.Length; i++) result[i] = c.MagnitudeDb(frequencies[i], sampleRate);
        return result;
    }

    private static readonly double[] Probe = LogFrequencies(480);

    /// <summary>Largest boost of the summed curve of either channel, used for automatic preamp.</summary>
    public static double MaxBoostDb(IEnumerable<EqBand> bands, double sampleRate)
    {
        var list = bands as IReadOnlyCollection<EqBand> ?? [.. bands];
        if (!list.Any(b => b.IsEnabled)) return 0;
        if (!list.Any(b => b.IsEnabled && b.Channels != EqChannels.Both))
            return ResponseDb(list, Probe, sampleRate).Max();
        return Math.Max(ResponseDb(list, Probe, sampleRate, EqChannels.Left).Max(),
                        ResponseDb(list, Probe, sampleRate, EqChannels.Right).Max());
    }
}
