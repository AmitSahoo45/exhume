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
    private string RepositoryPath => Path.Combine(root, "repository");

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

    [Theory]
    [InlineData("haunt")]
    [InlineData("dig")]
    public async Task RepositoryCannotContainTheApplicationDataDirectory(string command)
    {
        git.RepositoryPath = root;
        var args = command == "haunt"
            ? new[] { "haunt", "--path", root, "--home", Data, "--no-model" }
            : new[] { "dig", "--source", "git", "--path", root, "--home", Data };
        Assert.Equal(1, await Host().RunAsync(args));
        Assert.Contains("EXHUME_HOME", output.ToString());
        Assert.False(Directory.Exists(Data));
        Assert.Equal(0, git.PersonaReads);
        Assert.Equal(0, http.Calls);
    }

    [Theory]
    [InlineData("haunt")]
    [InlineData("dig")]
    public async Task RepositoryContextReadFailureHasASourceExitWithoutEscaping(string command)
    {
        git.ContextFailure = new LibGit2Sharp.LibGit2SharpException("synthetic source became unreadable");
        var args = command == "haunt"
            ? new[] { "haunt", "--path", RepositoryPath, "--home", Data, "--no-model" }
            : new[] { "dig", "--source", "git", "--path", RepositoryPath, "--home", Data };
        Assert.Equal(2, await Host().RunAsync(args));
        Assert.Contains("git", output.ToString());
        Assert.DoesNotContain("Data directory is not writable", output.ToString());
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task RepositoryAtDriveRootStillRejectsContainedApplicationData()
    {
        git.RepositoryPath = Path.GetPathRoot(root)!;
        Assert.Equal(1, await Host().RunAsync(["haunt", "--path", git.RepositoryPath, "--home", Data, "--no-model"]));
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public async Task UncitableDiffDoesNotBuildPersonaOrCallModel()
    {
        git.Chunks = [new("deleted.txt", "-removed", [], true)];
        Assert.Equal(0, await Host().RunAsync(["haunt", "--path", RepositoryPath, "--home", Data]));
        Assert.Contains("no citable lines", output.ToString());
        Assert.Equal(0, git.PersonaReads);
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task HauntReportsGlobalFileOmissionsAndPassesUntrackedOptIn()
    {
        git.Chunks = [new("sample.txt", "+new line", [1], true, 7)];
        Assert.Equal(0, await Host().RunAsync(["haunt", "--path", RepositoryPath, "--home", Data, "--include-untracked", "--no-model"]));
        Assert.True(git.LastOptions!.IncludeUntracked);
        Assert.Contains(Data, git.LastOptions.ExcludedPaths);
        Assert.Contains("7 changed files omitted", output.ToString());
    }

    [Fact]
    public async Task NarratedHauntUsesCacheAndRendersOnlyValidatedComments()
    {
        http.Responses.Enqueue("""{"voiceDescription":"Dry","habits":["small changes"],"petPhrases":[],"thingsToNitpick":[],"technicalWorldview":"Keep it clear"}""");
        http.Responses.Enqueue("""{"comments":[{"file":"sample.txt","lineNumber":1,"comment":"Check this line."}]}""");
        var host = Host();
        Assert.Equal(0, await host.RunAsync(["haunt", "--path", root, "--home", Data]));
        Assert.Equal(0, await host.RunAsync(["haunt", "--path", root, "--home", Data]));
        Assert.Equal(2, http.Calls);
        Assert.Equal(1, git.PersonaReads);
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
        git.RepositoryPath ??= RepositoryPath;
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
        public string? RepositoryPath { get; set; }
        public Exception? ContextFailure { get; set; }
        public PersonaContext GetPersonaContext(ScanOptions options) => ContextFailure is { } error ? throw error
            : new(RepositoryPath ?? options.Path!, "owner@example.test", new(2020,1,1,0,0,0,TimeSpan.Zero));
        public string Name => "git";
        public int PersonaReads { get; private set; }
        public ScanOptions? LastOptions { get; private set; }
        public IReadOnlyList<DiffChunk> Chunks { get; set; } =
            [new("sample.txt", "@@ -1,1 +1,1 @@\n-old line\n+new line\n", [1])];
        public SourceStatus Check(ScanOptions options) => new(SourceAvailability.Available, "fixture");
        public Task<IReadOnlyList<DiffChunk>> ReadDiffAsync(ScanOptions options, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(Chunks);
        }
        public Task<PersonaInput> BuildPersonaInputAsync(ScanOptions options, CancellationToken cancellationToken = default)
        {
            PersonaReads++;
            return Task.FromResult(new PersonaInput(RepositoryPath ?? options.Path!, "owner@example.test", new(2020,1,1,0,0,0,TimeSpan.Zero),
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
