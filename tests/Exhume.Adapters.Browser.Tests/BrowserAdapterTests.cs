using System.Text.Json;
using Exhume.Core;
using Microsoft.Data.Sqlite;

namespace Exhume.Adapters.Browser.Tests;

public sealed class BrowserAdapterTests
{
    [Fact]
    public async Task GroupsRealVisitsAcrossThreeMonthsAndMovesRareDomainsToOther()
    {
        using var fixture = new HistoryFixture();
        using var history = fixture.Create("chrome", "Default");
        fixture.Visit(history, "https://www.docs.example.com/a", "Repeated", "2024-01-02", 3);
        fixture.Visit(history, "https://rare.invalid/a", "Rare", "2024-01-03", 2);
        fixture.Visit(history, "file:///D:/notes.txt", "Local", "2024-01-04", 1);
        fixture.Visit(history, "https://a.co.uk/a", "Suffix", "2024-02-03", 3);
        fixture.Visit(history, "https://example.com/a", "March", "2024-03-03", 3);
        var adapter = new BrowserAdapter(fixture.Root);

        var items = await Collect(adapter, fixture.Options);

        Assert.Equal(4, items.Count);
        Assert.Equal("3", Assert.Single(items, x => x.Name == "2024-01 example.com").Metadata["visits"]);
        Assert.Equal("3", Assert.Single(items, x => x.Name == "2024-01 other").Metadata["visits"]);
        Assert.Contains(items, x => x.Name == "2024-02 co.uk");
        Assert.Contains(items, x => x.Name == "2024-03 example.com");
        Assert.All(items, x => Assert.Equal(["browser", "profile", "titles", "visits"], x.Metadata.Keys.Order().ToArray()));
        Assert.Equal(items.Select(x => x.Id), (await Collect(adapter, fixture.Options)).Select(x => x.Id));
    }

    [Fact]
    public async Task ConvertsKnownWebKitVectorAndIncludesAnOtherBucketBelowThreshold()
    {
        using var fixture = new HistoryFixture();
        using var history = fixture.Create("chrome", "Default");
        using var command = history.CreateCommand();
        command.CommandText = "INSERT INTO urls VALUES(1, 'https://example.com/', 'Epoch', 500); INSERT INTO visits VALUES(1, 1, 11644473600000000);";
        command.ExecuteNonQuery();

        var item = Assert.Single(await Collect(new BrowserAdapter(fixture.Root), fixture.Options));

        Assert.Equal(DateTimeOffset.UnixEpoch, item.FirstSeen);
        Assert.Equal("1970-01 other", item.Name);
        Assert.Equal("1", item.Metadata["visits"]);
    }

    [Fact]
    public async Task FiltersProfilesWithoutCombiningIdenticalNamesAcrossBrowsers()
    {
        using var fixture = new HistoryFixture();
        using var chrome = fixture.Create("chrome", "Default");
        using var edge = fixture.Create("edge", "Default");
        using var brave = fixture.Create("brave", "Profile 2");
        fixture.Visit(chrome, "https://example.com/", "Chrome", "2024-01-01", 3);
        fixture.Visit(edge, "https://example.com/", "Edge", "2024-01-01", 3);
        fixture.Visit(brave, "https://example.com/", "Brave", "2024-01-01", 3);
        var adapter = new BrowserAdapter(fixture.Root);

        Assert.Equal(3, (await Collect(adapter, fixture.Options)).Select(x => x.Id).Distinct().Count());
        Assert.Equal(2, (await Collect(adapter, fixture.Options with { Profile = "Default" })).Count);
        var selected = Assert.Single(await Collect(adapter, fixture.Options with { Profile = "edge:Default" }));
        Assert.Equal("edge", selected.Metadata["browser"]);
        Assert.Equal(SourceAvailability.Unavailable, adapter.Check(fixture.Options with { Profile = "Profile 999" }).Availability);
        Assert.Contains("chrome:Default", adapter.Check(fixture.Options).Reason);
    }

    [Fact]
    public async Task CopiesHeldOpenHistoryWithoutChangingSourceAndCleansOnEarlyDisposal()
    {
        using var fixture = new HistoryFixture();
        string source;
        using (var history = fixture.Create("chrome", "Default"))
        {
            source = history.DataSource;
            fixture.Visit(history, "https://example.com/", "Page", "2024-01-01", 3);
            fixture.Visit(history, "https://example.net/", "Page", "2024-01-01", 3);
        }
        var before = File.ReadAllBytes(source);
        using var heldOpen = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using (var iterator = new BrowserAdapter(fixture.Root).ScanAsync(fixture.Options).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync());
        }

        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Temp));
    }

    [Fact]
    public async Task ReadsCommittedWalRowsFromCopyWithoutModifyingSourceFiles()
    {
        using var fixture = new HistoryFixture();
        using var history = fixture.Create("chrome", "Default", wal: true);
        fixture.Visit(history, "https://example.com/", "WAL", "2024-01-01", 3);
        var files = Directory.GetFiles(Path.GetDirectoryName(history.DataSource)!).ToDictionary(x => x, ReadSharedBytes);

        Assert.Equal("3", Assert.Single(await Collect(new BrowserAdapter(fixture.Root), fixture.Options)).Metadata["visits"]);

        Assert.All(files, item => Assert.Equal(item.Value, ReadSharedBytes(item.Key)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Temp));
    }

    [Fact]
    public async Task CorruptProfileIsSkippedWithSanitizedErrorAndValidProfileSurvives()
    {
        using var fixture = new HistoryFixture();
        using var history = fixture.Create("edge", "Default");
        fixture.Visit(history, "https://example.com/", "Page", "2024-01-01", 3);
        var corrupt = fixture.HistoryPath("chrome", "Default");
        Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
        File.WriteAllText(corrupt, "not a database");
        var progress = new CaptureProgress();

        Assert.Single(await Collect(new BrowserAdapter(fixture.Root), fixture.Options, progress));

        Assert.Contains(progress.Items, x => x.Error is not null && !x.Error.Contains(fixture.Root, StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Temp));
    }

    [Fact]
    public async Task BoundsAndOrdersDistinctTitlesByVisitCount()
    {
        using var fixture = new HistoryFixture();
        using var history = fixture.Create("chrome", "Default");
        fixture.Visit(history, "https://example.com/1", "Zebra", "2024-01-01", 2);
        fixture.Visit(history, "https://example.com/2", "Alpha", "2024-01-01", 2);
        fixture.Visit(history, "https://example.com/3", new string('L', 500), "2024-01-01", 3);
        fixture.Visit(history, "https://example.com/4", "Rare", "2024-01-01", 1);

        var titles = JsonSerializer.Deserialize<string[]>(Assert.Single(await Collect(new BrowserAdapter(fixture.Root), fixture.Options)).Metadata["titles"])!;

        Assert.Equal(3, titles.Length);
        Assert.InRange(titles[0].Length, 1, 160);
        Assert.Equal("Alpha", titles[1]);
        Assert.Equal("Zebra", titles[2]);
    }

    [Fact]
    public async Task CancellationPropagatesAndRemovesTemporarySnapshots()
    {
        using var fixture = new HistoryFixture();
        using var history = fixture.Create("chrome", "Default");
        fixture.Visit(history, "https://example.com/", "Page", "2024-01-01", 3);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var unused in new BrowserAdapter(fixture.Root).ScanAsync(fixture.Options, cancellationToken: cancel.Token)) { }
        });
        Assert.False(Directory.Exists(fixture.Temp) && Directory.EnumerateFileSystemEntries(fixture.Temp).Any());
    }

    private static async Task<List<Artifact>> Collect(BrowserAdapter adapter, ScanOptions options, IProgress<ScanProgress>? progress = null)
    {
        var items = new List<Artifact>();
        await foreach (var item in adapter.ScanAsync(options, progress)) items.Add(item);
        return items;
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private sealed class CaptureProgress : IProgress<ScanProgress>
    {
        public List<ScanProgress> Items { get; } = [];
        public void Report(ScanProgress value) => Items.Add(value);
    }

    private sealed class HistoryFixture : IDisposable
    {
        private long id;
        private long visitId;
        public string Root { get; } = DataDirectoryPolicy.Normalize(Path.Combine(Path.GetTempPath(), "exhume-browser-" + Guid.NewGuid().ToString("N")));
        public string Temp => Path.Combine(Root, "temp");
        public ScanOptions Options => new() { TemporaryDirectory = Temp };
        public HistoryFixture() => Directory.CreateDirectory(Root);
        public string HistoryPath(string browser, string profile) => Path.Combine(Root, browser switch
        {
            "chrome" => Path.Combine("Google", "Chrome", "User Data"),
            "edge" => Path.Combine("Microsoft", "Edge", "User Data"),
            _ => Path.Combine("BraveSoftware", "Brave-Browser", "User Data")
        }, profile, "History");
        public SqliteConnection Create(string browser, string profile, bool wal = false)
        {
            var path = HistoryPath(browser, profile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = (wal ? "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;" : "") +
                "CREATE TABLE urls(id INTEGER PRIMARY KEY, url TEXT, title TEXT, visit_count INTEGER); CREATE TABLE visits(id INTEGER PRIMARY KEY, url INTEGER, visit_time INTEGER);";
            command.ExecuteNonQuery();
            return connection;
        }
        public void Visit(SqliteConnection connection, string url, string title, string day, int count)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO urls VALUES($id, $url, $title, 9999)";
            command.Parameters.AddWithValue("$id", ++id);
            command.Parameters.AddWithValue("$url", url);
            command.Parameters.AddWithValue("$title", title);
            command.ExecuteNonQuery();
            var when = DateTimeOffset.Parse(day + "T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            for (var i = 0; i < count; i++)
            {
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO visits VALUES($visit, $url, $time)";
                command.Parameters.AddWithValue("$visit", ++visitId);
                command.Parameters.AddWithValue("$url", id);
                command.Parameters.AddWithValue("$time", (when.UtcTicks - new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks) / 10 + i);
                command.ExecuteNonQuery();
            }
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
