using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Exhume.Adapters.Git;
using Exhume.Core;
using Spectre.Console;

namespace Exhume.Cli.Tests;

public sealed class HauntWorkflowTests : IDisposable
{
    private readonly string root = CreateRoot();
    private readonly StringWriter output = new();
    private readonly FakeGit git = new();
    private readonly Handler http = new();
    private string Data => Path.Combine(root, "data");

    private static string CreateRoot() => Directory.CreateDirectory(DataDirectoryPolicy.Normalize(
        Path.Combine(Path.GetTempPath(), "exhume-haunt-flow-" + Guid.NewGuid().ToString("N")))).FullName;

    [Fact]
    public async Task EmptyDiffHasNoPersonaOrModelWork()
    {
        git.Chunks = [];
        Assert.Equal(0, await Host().RunAsync(["haunt", "--path", root, "--home", Data]));
        Assert.Contains("Nothing to haunt", output.ToString());
        Assert.Equal(0, git.PersonaReads);
        Assert.Equal(0, http.Calls);
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public async Task RawHauntShowsPersonaInputAndCurrentDiffWithoutModel()
    {
        Assert.Equal(0, await Host().RunAsync(["haunt", "--path", root, "--home", Data, "--no-model"]));
        Assert.Contains("old subject", output.ToString());
        Assert.Contains("+new line", output.ToString());
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task NarratedHauntUsesCacheAndRendersOnlyValidatedComments()
    {
        http.Responses.Enqueue("""{"voiceDescription":"Dry","habits":["small changes"],"petPhrases":[],"thingsToNitpick":[],"technicalWorldview":"Keep it clear"}""");
        http.Responses.Enqueue("""[{"file":"sample.txt","lineNumber":1,"comment":"Check this line."}]""");
        var host = Host();
        Assert.Equal(0, await host.RunAsync(["haunt", "--path", root, "--home", Data]));
        Assert.Equal(0, await host.RunAsync(["haunt", "--path", root, "--home", Data]));
        Assert.Equal(2, http.Calls);
        Assert.Contains("sample.txt:1", output.ToString());
        Assert.Contains("Check this line.", output.ToString());
        Assert.Contains("Narration calls: 0", output.ToString());
    }

    [Fact]
    public async Task HauntReportStaysInsideDataDirectory()
    {
        Assert.Equal(1, await Host().RunAsync(["haunt", "--path", root, "--home", Data, "--no-model", "--report", "../outside.md"]));
        Assert.False(File.Exists(Path.Combine(root, "outside.md")));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task RawHauntCanSaveExplicitReportInDataDirectory()
    {
        Assert.Equal(0, await Host().RunAsync(["haunt", "--path", root, "--home", Data, "--no-model", "--report", "review.md"]));
        var path = Path.Combine(Data, "reports", "exhume-review.md");
        Assert.True(File.Exists(path));
        Assert.Contains("+new line", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task HauntRefusesReportDirectoryLinkedOutsideDataDirectory()
    {
        Directory.CreateDirectory(Data);
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        var link = Path.Combine(Data, "reports");
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Equal(1, await Host().RunAsync(["haunt", "--path", root, "--home", Data, "--no-model", "--report", "review.md"]));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
            Assert.Equal(0, git.PersonaReads);
        }
        finally { Directory.Delete(link); }
    }

    private CliHost Host()
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(output)
        });
        console.Profile.Width = 240;
        return new(console, http, adapters: [git], environment: new Dictionary<string, string?>(),
            workingDirectory: root, configFilePath: Path.Combine(root, "absent.json"));
    }

    public void Dispose()
    {
        http.Dispose();
        output.Dispose();
        Directory.Delete(root, true);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Queue<string> Responses { get; } = new();
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { response = Responses.Dequeue(), done = true }))
            });
        }
    }

    private sealed class FakeGit : IGitEvidenceReader
    {
        public string Name => "git";
        public int PersonaReads { get; private set; }
        public IReadOnlyList<DiffChunk> Chunks { get; set; } =
            [new("sample.txt", "@@ -1,1 +1,1 @@\n-old line\n+new line\n", [1])];
        public SourceStatus Check(ScanOptions options) => new(SourceAvailability.Available, "fixture");
        public Task<IReadOnlyList<DiffChunk>> ReadDiffAsync(ScanOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Chunks);
        public Task<PersonaInput> BuildPersonaInputAsync(ScanOptions options, CancellationToken cancellationToken = default)
        {
            PersonaReads++;
            return Task.FromResult(new PersonaInput(options.Path!, "owner@example.test", new(2020,1,1,0,0,0,TimeSpan.Zero),
                1, ["old subject"], [], new Dictionary<string,int> { [".txt"] = 1 }, new Dictionary<int,int> { [12] = 1 }));
        }
        public async IAsyncEnumerable<Artifact> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
