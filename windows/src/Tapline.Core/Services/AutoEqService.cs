using System.Net;

namespace Tapline.Core.Services;

/// <summary>Searches the AutoEQ project's headphone measurement database
/// (github.com/jaakkopasanen/AutoEq) and downloads parametric EQ profiles.
/// Not tied to a thread: <see cref="Changed"/> may be raised on any thread.</summary>
public sealed class AutoEqService
{
    public enum FormFactor { OverEar, InEar, Earbud, Unknown }

    public sealed record Entry(string Name, string Source, string? Rig, string Path, FormFactor FormFactor)
    {
        /// <summary>Percent-encoded path relative to results/, as listed in INDEX.md.</summary>
        public string Path { get; init; } = Path;

        public string Id => Path;

        public Uri? ProfileUrl
        {
            get
            {
                string folder = Path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                return Uri.TryCreate($"{Base}/{Path}/{folder}%20ParametricEQ.txt", UriKind.Absolute, out var url) ? url : null;
            }
        }

        public Uri? PageUrl =>
            Uri.TryCreate($"https://github.com/jaakkopasanen/AutoEq/tree/master/results/{Path}", UriKind.Absolute, out var url) ? url : null;
    }

    public const string Base = "https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results";
    private static readonly Uri IndexUrl = new($"{Base}/INDEX.md");
    private static readonly string[] SourcePriority = ["oratory1990", "crinacle", "Rtings", "Innerfidelity", "HypetheSonics", "Kuulokenurkka", "Headphone.com Legacy"];

    private readonly string _cacheDirectory;

    /// <summary>Raised whenever <see cref="Entries"/>, <see cref="IsLoading"/>, <see cref="ErrorMessage"/>
    /// or <see cref="LastUpdated"/> change; possibly on a worker thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<Entry> Entries { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset? LastUpdated { get; private set; }

    /// <param name="cacheDirectory">Where INDEX.md and downloaded profiles are kept; created if missing.</param>
    public AutoEqService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        ServiceSupport.CreateDirectory(cacheDirectory);
    }

    private string IndexCache => System.IO.Path.Combine(_cacheDirectory, "INDEX.md");

    /// <summary>Loads the index from the cache (if under a week old) or the network. Errors end up in
    /// <see cref="ErrorMessage"/>; only cancellation through <paramref name="cancellationToken"/> throws.</summary>
    public async Task LoadIndexAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && Entries.Count > 0) return;
        IsLoading = true;
        ErrorMessage = null;
        Changed?.Invoke();
        try
        {
            var modified = ServiceSupport.ModificationDate(IndexCache);
            bool fresh = modified is { } m && DateTimeOffset.UtcNow - m < TimeSpan.FromSeconds(7 * 86_400);

            if (!force && fresh && ServiceSupport.TryReadText(IndexCache) is { } cachedText)
            {
                Entries = ParseIndex(cachedText);
                LastUpdated = modified;
                return;
            }
            try
            {
                using var response = await ServiceSupport.Http.GetAsync(IndexUrl, cancellationToken).ConfigureAwait(false);
                var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK || ServiceSupport.DecodeUtf8(data) is not { } text)
                    throw new HttpRequestException($"Bad server response ({(int)response.StatusCode}).", null, response.StatusCode);
                await ServiceSupport.TryWriteAsync(IndexCache, data).ConfigureAwait(false);
                Entries = ParseIndex(text);
                LastUpdated = DateTimeOffset.UtcNow;
            }
            catch (Exception e) when (!ServiceSupport.IsCallerCancellation(e, cancellationToken))
            {
                if (ServiceSupport.TryReadText(IndexCache) is { } text)
                {
                    Entries = ParseIndex(text);
                    LastUpdated = modified;
                }
                else
                {
                    ErrorMessage = e.Message;
                }
            }
        }
        finally
        {
            IsLoading = false;
            Changed?.Invoke();
        }
    }

    public List<Entry> Search(string query, int limit = 120)
    {
        var tokens = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return [];
        var q = query.ToLowerInvariant();
        var matches = Entries.Where(e =>
        {
            var hay = e.Name.ToLowerInvariant();
            return tokens.All(t => hay.Contains(t, StringComparison.Ordinal));
        });
        var order = Comparer<Entry>.Create((a, b) =>
        {
            bool ap = a.Name.ToLowerInvariant().StartsWith(q, StringComparison.Ordinal);
            bool bp = b.Name.ToLowerInvariant().StartsWith(q, StringComparison.Ordinal);
            if (ap != bp) return ap ? -1 : 1;
            if (a.Name != b.Name) return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            return Rank(a.Source).CompareTo(Rank(b.Source));
        });
        return matches.Order(order).Take(limit).ToList();
    }

    /// <summary>The profile text (ParametricEQ.txt), from the cache or the network; validated by parsing.</summary>
    /// <exception cref="HttpRequestException">Download failed.</exception>
    /// <exception cref="ParametricEqParseException">The file holds no filters.</exception>
    public async Task<string> FetchProfileAsync(Entry entry, CancellationToken cancellationToken = default)
    {
        var url = entry.ProfileUrl ?? throw new HttpRequestException($"Bad URL for {entry.Path}.");
        var key = ServiceSupport.Sha256Hex(entry.Path);
        var cached = System.IO.Path.Combine(_cacheDirectory, $"{key}.txt");
        if (ServiceSupport.TryReadText(cached) is { } cachedText) return cachedText;
        using var response = await ServiceSupport.Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || ServiceSupport.DecodeUtf8(data) is not { } text)
            throw new HttpRequestException($"Profile not found ({(int)response.StatusCode}).", null, response.StatusCode);
        _ = ParametricEqFormat.Parse(text);
        await ServiceSupport.TryWriteAsync(cached, data).ConfigureAwait(false);
        return text;
    }

    private static int Rank(string source)
    {
        int i = Array.IndexOf(SourcePriority, source);
        return i >= 0 ? i : SourcePriority.Length;
    }

    /// <summary>Lines look like: <c>- [Bose QuietComfort 45](./oratory1990/over-ear/Bose%20QuietComfort%2045) by oratory1990 on GRAS</c></summary>
    public static List<Entry> ParseIndex(string text)
    {
        var result = new List<Entry>(9000);
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (!line.StartsWith("- [", StringComparison.Ordinal)) continue;
            int nameEnd = line.IndexOf("](./", StringComparison.Ordinal);
            if (nameEnd < 0) continue;
            int pathStart = nameEnd + 4;
            int pathEnd = line.IndexOf(") by ", pathStart, StringComparison.Ordinal);
            if (pathEnd < 0) continue;
            var name = line[3..nameEnd].Replace("\u200B", "", StringComparison.Ordinal);
            var path = line[pathStart..pathEnd];
            var tail = line[(pathEnd + 5)..];
            var parts = tail.Split(" on ");
            var source = parts[0];
            string? rig = parts.Length > 1 ? string.Join(" on ", parts.Skip(1)) : null;
            var lower = path.ToLowerInvariant();
            var form = lower.Contains("in-ear") ? FormFactor.InEar
                : lower.Contains("earbud") ? FormFactor.Earbud
                : lower.Contains("over-ear") ? FormFactor.OverEar
                : FormFactor.Unknown;
            result.Add(new Entry(name, source, rig, path, form));
        }
        return result;
    }
}
