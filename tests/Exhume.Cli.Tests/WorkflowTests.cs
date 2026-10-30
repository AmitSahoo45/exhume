using System.Net;
using System.Runtime.CompilerServices;
using Exhume.Core;
using Spectre.Console;

namespace Exhume.Cli.Tests;

public sealed class WorkflowTests : IDisposable
{
    private readonly string root = CreateRoot();
    private readonly StringWriter output = new();
    private readonly NoModelHandler http = new();
    private string Data => Path.Combine(root, "data");

    private static string CreateRoot() => Directory.CreateDirectory(DataDirectoryPolicy.Normalize(
        Path.Combine(Path.GetTempPath(), "exhume-flow-" + Guid.NewGuid().ToString("N")))).FullName;

    [Fact]
    public async Task DigUpsertsAndRecordsScansWithoutCallingModel()
    {
        var host = Host([new Source("folder", [Item("folder", "one")])]);
        Assert.Equal(0, await host.RunAsync(["dig", "--source", "folder", "--home", Data]));
        Assert.Equal(0, await host.RunAsync(["dig", "--source", "folder", "--home", Data]));
        Assert.Equal(new StoreCounts(1, 0, 2), await Store().GetCountsAsync());
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task DigKeepsSuccessfulSourceWhenAnotherFails()
    {
        var host = Host([new Source("folder", [Item("folder", "one")]), new Source("browser", [], fail: true)]);
        Assert.Equal(2, await host.RunAsync(["dig", "--source", "folder", "--source", "browser", "--home", Data]));
        Assert.Equal(new StoreCounts(1, 0, 2), await Store().GetCountsAsync());
        Assert.Contains("browser", output.ToString());
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task CancelledDigDiscardsPendingArtifactsAndRecordsCancellation()
    {
        var original = Item("folder", "original");
        await Store().UpsertAsync([original]);
        using var cancellation = new CancellationTokenSource();
        var host = Host([new Source("folder", [Item("folder", "pending")], cancel: cancellation)]);
        Assert.Equal(2, await host.RunAsync(["dig", "--source", "folder", "--home", Data], cancellation.Token));
        Assert.Equal(original.Id, Assert.Single(await Store().QueryAsync()).Id);
        Assert.Equal(1, (await Store().GetCountsAsync()).Scans);
        Assert.Contains("cancel", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownSourceIsUsageErrorBeforeStorageIsCreated()
    {
        Assert.Equal(1, await Host([]).RunAsync(["dig", "--source", "unknown", "--home", Data]));
        Assert.False(Directory.Exists(Data));
        Assert.Equal(0, http.Calls);
    }

    [Theory]
    [InlineData("eras")]
    [InlineData("timeline")]
    public async Task RawViewsShowSeededMonthsWithoutModel(string command)
    {
        await Store().UpsertAsync([Item("folder", "2023"), Item("browser", "2023")]);
        Assert.Equal(0, await Host([]).RunAsync([command, "--no-model", "--home", Data]));
        Assert.Contains("2023-02", output.ToString());
        Assert.Contains("folder", output.ToString());
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task ErasRetainsUndatedEvidenceWithoutInventingAMonthOrNarratingIt()
    {
        await Store().UpsertAsync([new Artifact("folder", "FolderMonth", "undated", "undated document", null, null,
            "fixture", new Dictionary<string, string> { ["count"] = "3" })]);
        Assert.Equal(0, await Host([]).RunAsync(["eras", "--source", "folder", "--home", Data]));
        Assert.Contains("undated document", output.ToString());
        Assert.Contains("count: 3", output.ToString());
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task ObituariesOrderByLastSeenAndRespectLimit()
    {
        await Store().UpsertAsync([
            new Artifact("windows", "DeadProgram", "older", "older program", null, new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), "fixture"),
            new Artifact("windows", "DeadProgram", "newer", "newer program", null, new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), "fixture")
        ]);
        Assert.Equal(0, await Host([]).RunAsync(["obituaries", "--no-model", "--limit", "1", "--home", Data]));
        Assert.Contains("newer program", output.ToString());
        Assert.DoesNotContain("older program", output.ToString());
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task FailedNarrationFallsBackToRawDataSuccessfully()
    {
        await Store().UpsertAsync([Item("folder", "one")]);
        Assert.Equal(0, await Host([]).RunAsync(["eras", "--source", "folder", "--max-attempts", "1", "--home", Data]));
        Assert.Contains("2023-02", output.ToString());
        Assert.Contains("raw", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task RedactedExportRemovesTitlesSamplesAndEvidencePathsWithoutChangingStore()
    {
        var folder = Item("folder", "folder", new() { ["count"] = "1", ["samples"] = "[\"private-note.txt\"]" });
        var browser = Item("browser", "browser", new() { ["visits"] = "3", ["titles"] = "[\"Private page title\"]" });
        await Store().UpsertAsync([folder, browser]);
        var path = Path.Combine(root, "report.md");
        Assert.Equal(0, await Host([]).RunAsync(["export", "--path", path, "--redact", "--no-model", "--home", Data]));
        var report = await File.ReadAllTextAsync(path);
        Assert.Contains("example.test", report);
        Assert.Contains(".txt", report);
        Assert.DoesNotContain("private-note", report);
        Assert.DoesNotContain("Private page title", report);
        Assert.DoesNotContain("private-evidence", report);
        Assert.Equal("[\"Private page title\"]", (await Store().QueryAsync(new ArtifactQuery(Source: "browser")))[0].Metadata["titles"]);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task ExportRefusesToOverwriteAnExistingFile()
    {
        var path = Path.Combine(root, "report.md");
        await File.WriteAllTextAsync(path, "preserved");
        Assert.Equal(1, await Host([]).RunAsync(["export", "--path", path, "--no-model", "--home", Data]));
        Assert.Equal("preserved", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ForgetRequiresExplicitConfirmationThenRemovesStore()
    {
        await Store().UpsertAsync([Item("folder", "one")]);
        Assert.Equal(0, await Host([]).RunAsync(["forget", "--home", Data]));
        Assert.Single(await Store().QueryAsync());
        Assert.Equal(0, await Host([]).RunAsync(["forget", "--yes", "--home", Data]));
        Assert.False(Directory.Exists(Data));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task ForgetPreservesUnrelatedFiles()
    {
        await Store().UpsertAsync([Item("folder", "one")]);
        var keep = Path.Combine(Data, "keep.txt");
        await File.WriteAllTextAsync(keep, "preserve");
        Assert.Equal(0, await Host([]).RunAsync(["forget", "--yes", "--home", Data]));
        Assert.Equal("preserve", await File.ReadAllTextAsync(keep));
        Assert.False(File.Exists(Store().DatabasePath));
    }

    [Fact]
    public async Task ForgetCleansInterruptedBrowserSnapshotsAndPreservesUnknownEntries()
    {
        var snapshot = Path.Combine(Data, "temp", "history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshot);
        await File.WriteAllTextAsync(Path.Combine(snapshot, "History"), "private history");
        await File.WriteAllTextAsync(Path.Combine(snapshot, "History-wal"), "private WAL");
        var unknown = Path.Combine(Data, "temp", "history-owner-notes");
        Directory.CreateDirectory(unknown);
        await File.WriteAllTextAsync(Path.Combine(unknown, "keep.txt"), "preserve");
        var reportDirectory = Directory.CreateDirectory(Path.Combine(Data, "reports")).FullName;
        var partialReport = Path.Combine(reportDirectory, ".exhume-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        await File.WriteAllTextAsync(partialReport, "private diff");
        Assert.Equal(0, await Host([]).RunAsync(["forget", "--yes", "--home", Data]));
        Assert.False(Directory.Exists(snapshot));
        Assert.False(File.Exists(partialReport));
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(unknown, "keep.txt")));
    }

    [Fact]
    public async Task RedactedExportKeepsExtensionsFromRealFolderAdapterSamples()
    {
        await Store().UpsertAsync([Item("folder", "one", new() { ["samples"] = "private-note.txt | personal-photo.jpg" })]);
        var path = Path.Combine(root, "report.md");
        Assert.Equal(0, await Host([]).RunAsync(["export", "--path", path, "--redact", "--no-model", "--home", Data]));
        var report = await File.ReadAllTextAsync(path);
        Assert.Contains(".txt", report);
        Assert.Contains(".jpg", report);
        Assert.DoesNotContain("private-note", report);
        Assert.DoesNotContain("personal-photo", report);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task ExportReportsNarrationFallback()
    {
        await Store().UpsertAsync([Item("folder", "one")]);
        var path = Path.Combine(root, "report.md");
        Assert.Equal(0, await Host([]).RunAsync(["export", "--path", path, "--max-attempts", "1", "--home", Data]));
        Assert.Contains("Narration unavailable", output.ToString());
        Assert.Contains("raw evidence", await File.ReadAllTextAsync(path));
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task CancellationAfterCommittedBatchStillRecordsScan()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new StoreDecorator(Store(), () => cancellation.Cancel());
        var host = Host([new Source("folder", [Item("folder", "one")])], store);
        Assert.Equal(2, await host.RunAsync(["dig", "--source", "folder", "--home", Data], cancellation.Token));
        Assert.Equal(new StoreCounts(1, 0, 1), await Store().GetCountsAsync());
        Assert.Contains("cancel", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StorageWriteFailureIsFatalRatherThanSourceFailure()
    {
        var store = new StoreDecorator(Store(), () => throw new IOException("storage unavailable"));
        Assert.Equal(3, await Host([new Source("folder", [Item("folder", "one")])], store)
            .RunAsync(["dig", "--source", "folder", "--home", Data]));
    }

    [Theory]
    [InlineData("eras", "--year", "0")]
    [InlineData("timeline", "--month", "2024-13")]
    [InlineData("obituaries", "--limit", "0")]
    public async Task InvalidViewFiltersReturnUsageError(params string[] args)
    {
        Assert.Equal(1, await Host([]).RunAsync([.. args, "--home", Data, "--no-model"]));
        Assert.False(Directory.Exists(Data));
    }

    private Artifact Item(string source, string key, Dictionary<string, string>? metadata = null) =>
        new(source, source == "folder" ? "FolderMonth" : "BrowserMonth", key,
            source == "browser" ? "2023-02 example.test" : "2023-02 document",
            new(2023, 2, 1, 0, 0, 0, TimeSpan.Zero), new(2023, 2, 10, 0, 0, 0, TimeSpan.Zero),
            Path.Combine(root, "private-evidence"), metadata);

    private SqliteArtifactStore Store() => new(Data);

    private CliHost Host(IReadOnlyList<IEvidenceAdapter> adapters, IArtifactStore? store = null)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No, Out = new AnsiConsoleOutput(output)
        });
        console.Profile.Width = 240;
        return new CliHost(console, http, storeFactory: store is null ? null : _ => store, adapters: adapters, environment: new Dictionary<string, string?>(),
            workingDirectory: root, configFilePath: Path.Combine(root, "absent.json"));
    }

    public void Dispose()
    {
        http.Dispose();
        output.Dispose();
        Directory.Delete(root, true);
    }

    private sealed class NoModelHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("fixture model unavailable");
        }
    }

    private sealed class StoreDecorator(IArtifactStore inner, Action afterWrite) : IArtifactStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);
        public async Task UpsertAsync(IEnumerable<Artifact> artifacts, CancellationToken cancellationToken = default)
        {
            await inner.UpsertAsync(artifacts, cancellationToken);
            afterWrite();
        }
        public Task<IReadOnlyList<Artifact>> QueryAsync(ArtifactQuery? query = null, CancellationToken cancellationToken = default) => inner.QueryAsync(query, cancellationToken);
        public Task<CachedNarration?> GetNarrationAsync(string key, CancellationToken cancellationToken = default) => inner.GetNarrationAsync(key, cancellationToken);
        public Task SaveNarrationAsync(CachedNarration entry, CancellationToken cancellationToken = default) => inner.SaveNarrationAsync(entry, cancellationToken);
        public Task RecordScanAsync(ScanRecord scan, CancellationToken cancellationToken = default) => inner.RecordScanAsync(scan, cancellationToken);
        public Task<StoreCounts> GetCountsAsync(CancellationToken cancellationToken = default) => inner.GetCountsAsync(cancellationToken);
        public Task ForgetAsync(CancellationToken cancellationToken = default) => inner.ForgetAsync(cancellationToken);
    }

    private sealed class Source(string name, IReadOnlyList<Artifact> artifacts, bool fail = false, CancellationTokenSource? cancel = null) : IEvidenceAdapter
    {
        public string Name => name;
        public SourceStatus Check(ScanOptions options) => new(SourceAvailability.Available, "synthetic fixture");
        public async IAsyncEnumerable<Artifact> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (fail) throw new IOException("synthetic source failure");
            foreach (var artifact in artifacts) yield return artifact;
            cancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
