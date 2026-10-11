using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Tapline.Core.Services;

/// <summary>Plumbing shared by the online services.</summary>
internal static class ServiceSupport
{
    /// <summary>One client for the whole app; per-request timeouts go through cancellation tokens.</summary>
    public static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary><c>String(data:encoding: .utf8)</c>: null for invalid UTF-8.</summary>
    public static string? DecodeUtf8(byte[] data)
    {
        try
        {
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary><c>String(decoding:as: UTF8.self)</c>: invalid bytes become U+FFFD.</summary>
    public static string DecodeUtf8Lossy(byte[] data) => Encoding.UTF8.GetString(data);

    public static string Sha256Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public static void CreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Last write time of <paramref name="path"/>, or null if it does not exist.</summary>
    public static DateTimeOffset? ModificationDate(string path)
    {
        try
        {
            return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static byte[]? TryReadBytes(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? TryReadText(string path) => TryReadBytes(path) is { } data ? DecodeUtf8(data) : null;

    public static async Task TryWriteAsync(string path, byte[] data)
    {
        try
        {
            await File.WriteAllBytesAsync(path, data).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether <paramref name="e"/> is the caller's cancellation rather than a network failure.</summary>
    public static bool IsCallerCancellation(Exception e, CancellationToken token) =>
        e is OperationCanceledException && token.IsCancellationRequested;
}
