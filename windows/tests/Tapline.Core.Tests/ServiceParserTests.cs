using System.Text;
using Tapline.Core;
using Tapline.Core.Services;

namespace Tapline.Core.Tests;

public class AutoEqParserTests
{
    [Fact]
    public void ParsesIndexLines()
    {
        const string index = "# Index\n\n" +
            "- [Bose QuietComfort 45](./oratory1990/over-ear/Bose%20QuietComfort%2045) by oratory1990 on GRAS\n" +
            "- [Apple​ AirPods Pro](./crinacle/GRAS%20RA0045%20in-ear/Apple%20AirPods%20Pro) by crinacle on GRAS RA0045 on a stand\r\n" +
            "- [Some Earbud](./Rtings/earbud/Some%20Earbud) by Rtings\n" +
            "- [Odd](./Innerfidelity/Odd) by Innerfidelity\n" +
            "- Not an entry\n" +
            "- [Broken](./x/y) without by\n";
        var entries = AutoEqService.ParseIndex(index);
        Assert.Equal(4, entries.Count);

        var bose = entries[0];
        Assert.Equal("Bose QuietComfort 45", bose.Name);
        Assert.Equal("oratory1990", bose.Source);
        Assert.Equal("GRAS", bose.Rig);
        Assert.Equal("oratory1990/over-ear/Bose%20QuietComfort%2045", bose.Path);
        Assert.Equal(bose.Path, bose.Id);
        Assert.Equal(AutoEqService.FormFactor.OverEar, bose.FormFactor);
        Assert.Equal("https://raw.githubusercontent.com/jaakkopasanen/AutoEq/master/results/oratory1990/over-ear/Bose%20QuietComfort%2045/Bose%20QuietComfort%2045%20ParametricEQ.txt",
                     bose.ProfileUrl?.AbsoluteUri);
        Assert.Equal("https://github.com/jaakkopasanen/AutoEq/tree/master/results/oratory1990/over-ear/Bose%20QuietComfort%2045",
                     bose.PageUrl?.AbsoluteUri);

        Assert.Equal("Apple AirPods Pro", entries[1].Name);
        Assert.Equal("GRAS RA0045 on a stand", entries[1].Rig);
        Assert.Equal(AutoEqService.FormFactor.InEar, entries[1].FormFactor);
        Assert.Null(entries[2].Rig);
        Assert.Equal(AutoEqService.FormFactor.Earbud, entries[2].FormFactor);
        Assert.Equal(AutoEqService.FormFactor.Unknown, entries[3].FormFactor);
    }
}

public class SquigParserTests
{
    [Fact]
    public void ParsesDirectory()
    {
        const string json = """
            [
              {"username": "aden", "name": "Aden", "urlType": "subdomain",
               "dbs": [{"type": "Headphones", "folder": "headphones"}, {"type": "IEMs", "folder": "/iems/"}, {"type": "Speakers", "folder": "/"}]},
              {"username": "rootsite", "urlType": "root", "dbs": [{"type": "5128"}]},
              {"username": "alt", "name": "Alt", "urlType": "altDomain", "altDomain": "https://graphs.example.com/", "dbs": [{"type": "Earbuds", "folder": "buds"}]},
              {"username": "noalt", "urlType": "altDomain", "dbs": [{"type": "IEMs"}]},
              {"username": "labber", "dbs": [{"type": "IEMs", "folder": "/"}]},
              {"name": "No username", "dbs": [{"type": "IEMs"}]}
            ]
            """;
        var dbs = SquigService.ParseDirectory(json);
        Assert.Equal(
        [
            "Aden|aden|Headphones|https://aden.squig.link/headphones/",
            "Aden|aden|Iems|https://aden.squig.link/iems/",
            "rootsite|rootsite|B5128|https://squig.link/",
            "Alt|alt|Earbuds|https://graphs.example.com/buds/",
            "labber|labber|Iems|https://squig.link/lab/labber/",
        ], dbs.Select(d => $"{d.SiteName}|{d.Username}|{d.Kind}|{d.BaseUrl.AbsoluteUri}"));
        Assert.Equal("https://aden.squig.link/headphones/", dbs[0].Id);
        Assert.Empty(SquigService.ParseDirectory("{\"not\": \"an array\"}"));
        Assert.Empty(SquigService.ParseDirectory("not json"));
        Assert.Empty(SquigService.ParseDirectory(Encoding.UTF8.GetBytes("[1, 2]")));
    }

    [Fact]
    public void ParsesConfig()
    {
        const string js = """
            // const DIR = "old/";
            const DIR = "data/phones/";  // where the files live
            const site = "https://example.com/graph"; // keeps the URL
            const targets = [
                { type: "Neutral", files: ["Harman 2019", 'Diffuse Field'] },
            //  { type: "Old", files: ["Commented"] },
                { type: "Reviewer", files: ["Bass [boosted]", "Harman 2019"] },
                { type: "Nested", files: [] }
            ];
            const after = ["not", "a target"];
            """;
        var (dir, targets) = SquigService.ParseConfig(js);
        Assert.Equal("data/phones/", dir);
        // As in Swift, a "]" inside a name ends the files: [...] match, dropping that entry.
        Assert.Equal(["Harman 2019", "Diffuse Field"], targets);

        var plain = SquigService.ParseConfig("let x = 1;");
        Assert.Equal("data/", plain.Dir);
        Assert.Empty(plain.Targets);
    }

    [Fact]
    public void ExtractsQuotedStrings()
    {
        Assert.Equal(["a", "b c", "it's", ""], SquigService.QuotedStrings("x = ['a', \"b c\", \"it's\", ''] 'unterminated"));
        Assert.Empty(SquigService.QuotedStrings("no quotes here"));
        Assert.Equal(["hbb", "crinacle"], SquigService.QuotedStrings("optedOut = [\"hbb\", 'crinacle']"));
    }

    [Fact]
    public void SplitsWords()
    {
        Assert.Equal(["apple", "airpods", "pro", "3", "anc", "vol", "4"], SquigService.Words("Apple AirPods Pro 3 (ANC - vol-4)"));
        Assert.Equal(["sennheiser", "hd", "650"], SquigService.Words("  Sennheiser_HD-650 "));
        Assert.Equal(["cafe\u0301", "\u00FC"], SquigService.Words("Cafe\u0301 \u00DC"));
        Assert.Empty(SquigService.Words(" -- "));
    }

    [Fact]
    public void ResolvesPercentEncodedPaths()
    {
        var baseUrl = new Uri("https://aden.squig.link/headphones/");
        Assert.Equal("https://aden.squig.link/headphones/data/Sony%20WH-1000XM5%20L.txt",
                     SquigService.Resolve("data/Sony WH-1000XM5 L.txt", baseUrl)?.AbsoluteUri);
        Assert.Equal("https://aden.squig.link/data/Model%20%23100%3F%20(50%25)%20Target.txt",
                     SquigService.Resolve("../data/Model #100? (50%) Target.txt", baseUrl)?.AbsoluteUri);
        Assert.Equal("https://aden.squig.link/headphones/data/Mot%C3%B6rhead.txt",
                     SquigService.Resolve("data/Motörhead.txt", baseUrl)?.AbsoluteUri);
    }

    [Fact]
    public void ParsesPhoneBook()
    {
        var db = new SquigService.Database("Aden", "aden", SquigService.Kind.Headphones, new Uri("https://aden.squig.link/headphones/"));
        const string json = """
            [
              {"name": "Sony", "phones": ["WH-1000XM5", {"name": "WH-1000XM4", "file": ["WH-1000XM4 ANC", "WH-1000XM4 Passive", ""]}]},
              {"name": "Apple", "phones": [{"name": "AirPods Max", "file": "AirPods Max (ANC)"}, {"name": "AirPods Pro"}, {"name": "Odd", "file": [1]}]},
              {"phones": ["Nameless"]}
            ]
            """;
        var phones = SquigService.ParsePhoneBook(Encoding.UTF8.GetBytes(json), db);
        Assert.Equal(
        [
            "Sony|WH-1000XM5|WH-1000XM5",
            "Sony|WH-1000XM4|WH-1000XM4 ANC",
            "Sony|WH-1000XM4|WH-1000XM4 Passive",
            "Apple|AirPods Max|AirPods Max (ANC)",
            "Apple|AirPods Pro|AirPods Pro",
            "Apple|Odd|Odd",
            "|Nameless|Nameless",
        ], phones.Select(p => $"{p.Brand}|{p.Model}|{p.File}"));
        Assert.Equal("WH-1000XM4 ANC", phones[1].DisplayName);
        Assert.Equal("Apple AirPods Max · Max ANC", new SquigService.Phone("Apple", "AirPods Max", "Max ANC", db).DisplayName);
        Assert.Equal("Apple  · file", new SquigService.Phone("Apple", "", "file", db).DisplayName);
        Assert.Equal("https://aden.squig.link/headphones/|WH-1000XM5", phones[0].Id);
        Assert.Empty(SquigService.ParsePhoneBook(Encoding.UTF8.GetBytes("[{\"name\": \"x\"}, 3]"), db));
    }

    [Fact]
    public void KindRawValuesMatchSquigLink()
    {
        foreach (var kind in Enum.GetValues<SquigService.Kind>())
            Assert.Equal(kind, SquigService.KindFromRawValue(SquigService.RawValue(kind)));
        Assert.Equal("IEMs", SquigService.RawValue(SquigService.Kind.Iems));
        Assert.Equal("5128", SquigService.RawValue(SquigService.Kind.B5128));
        Assert.Equal("B&K 5128", SquigService.Title(SquigService.Kind.B5128));
        Assert.Null(SquigService.KindFromRawValue("iems"));
    }

    [Fact]
    public void ServicesStartEmptyWithoutNetwork()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tapline-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var squig = new SquigService(Path.Combine(dir, "squig"));
            var autoEq = new AutoEqService(Path.Combine(dir, "AutoEQ"));
            Assert.True(Directory.Exists(Path.Combine(dir, "squig")) && Directory.Exists(Path.Combine(dir, "AutoEQ")));
            Assert.Empty(squig.Search("sony", new HashSet<SquigService.Kind> { SquigService.Kind.Headphones }));
            Assert.Empty(autoEq.Search("sony"));
            Assert.Empty(autoEq.Search("   "));
            Assert.False(squig.IsLoading || autoEq.IsLoading);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
