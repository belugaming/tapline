using System.Collections.Concurrent;
using System.Numerics;

namespace Tapline.Core;

/// <summary>Iterative radix-2 FFT in double precision, the stand-in for vDSP.
/// Scaling is that of the plain DFT: the forward transforms are unscaled, the inverse
/// transforms divide by n, so <c>Inverse(Forward(x)) == x</c>. (vDSP's real FFT returns
/// twice these values; the ported call sites are normalised accordingly.)</summary>
public static class Fft
{
    public static bool IsPowerOfTwo(int n) => n > 0 && BitOperations.IsPow2(n);

    /// <summary>Smallest power of two that is at least <paramref name="n"/>.</summary>
    public static int NextPowerOfTwo(int n) => n <= 1 ? 1 : (int)BitOperations.RoundUpToPowerOf2((uint)n);

    /// <summary>In-place complex DFT: X[k] = sum x[j] e^(-2 pi i jk/n). Length must be a power of two.</summary>
    public static void Forward(Span<double> re, Span<double> im) => Transform(re, im, inverse: false);

    /// <summary>In-place inverse complex DFT, scaled by 1/n.</summary>
    public static void Inverse(Span<double> re, Span<double> im)
    {
        Transform(re, im, inverse: true);
        double scale = 1.0 / re.Length;
        for (int i = 0; i < re.Length; i++)
        {
            re[i] *= scale;
            im[i] *= scale;
        }
    }

    /// <summary>DFT of a real signal zero-padded (or truncated) to <paramref name="n"/> samples:
    /// bins 0 ... n/2 (n/2 + 1 values; DC and Nyquist have zero imaginary parts).</summary>
    public static (double[] Re, double[] Im) RealForward(ReadOnlySpan<double> x, int n)
    {
        RequireRealSize(n);
        int h = n / 2;
        var zr = new double[h];
        var zi = new double[h];
        int count = Math.Min(x.Length, n);
        for (int j = 0; j < count; j++)
        {
            if ((j & 1) == 0) zr[j >> 1] = x[j]; else zi[j >> 1] = x[j];
        }
        return RealForwardPacked(zr, zi, n);
    }

    /// <inheritdoc cref="RealForward(ReadOnlySpan{double}, int)"/>
    public static (double[] Re, double[] Im) RealForward(ReadOnlySpan<float> x, int n)
    {
        RequireRealSize(n);
        int h = n / 2;
        var zr = new double[h];
        var zi = new double[h];
        int count = Math.Min(x.Length, n);
        for (int j = 0; j < count; j++)
        {
            if ((j & 1) == 0) zr[j >> 1] = x[j]; else zi[j >> 1] = x[j];
        }
        return RealForwardPacked(zr, zi, n);
    }

    /// <summary>Inverse of <see cref="RealForward(ReadOnlySpan{double}, int)"/>: n/2 + 1 bins of a
    /// conjugate-symmetric spectrum to n real samples, scaled by 1/n.</summary>
    public static double[] RealInverse(ReadOnlySpan<double> re, ReadOnlySpan<double> im, int n)
    {
        RequireRealSize(n);
        int h = n / 2;
        if (re.Length < h + 1 || im.Length < h + 1) throw new ArgumentException("Expected n/2 + 1 bins.");
        var (cos, sin) = Twiddles(n);
        var zr = new double[h];
        var zi = new double[h];
        for (int k = 0; k < h; k++)
        {
            // E = (X[k] + conj X[h-k]) / 2, O = (X[k] - conj X[h-k]) / 2 * e^(+2 pi i k/n), Z = E + iO.
            double ar = re[k], ai = im[k];
            double br = re[h - k], bi = -im[h - k];
            double er = (ar + br) / 2, ei = (ai + bi) / 2;
            double dr = (ar - br) / 2, di = (ai - bi) / 2;
            double c = cos[k], s = sin[k];
            double or = dr * c - di * s, oi = dr * s + di * c;
            zr[k] = er - oi;
            zi[k] = ei + or;
        }
        Inverse(zr, zi);
        var x = new double[n];
        for (int k = 0; k < h; k++)
        {
            x[2 * k] = zr[k];
            x[2 * k + 1] = zi[k];
        }
        return x;
    }

    /// <summary>Hann window as <c>vDSP_hann_window</c> makes it: w[i] = c (1 - cos(2 pi i / length)),
    /// with c = sqrt(2/3) for <c>vDSP_HANN_NORM</c> (<paramref name="normalized"/>) and 0.5 otherwise.</summary>
    public static double[] HannWindow(int length, bool normalized = true)
    {
        double c = normalized ? Math.Sqrt(2.0 / 3) : 0.5;
        var w = new double[length];
        for (int i = 0; i < length; i++) w[i] = c * (1 - Math.Cos(2 * Math.PI * i / length));
        return w;
    }

    private static (double[] Re, double[] Im) RealForwardPacked(double[] zr, double[] zi, int n)
    {
        int h = n / 2;
        Forward(zr, zi);
        var (cos, sin) = Twiddles(n);
        var xr = new double[h + 1];
        var xi = new double[h + 1];
        for (int k = 0; k <= h; k++)
        {
            // X[k] = E[k] + e^(-2 pi i k/n) O[k], with E/O the DFTs of the even/odd samples.
            int a = k % h, b = (h - k) % h;
            double ar = zr[a], ai = zi[a], br = zr[b], bi = zi[b];
            double er = (ar + br) / 2, ei = (ai - bi) / 2;
            double or = (ai + bi) / 2, oi = -(ar - br) / 2;
            double c = k == h ? -1 : cos[k], s = k == h ? 0 : sin[k];
            xr[k] = er + c * or + s * oi;
            xi[k] = ei + c * oi - s * or;
        }
        return (xr, xi);
    }

    private static void RequireRealSize(int n)
    {
        if (n < 2 || !IsPowerOfTwo(n)) throw new ArgumentException("FFT size must be a power of two of at least 2.", nameof(n));
    }

    private static void Transform(Span<double> re, Span<double> im, bool inverse)
    {
        int n = re.Length;
        if (im.Length != n) throw new ArgumentException("Real and imaginary parts must have the same length.");
        if (!IsPowerOfTwo(n)) throw new ArgumentException("FFT size must be a power of two.");
        if (n == 1) return;

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        var (cos, sin) = Twiddles(n);
        double sign = inverse ? 1 : -1;
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1, step = n / len;
            for (int start = 0; start < n; start += len)
            {
                for (int k = 0; k < half; k++)
                {
                    double wr = cos[k * step], wi = sign * sin[k * step];
                    int a = start + k, b = a + half;
                    double tr = re[b] * wr - im[b] * wi;
                    double ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }

    private static readonly ConcurrentDictionary<int, (double[] Cos, double[] Sin)> TwiddleCache = new();

    /// <summary>cos and sin of 2 pi k/n for k &lt; n/2; cached for sizes up to 2^16.</summary>
    private static (double[] Cos, double[] Sin) Twiddles(int n)
    {
        if (n <= 1 << 16 && TwiddleCache.TryGetValue(n, out var cached)) return cached;
        int h = Math.Max(1, n / 2);
        var cos = new double[h];
        var sin = new double[h];
        for (int k = 0; k < h; k++)
        {
            double angle = 2 * Math.PI * k / n;
            cos[k] = Math.Cos(angle);
            sin[k] = Math.Sin(angle);
        }
        if (n <= 1 << 16) TwiddleCache.TryAdd(n, (cos, sin));
        return (cos, sin);
    }
}
