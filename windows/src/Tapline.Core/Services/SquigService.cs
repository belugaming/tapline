using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tapline.Core.Services;

/// <summary>Searches the squig.link network of frequency-response databases
/// (https://squig.link) and downloads measurements and target curves.
///
/// Sites listed in squig.link's own opt-out list are never contacted.
/// Not tied to a thread: <see cref="Changed"/> may be raised on any thread.</summary>
public sealed class SquigService
{
    public enum Kind { Headphones, Iems, Earbuds, B5128 }

    public sealed record Database(string SiteName, string Username, Kind Kind, Uri BaseUrl)
    {
        /// <summary>Ends with a slash, e.g. https://aden.squig.link/headphones/</summary>
        public Uri BaseUrl { get; init; } = BaseUrl;

        public string Id => BaseUrl.AbsoluteUri;
    }

    public sealed record Phone(string Brand, string Model, string File, Database Database)
    {
        /// <summary>File stem in the database's data directory, e.g. "Apple AirPods Pro 3 (ANC - vol-4)".</summary>
        public string File { get; init; } = File;

        public string Id => Database.Id + "|" + File;

        /// <summary>Variant shown to the user: the file name usually carries the mode (ANC, wired…).</summary>
        public string DisplayName =>
            Model.Length > 0 && File.Contains(Model, StringComparison.CurrentCultureIgnoreCase) ? File : $"{Brand} {Model} · {File}";
    }

    public enum SquigError { MissingMeasurement, MissingTarget }

    public sealed class SquigException(SquigError error) : Exception(error == SquigError.MissingMeasurement
        ? "Measurement file not found on the site."
        : "Target curve not found on the site.")
    {
        public SquigError Error { get; } = error;
    }

    /// <summary>Raised whenever <see cref="Databases"/>, <see cref="Phones"/>, <see cref="LoadedDatabases"/>,
    /// <see cref="IsLoading"/> or <see cref="ErrorMessage"/> change; possibly on a worker thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<Database> Databases { get; private set; } = [];
    public IReadOnlyList<Phone> Phones { get; private set; } = [];
    public int LoadedDatabases => Volatile.Read(ref _loadedDatabases);
    public bool IsLoading => Volatile.Read(ref _isLoading) != 0;
    public string? ErrorMessage { get; private set; }

    private int _loadedDatabases;
    private int _isLoading;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _targetCache = [];
    private readonly Dictionary<string, string> _dataDirectory = [];
    private readonly Dictionary<Kind, Database> _fallbackTargets = [];
    private readonly string _cacheDirectory;

    private static readonly Uri DirectoryUrl = new("https://squig.link/squigsites.json");
    private static readonly Uri OptOutUrl = new("https://squig.link/squigsites.js");
    /// <summary>Fallback copy of squig.link's opt-out list, used if it cannot be downloaded.</summary>
    private static readonly HashSet<string> KnownOptOuts = ["64audio", "bloomaudio", "cammyfi", "crinacle", "eliseaudio", "hbb", "joycesreview", "kr0mka", "graph", "vsg"];

    private const double Day = 86_400;
    private const string UserAgent = "Tapline (https://github.com/belugaming/tapline)";

    /// <param name="cacheDirectory">Where downloaded files are kept; created if missing.</param>
    public SquigService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        ServiceSupport.CreateDirectory(cacheDirectory);
    }

    // MARK: - Loading

    /// <summary>Loads the site directory and every phone book. Errors end up in <see cref="ErrorMessage"/>;
    /// only cancellation through <paramref name="cancellationToken"/> throws.</summary>
    public async Task LoadAllAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (IsLoading || (!force && Phones.Count > 0)) return;
        if (Interlocked.Exchange(ref _isLoading, 1) != 0) return;
        ErrorMessage = null;
        Volatile.Write(ref _loadedDatabases, 0);
        Changed?.Invoke();
        try
        {
            IReadOnlyList<Database> databases;
            try
            {
                var optOuts = await FetchOptOutsAsync(cancellationToken).ConfigureAwait(false);
                var directory = await CachedDataAsync(DirectoryUrl, force ? 0 : 7 * Day, cancellationToken).ConfigureAwait(false);
                databases = ParseDirectory(directory).Where(db => !optOuts.Contains(db.Username)).ToList();
                Databases = databases;
            }
            catch (Exception e) when (!ServiceSupport.IsCallerCancellation(e, cancellationToken))
            {
                ErrorMessage = e.Message;
                return;
            }
            Changed?.Invoke();

            var collected = new List<Phone>();
            using var slots = new SemaphoreSlim(8);
            var tasks = databases.Select(async db =>
            {
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var batch = await LoadPhoneBookAsync(db, force, cancellationToken).ConfigureAwait(false);
                    lock (collected) collected.AddRange(batch);
                    Interlocked.Increment(ref _loadedDatabases);
                    Changed?.Invoke();
                }
                finally
                {
                    slots.Release();
                }
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            Phones = collected;
        }
        finally
        {
            Volatile.Write(ref _isLoading, 0);
            Changed?.Invoke();
        }
    }

    private async Task<HashSet<string>> FetchOptOutsAsync(CancellationToken cancellationToken)
    {
        string? js;
        try
        {
            js = ServiceSupport.DecodeUtf8(await CachedDataAsync(OptOutUrl, 7 * Day, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (!ServiceSupport.IsCallerCancellation(e, cancellationToken))
        {
            js = null;
        }
        if (js is null || Regex.Match(js, @"optedOut\s*=\s*\[([^\]]*)\]") is not { Success: true } match) return [.. KnownOptOuts];
        var names = QuotedStrings(match.Value);
        return names.Count == 0 ? [.. KnownOptOuts] : [.. names, .. KnownOptOuts];
    }

    private async Task<List<Phone>> LoadPhoneBookAsync(Database db, bool force, CancellationToken cancellationToken)
    {
        byte[] data;
        try
        {
            data = await CachedDataAsync(new Uri(db.BaseUrl, "data/phone_book.json"), force ? 0 : 7 * Day, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (!ServiceSupport.IsCallerCancellation(e, cancellationToken))
        {
            return [];
        }
        return ParsePhoneBook(data, db);
    }

    /// <summary>Phones of a <c>data/phone_book.json</c>: brands with plain names or <c>{name, file}</c> entries.</summary>
    public static List<Phone> ParsePhoneBook(byte[] data, Database db)
    {
        var result = new List<Phone>();
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            if (doc.RootElement.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Object)) return [];
            foreach (var brandEntry in doc.RootElement.EnumerateArray())
            {
                string brand = StringValue(brandEntry, "name") ?? "";
                if (!brandEntry.TryGetProperty("phones", out var phones) || phones.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in phones.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var name = item.GetString()!;
                        result.Add(new Phone(brand, name, name, db));
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        string model = StringValue(item, "name") ?? "";
                        List<string> files;
                        if (item.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Array
                            && file.EnumerateArray().All(f => f.ValueKind == JsonValueKind.String))
                        {
                            files = file.EnumerateArray().Select(f => f.GetString()!).ToList();
                        }
                        else if (StringValue(item, "file") is { } one) files = [one];
                        else files = [model];
                        foreach (var f in files)
                            if (f.Length > 0) result.Add(new Phone(brand, model, f, db));
                    }
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return result;
    }

    // MARK: - Search

    /// <summary>Every query word must start a word of the entry, so "pro 3" does not match "53dB".</summary>
    public List<Phone> Search(string query, IReadOnlySet<Kind> kinds, int limit = 150)
    {
        var tokens = Words(query);
        if (tokens.Count == 0) return [];
        var phrase = string.Join(" ", tokens);
        var scored = new List<(Phone Phone, int Score)>();
        foreach (var p in Phones)
        {
            if (!kinds.Contains(p.Database.Kind)) continue;
            var words = Words($"{p.Brand} {p.Model} {p.File}");
            if (!tokens.All(t => words.Any(w => w.StartsWith(t, StringComparison.Ordinal)))) continue;
            int score = string.Join(" ", words).Contains(phrase, StringComparison.Ordinal) ? 100 : 0;
            score += tokens.Count(t => words.Contains(t)) * 10;
            scored.Add((p, score));
        }
        var order = Comparer<(Phone Phone, int Score)>.Create((x, y) =>
        {
            if (x.Score != y.Score) return y.Score.CompareTo(x.Score);
            if (x.Phone.Model != y.Phone.Model) return string.Compare(x.Phone.Model, y.Phone.Model, StringComparison.CurrentCultureIgnoreCase);
            if (x.Phone.Database.SiteName != y.Phone.Database.SiteName)
                return string.CompareOrdinal(x.Phone.Database.SiteName, y.Phone.Database.SiteName);
            return string.CompareOrdinal(x.Phone.File, y.Phone.File);
        });
        return scored.Order(order).Take(limit).Select(x => x.Phone).ToList();
    }

    /// <summary>Lowercased runs of letters and digits.</summary>
    public static List<string> Words(string s)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var rune in s.ToLowerInvariant().EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            bool wordPart = Rune.IsLetter(rune) || Rune.IsNumber(rune)
                            || (current.Length > 0 && category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark);
            if (wordPart)
            {
                current.Append(rune.ToString());
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    // MARK: - Measurements & targets

    /// <summary><paramref name="path"/> percent-encoded as a URL path and resolved against <paramref name="baseUrl"/>.</summary>
    public static Uri? Resolve(string path, Uri baseUrl)
    {
        const string allowed = "!$&'()*+,-./:;=@_~";
        var encoded = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(path))
        {
            char c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || allowed.Contains(c))) encoded.Append(c);
            else encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return Uri.TryCreate(baseUrl, encoded.ToString(), out var url) ? url : null;
    }

    /// <summary>The phone's measurement: the average of its L / R (or L1 / R1, or single) files on <see cref="EqFitter.Grid"/>.</summary>
    /// <exception cref="SquigException">No measurement file could be read.</exception>
    public async Task<FrequencyResponse> MeasurementAsync(Phone phone, CancellationToken cancellationToken = default)
    {
        var dir = await DataDirAsync(phone.Database, cancellationToken).ConfigureAwait(false);
        var channels = new List<FrequencyResponse>();
        foreach (var suffixes in (string[][])[[" L", " R"], [" L1", " R1"], [""]])
        {
            foreach (var suffix in suffixes)
            {
                if (Resolve(dir + phone.File + suffix + ".txt", phone.Database.BaseUrl) is not { } url) continue;
                if (await TryCachedDataAsync(url, 30 * Day, cancellationToken).ConfigureAwait(false) is { } data
                    && FrequencyResponse.Parse(ServiceSupport.DecodeUtf8Lossy(data)) is { } fr)
                {
                    channels.Add(fr);
                }
            }
            if (channels.Count > 0) break;
        }
        return FrequencyResponse.Average(channels, EqFitter.Grid) ?? throw new SquigException(SquigError.MissingMeasurement);
    }

    /// <summary>Target curves the site offers, read from its graph tool configuration.</summary>
    public async Task<IReadOnlyList<string>> TargetsAsync(Database db, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            if (_targetCache.TryGetValue(db.Id, out var cached)) return cached;
        IReadOnlyList<string> names = [];
        // The graph tool page names its config script, e.g. "../assets/js/config_hp.js".
        var candidates = new List<Uri>();
        if (await TryCachedDataAsync(db.BaseUrl, 7 * Day, cancellationToken).ConfigureAwait(false) is { } page
            && ServiceSupport.DecodeUtf8(page) is { } html
            && Regex.Match(html, @"src=[""'][^""']*config[^""']*\.js") is { Success: true } match)
        {
            var src = Regex.Replace(match.Value, @"^src=[""']", "");
            if (Uri.TryCreate(db.BaseUrl, src, out var url)) candidates.Add(url);
        }
        foreach (var relative in (string[])["config.js", "assets/js/config.js"])
            if (Uri.TryCreate(db.BaseUrl, relative, out var url)) candidates.Add(url);
        foreach (var url in candidates)
        {
            if (await TryCachedDataAsync(url, 7 * Day, cancellationToken).ConfigureAwait(false) is not { } data
                || ServiceSupport.DecodeUtf8(data) is not { } js || !js.Contains("targets", StringComparison.Ordinal)) continue;
            var (dir, targets) = ParseConfig(js);
            lock (_gate) _dataDirectory[db.Id] = dir;
            names = targets;
            break;
        }
        lock (_gate) _targetCache[db.Id] = names;
        return names;
    }

    /// <exception cref="SquigException">The target file could not be read.</exception>
    public async Task<FrequencyResponse> TargetAsync(string name, Database db, CancellationToken cancellationToken = default)
    {
        var dir = await DataDirAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var path in (string[])[dir + "targets/" + name + " Target.txt", dir + name + " Target.txt"])
        {
            if (Resolve(path, db.BaseUrl) is not { } url) continue;
            if (await TryCachedDataAsync(url, 30 * Day, cancellationToken).ConfigureAwait(false) is { } data
                && FrequencyResponse.Parse(ServiceSupport.DecodeUtf8Lossy(data)) is { } fr)
            {
                return fr;
            }
        }
        throw new SquigException(SquigError.MissingTarget);
    }

    /// <summary>A database of the same kind that publishes a Harman target, for databases without targets.</summary>
    public async Task<Database?> FallbackTargetDatabaseAsync(Kind kind, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            if (_fallbackTargets.TryGetValue(kind, out var known)) return known;
        foreach (var db in Databases.Where(d => d.Kind == kind))
        {
            var names = await TargetsAsync(db, cancellationToken).ConfigureAwait(false);
            if (names.Any(n => n.Contains("harman", StringComparison.CurrentCultureIgnoreCase)))
            {
                lock (_gate) _fallbackTargets[kind] = db;
                return db;
            }
        }
        return null;
    }

    private async Task<string> DataDirAsync(Database db, CancellationToken cancellationToken)
    {
        lock (_gate)
            if (_dataDirectory.TryGetValue(db.Id, out var dir)) return dir;
        await TargetsAsync(db, cancellationToken).ConfigureAwait(false);
        lock (_gate)
            return _dataDirectory.TryGetValue(db.Id, out var found) ? found : "data/";
    }

    // MARK: - Parsing

    /// <summary>The databases listed in squig.link's <c>squigsites.json</c>.</summary>
    public static List<Database> ParseDirectory(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return ParseDirectory(doc.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <inheritdoc cref="ParseDirectory(byte[])"/>
    public static List<Database> ParseDirectory(string json) => ParseDirectory(Encoding.UTF8.GetBytes(json));

    private static List<Database> ParseDirectory(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Object)) return [];
        var result = new List<Database>();
        foreach (var site in root.EnumerateArray())
        {
            if (StringValue(site, "username") is not { } username) continue;
            string name = StringValue(site, "name") ?? username;
            string root2 = StringValue(site, "urlType") switch
            {
                "root" => "https://squig.link",
                "altDomain" => StringValue(site, "altDomain") ?? "",
                "subdomain" => $"https://{username}.squig.link",
                _ => $"https://squig.link/lab/{username}",
            };
            if (root2.Length == 0) continue;
            if (!site.TryGetProperty("dbs", out var dbs) || dbs.ValueKind != JsonValueKind.Array
                || dbs.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Object)) continue;
            foreach (var db in dbs.EnumerateArray())
            {
                if (StringValue(db, "type") is not { } type || KindFromRawValue(type) is not { } kind) continue;
                string folder = StringValue(db, "folder") ?? "/";
                if (!folder.StartsWith('/')) folder = "/" + folder;
                if (!folder.EndsWith('/')) folder += "/";
                string trimmedRoot = root2.EndsWith('/') ? root2[..^1] : root2;
                if (!Uri.TryCreate(trimmedRoot + folder, UriKind.Absolute, out var url)) continue;
                result.Add(new Database(name, username, kind, url));
            }
        }
        return result;
    }

    /// <summary>Extracts <c>DIR</c> and the target file names from a squig graph tool config.js.</summary>
    public static (string Dir, List<string> Targets) ParseConfig(string js)
    {
        // Drop comments: whole commented lines and trailing " // ..." (keeps "https://").
        var lines = new List<string>();
        foreach (var line in js.Split('\n'))
        {
            if (Swift.TrimWhitespaces(line).StartsWith("//", StringComparison.Ordinal)) continue;
            var comment = Regex.Match(line, @"\s//.*$");
            lines.Add(comment.Success ? line[..comment.Index] : line);
        }
        var cleaned = string.Join("\n", lines);

        string dir = "data/";
        if (Regex.Match(cleaned, @"DIR\s*=\s*[""']([^""']+)[""']") is { Success: true } dirMatch)
            dir = QuotedStrings(dirMatch.Value).FirstOrDefault() ?? dir;

        var targets = new List<string>();
        if (Regex.Match(cleaned, @"targets\s*=\s*\[") is { Success: true } start)
        {
            // Find the matching closing bracket of the targets array.
            int depth = 1;
            int index = start.Index + start.Length;
            while (index < cleaned.Length && depth > 0)
            {
                char ch = cleaned[index];
                if (ch == '[') depth++;
                else if (ch == ']') depth--;
                index++;
            }
            var body = cleaned[(start.Index + start.Length)..index];
            foreach (Match files in Regex.Matches(body, @"files\s*:\s*\[[^\]]*\]"))
                targets.AddRange(QuotedStrings(files.Value));
        }
        var seen = new HashSet<string>();
        return (dir, targets.Where(seen.Add).ToList());
    }

    /// <summary>Contents of the '...' and "..." strings in <paramref name="s"/>, in order.</summary>
    public static List<string> QuotedStrings(string s)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (char ch in s)
        {
            if (quote is { } q)
            {
                if (ch == q)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
        }
        return result;
    }

    /// <summary>The squig.link name of a kind ("Headphones", "IEMs", "Earbuds", "5128").</summary>
    public static string RawValue(Kind kind) => kind switch
    {
        Kind.Headphones => "Headphones",
        Kind.Iems => "IEMs",
        Kind.Earbuds => "Earbuds",
        _ => "5128",
    };

    public static Kind? KindFromRawValue(string rawValue) => rawValue switch
    {
        "Headphones" => Kind.Headphones,
        "IEMs" => Kind.Iems,
        "Earbuds" => Kind.Earbuds,
        "5128" => Kind.B5128,
        _ => null,
    };

    /// <summary>Display title (an English localization key).</summary>
    public static string Title(Kind kind) => kind switch
    {
        Kind.Headphones => "Headphones",
        Kind.Iems => "In-ear",
        Kind.Earbuds => "Earbuds",
        _ => "B&K 5128",
    };

    private static string? StringValue(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // MARK: - HTTP cache

    private async Task<byte[]?> TryCachedDataAsync(Uri url, double maxAgeSeconds, CancellationToken cancellationToken)
    {
        try
        {
            return await CachedDataAsync(url, maxAgeSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (!ServiceSupport.IsCallerCancellation(e, cancellationToken))
        {
            return null;
        }
    }

    private async Task<byte[]> CachedDataAsync(Uri url, double maxAgeSeconds, CancellationToken cancellationToken)
    {
        var key = ServiceSupport.Sha256Hex(url.AbsoluteUri);
        var file = Path.Combine(_cacheDirectory, key);
        if (ServiceSupport.ModificationDate(file) is { } modified
            && (DateTimeOffset.UtcNow - modified).TotalSeconds < maxAgeSeconds
            && ServiceSupport.TryReadBytes(file) is { } cached)
        {
            return cached;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var response = await ServiceSupport.Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException($"Not found ({(int)response.StatusCode}): {url}", null, response.StatusCode);
            var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            await ServiceSupport.TryWriteAsync(file, data).ConfigureAwait(false);
            return data;
        }
        catch (Exception e) when (!ServiceSupport.IsCallerCancellation(e, cancellationToken))
        {
            if (ServiceSupport.TryReadBytes(file) is { } stale) return stale;
            throw;
        }
    }
}
