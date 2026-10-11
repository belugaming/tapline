using Tapline.Core;

namespace Tapline.Core.Tests;

public class FftTests
{
    private static (double[] Re, double[] Im) NaiveDft(double[] re, double[] im, bool inverse = false)
    {
        int n = re.Length;
        var xr = new double[n];
        var xi = new double[n];
        double sign = inverse ? 1 : -1;
        for (int k = 0; k < n; k++)
        {
            double sr = 0, si = 0;
            for (int j = 0; j < n; j++)
            {
                double angle = sign * 2 * Math.PI * ((long)j * k % n) / n;
                double c = Math.Cos(angle), s = Math.Sin(angle);
                sr += re[j] * c - im[j] * s;
                si += re[j] * s + im[j] * c;
            }
            xr[k] = inverse ? sr / n : sr;
            xi[k] = inverse ? si / n : si;
        }
        return (xr, xi);
    }

    private static double[] RandomSignal(int n, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => random.NextDouble() * 2 - 1).ToArray();
    }

    private static void AssertClose(double[] expected, double[] actual, double tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"[{i}] {actual[i]} vs {expected[i]}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(1024)]
    public void ComplexForwardMatchesNaiveDft(int n)
    {
        var re = RandomSignal(n, n);
        var im = RandomSignal(n, n + 1);
        var (er, ei) = NaiveDft(re, im);
        var (ar, ai) = (re.ToArray(), im.ToArray());
        Fft.Forward(ar, ai);
        AssertClose(er, ar, 1e-9 * n);
        AssertClose(ei, ai, 1e-9 * n);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(512)]
    public void ComplexInverseUndoesForward(int n)
    {
        var re = RandomSignal(n, 3);
        var im = RandomSignal(n, 4);
        var (ar, ai) = (re.ToArray(), im.ToArray());
        Fft.Forward(ar, ai);
        var (nr, ni) = NaiveDft(ar, ai, inverse: true);
        Fft.Inverse(ar, ai);
        AssertClose(re, ar, 1e-12);
        AssertClose(im, ai, 1e-12);
        AssertClose(nr, ar, 1e-9);
        AssertClose(ni, ai, 1e-9);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(4, 3)]
    [InlineData(16, 16)]
    [InlineData(512, 300)]
    [InlineData(2048, 2048)]
    public void RealForwardMatchesNaiveDft(int n, int length)
    {
        var x = RandomSignal(length, n * 7 + length);
        var padded = x.Concat(new double[n - length]).ToArray();
        var (er, ei) = NaiveDft(padded, new double[n]);
        var (ar, ai) = Fft.RealForward(x, n);
        Assert.Equal(n / 2 + 1, ar.Length);
        AssertClose(er[..(n / 2 + 1)], ar, 1e-9 * n);
        AssertClose(ei[..(n / 2 + 1)], ai, 1e-9 * n);
        Assert.True(Math.Abs(ai[0]) < 1e-12 && Math.Abs(ai[n / 2]) < 1e-9 * n);

        var (fr, fi) = Fft.RealForward(x.Select(v => (float)v).ToArray(), n);
        var (gr, gi) = NaiveDft(padded.Select(v => (double)(float)v).ToArray(), new double[n]);
        AssertClose(gr[..(n / 2 + 1)], fr, 1e-9 * n);
        AssertClose(gi[..(n / 2 + 1)], fi, 1e-9 * n);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(4096)]
    public void RealInverseUndoesRealForward(int n)
    {
        var x = RandomSignal(n, 11);
        var (re, im) = Fft.RealForward(x, n);
        AssertClose(x, Fft.RealInverse(re, im, n), 1e-12);
    }

    [Fact]
    public void ConvolveMatchesDirectConvolution()
    {
        var random = new Random(5);
        var a = Enumerable.Range(0, 37).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
        var b = Enumerable.Range(0, 100).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
        var y = SineSweep.Convolve(a, b);
        Assert.Equal(a.Length + b.Length - 1, y.Length);
        for (int i = 0; i < y.Length; i++)
        {
            double sum = 0;
            for (int j = 0; j < a.Length; j++)
                if (i - j >= 0 && i - j < b.Length) sum += (double)a[j] * b[i - j];
            Assert.True(Math.Abs(sum - y[i]) < 1e-6, $"[{i}] {y[i]} vs {sum}");
        }
        Assert.Equal([6f], SineSweep.Convolve([2f], [3f]));
        Assert.Empty(SineSweep.Convolve([], []));
    }

    [Fact]
    public void HannWindowMatchesVdsp()
    {
        // vDSP_hann_window(8, vDSP_HANN_NORM) and vDSP_HANN_DENORM, as printed on macOS.
        float[] norm = [0.0000000f, 0.2391463f, 0.8164966f, 1.3938469f, 1.6329932f, 1.3938469f, 0.8164966f, 0.2391463f];
        float[] denorm = [0.0000000f, 0.1464466f, 0.5000000f, 0.8535534f, 1.0000000f, 0.8535534f, 0.5000000f, 0.1464466f];
        AssertClose(norm.Select(v => (double)v).ToArray(), Fft.HannWindow(8), 1e-7);
        AssertClose(denorm.Select(v => (double)v).ToArray(), Fft.HannWindow(8, normalized: false), 1e-7);
    }

    [Fact]
    public void RejectsSizesThatAreNotPowersOfTwo()
    {
        Assert.Throws<ArgumentException>(() => Fft.Forward(new double[6], new double[6]));
        Assert.Throws<ArgumentException>(() => Fft.RealForward(new double[4], 12));
        Assert.Throws<ArgumentException>(() => Fft.RealForward(new double[1], 1));
    }
}
