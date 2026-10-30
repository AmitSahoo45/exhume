using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Exhume.Core;
using Spectre.Console;

namespace Exhume.Cli.Tests;

public sealed class ReviewWorkflowTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(DataDirectoryPolicy.Normalize(
        Path.Combine(Path.GetTempPath(), "exhume-review-" + Guid.NewGuid().ToString("N")))).FullName;
    private readonly StringWriter output = new();
    private readonly Handler http = new();
    private string Data => Path.Combine(root, "data");
    private SqliteArtifactStore Store => new(Data);

    [Fact]
    public async Task ItemWarningKeepsSuccessfulArtifactsAndDoesNotFailTheSource()
    {
        Assert.Equal(0, await Host([new WarningSource()]).RunAsync(["dig", "--source", "folder", "--home", Data]));
        Assert.Single(await Store.QueryAsync());
        Assert.Contains("1 warning", output.ToString());
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT warning_count FROM scans WHERE error IS NULL;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("eras")]
    [InlineData("dig")]
    public async Task ProhibitedDataDirectoryReportsStorageExitAndExactOverridePath(string command)
    {
        const string prohibited = @"C:\Exhume-review-never-write";
        Assert.Equal(3, await Host().RunAsync([command, "--home", prohibited]));
        Assert.Contains(prohibited, output.ToString());
        Assert.Contains("EXHUME_HOME", output.ToString());
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task ExportParentFileReturnsDestinationErrorWithoutStackTraceOrCreatingStore()
    {
        var parent = Path.Combine(root, "parent.txt");
        await File.WriteAllTextAsync(parent, "preserved");
        Assert.Equal(3, await Host().RunAsync(["export", "--path", Path.Combine(parent, "report.md"), "--home", Data, "--no-model"]));
        Assert.Contains("Export destination", output.ToString());
        Assert.DoesNotContain("at Exhume", output.ToString());
        Assert.Equal("preserved", await File.ReadAllTextAsync(parent));
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public async Task RedactedSamplesDoNotLeakDottedTitlesAsExtensions()
    {
        await Store.UpsertAsync([Folder(new() { ["samples"] = "A.B.C. Private Book Title | safe.txt | secret.reallylongsuffix | .env" })]);
        var path = Path.Combine(root, "redacted.md");
        Assert.Equal(0, await Host().RunAsync(["export", "--path", path, "--home", Data, "--redact", "--no-model"]));
        var report = await File.ReadAllTextAsync(path);
        Assert.Contains(".txt", report);
        Assert.DoesNotContain("Private Book Title", report);
        Assert.DoesNotContain("reallylongsuffix", report);
    }

    [Fact]
    public async Task RedactedExportGeneratesSafeProseWithoutReusingPrivateCacheOrSendingPrivateFields()
    {
        await Store.UpsertAsync([
            Folder(new() { ["count"] = "2", ["samples"] = "SECRET_FILE_7.txt", ["arbitrary"] = "SECRET_METADATA_7" }),
            new Artifact("windows", "DeadProgram", "tool", "Tool", null, null, "SECRET_EVIDENCE_7",
                new Dictionary<string, string> { ["exePath"] = "SECRET_PATH_7", ["confidence"] = "high" })
        ]);
        var host = Host();
        http.Label = "PRIVATE_PROSE_7";
        Assert.Equal(0, await host.RunAsync(["export", "--path", Path.Combine(root, "private.md"), "--home", Data]));
        int before = http.Calls;
        http.Label = "Shareable prose";
        var safe = Path.Combine(root, "safe.md");
        Assert.Equal(0, await host.RunAsync(["export", "--path", safe, "--home", Data, "--redact"]));
        Assert.Equal(before + 2, http.Calls);
        Assert.All(http.Bodies.Skip(before), body => Assert.DoesNotContain("SECRET_", body));
        var text = await File.ReadAllTextAsync(safe);
        Assert.Contains("Shareable prose", text);
        Assert.DoesNotContain("PRIVATE_PROSE_7", text);
        Assert.DoesNotContain("SECRET_", text);
        Assert.Equal(0, await host.RunAsync(["export", "--path", Path.Combine(root, "safe-again.md"), "--home", Data, "--redact"]));
        Assert.Equal(before + 2, http.Calls);
        Assert.Contains("SECRET_FILE_7", (await Store.QueryAsync(new(Source: "folder")))[0].Metadata["samples"]);
    }

    [Fact]
    public async Task NonLoopbackNarrationWarnsOnceAndRawViewsDoNotSuggestDataWasSent()
    {
        await Store.UpsertAsync([Folder(new() { ["count"] = "2" })]);
        Assert.Equal(0, await Host().RunAsync(["eras", "--home", Data, "--ollama-url", "https://model.example.test", "--max-attempts", "1"]));
        Assert.Contains("outside this machine", output.ToString());
        Assert.Equal(1, output.ToString().Split("outside this machine").Length - 1);
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await Host().RunAsync(["eras", "--home", Data, "--ollama-url", "https://model.example.test", "--no-model"]));
        Assert.DoesNotContain("outside this machine", output.ToString());
    }

    [Fact]
    public async Task ForgetRemovesOnlyOwnedNarrationLogAlongWithCorruptStore()
    {
        Directory.CreateDirectory(Path.Combine(Data, "logs"));
        await File.WriteAllTextAsync(Store.DatabasePath, "corrupt private data");
        var log = Path.Combine(Data, "logs", "narration.jsonl");
        var keep = Path.Combine(Data, "logs", "keep.txt");
        await File.WriteAllTextAsync(log, "private timings");
        await File.WriteAllTextAsync(keep, "owner notes");
        Assert.Equal(0, await Host().RunAsync(["forget", "--home", Data, "--yes"]));
        Assert.False(File.Exists(log));
        Assert.False(File.Exists(Store.DatabasePath));
        Assert.Equal("owner notes", await File.ReadAllTextAsync(keep));
    }

    private static Artifact Folder(Dictionary<string, string> metadata) => new("folder", "FolderMonth", "folder", "2020-02 document",
        new(2020,2,1,0,0,0,TimeSpan.Zero), new(2020,2,2,0,0,0,TimeSpan.Zero), "SECRET_EVIDENCE_7", metadata);

    private CliHost Host(IReadOnlyList<IEvidenceAdapter>? adapters = null)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(output)
        });
        console.Profile.Width = 240;
        return new(console, http, adapters: adapters ?? [], environment: new Dictionary<string,string?>(),
            workingDirectory: root, configFilePath: Path.Combine(root, "missing.json"));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls => Bodies.Count;
        public List<string> Bodies { get; } = [];
        public string Label { get; set; } = "Safe summary";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            object value = body.Contains("causeOfDeath", StringComparison.Ordinal)
                ? new { headline = Label, body = Label, causeOfDeath = "Unknown", epitaph = "Remembered" }
                : new { title = Label, body = Label, themes = new[] { "Evidence" } };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { response = JsonSerializer.Serialize(value), done = true })) };
        }
    }

    private sealed class WarningSource : IEvidenceAdapter
    {
        public string Name => "folder";
        public SourceStatus Check(ScanOptions options) => new(SourceAvailability.Available, "fixture");
        public async IAsyncEnumerable<Artifact> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            progress?.Report(new(Name, "item skipped", 0, Warning: "One inaccessible item was skipped."));
            yield return Folder(new() { ["count"] = "1" });
        }
    }

    public void Dispose()
    {
        http.Dispose(); output.Dispose(); Directory.Delete(root, true);
    }
}
