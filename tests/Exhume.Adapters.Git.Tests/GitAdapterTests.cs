using System.Security.Cryptography;
using System.Text.Json;
using Exhume.Core;
using LibGit2Sharp;

namespace Exhume.Adapters.Git.Tests;

public sealed class GitAdapterTests
{
    [Fact]
    public async Task GroupsTwoMonthsByAuthorCaseInsensitivelyAndLeavesRepositoryUnchanged()
    {
        using var fixture = new RepositoryFixture();
        fixture.Commit("2023-01-02T04:00:00Z", "First", "first.cs", "class First {}\n", "OWNER@example.invalid");
        fixture.Commit("2023-01-03T05:00:00Z", "Other", "other.py", "pass\n", "other@example.invalid");
        fixture.Commit("2023-02-04T06:00:00Z", "Second", "second.cs", "class Second {}\n");
        var before = fixture.Fingerprint();

        var items = await Collect(new GitAdapter(), fixture.Options);

        Assert.Equal(2, items.Count);
        Assert.All(items, x => Assert.Equal("1", x.Metadata["commits"]));
        Assert.Equal("First", JsonSerializer.Deserialize<string[]>(items[0].Metadata["messages"])![0]);
        Assert.Equal("Second", JsonSerializer.Deserialize<string[]>(items[1].Metadata["messages"])![0]);
        Assert.Contains(".cs", items[0].Metadata["languages"]);
        Assert.DoesNotContain("other.py", string.Join(" ", items.SelectMany(x => x.Metadata.Values)));
        Assert.Equal(before, fixture.Fingerprint());
        Assert.Equal(items.Select(x => x.Id), (await Collect(new GitAdapter(), fixture.Options)).Select(x => x.Id));
    }

    [Fact]
    public async Task BoundsMonthSubjectsAndCountsDistinctFilesAndLocalHours()
    {
        using var fixture = new RepositoryFixture();
        for (var i = 0; i < 12; i++)
            fixture.Commit($"2023-01-{i + 1:00}T23:00:00+05:30", $"{i:00} " + new string('x', 500), "same.cs", $"value {i}\n");

        var item = Assert.Single(await Collect(new GitAdapter(), fixture.Options));
        var subjects = JsonSerializer.Deserialize<string[]>(item.Metadata["messages"])!;

        Assert.Equal("12", item.Metadata["commits"]);
        Assert.Equal("1", item.Metadata["filesTouched"]);
        Assert.Equal(8, subjects.Length);
        Assert.StartsWith("00 ", subjects[0]);
        Assert.All(subjects, x => Assert.InRange(x.Length, 1, 160));
        Assert.Equal(12, JsonSerializer.Deserialize<Dictionary<int, int>>(item.Metadata["hours"])![23]);
    }

    [Fact]
    public void RequiresPathAndExplicitOrConfiguredAuthor()
    {
        using var fixture = new RepositoryFixture();
        var adapter = new GitAdapter();
        Assert.Equal(SourceAvailability.Unavailable, adapter.Check(new ScanOptions()).Availability);
        fixture.Repository.Config.Set("user.email", "");
        Assert.Equal(SourceAvailability.Unavailable, adapter.Check(fixture.Options).Availability);
        Assert.Equal(SourceAvailability.Available, adapter.Check(fixture.Options with { Author = "owner@example.invalid" }).Availability);
    }

    [Fact]
    public async Task PersonaUsesStrictCutoffAndEvenlySpreadsAtMostTwoHundredSubjects()
    {
        using var fixture = new RepositoryFixture();
        var start = DateTimeOffset.Parse("2020-01-01T00:00:00Z");
        for (var i = 0; i < 205; i++)
            fixture.Commit(start.AddDays(i).ToString("O"), $"subject {i:000}", "same.cs", $"value {i}\n");
        fixture.Commit("2021-01-01T00:00:00Z", "At cutoff", "same.cs", "boundary\n");
        fixture.Commit("2021-01-02T00:00:00Z", "Later", "same.cs", "later\n");

        var input = await new GitAdapter().BuildPersonaInputAsync(fixture.Options with { Cutoff = DateTimeOffset.Parse("2021-01-01T00:00:00Z") });

        Assert.Equal(205, input.TotalCommitCount);
        Assert.Equal(200, input.CommitSubjects.Count);
        Assert.Equal("subject 000", input.CommitSubjects[0]);
        Assert.Equal("subject 204", input.CommitSubjects[^1]);
        Assert.DoesNotContain("At cutoff", input.CommitSubjects);
        Assert.InRange(input.DiffSamples.Count, 1, 10);
        Assert.All(input.DiffSamples, x => Assert.InRange(x.Text.Split('\n').Length, 1, 45));
        Assert.Equal(205, input.Hours.Values.Sum());
        Assert.Contains(".cs", input.Languages.Keys);
    }

    [Fact]
    public async Task PersonaDefaultCutoffExcludesRecentAndOtherAuthorCommits()
    {
        using var fixture = new RepositoryFixture();
        fixture.Commit(DateTimeOffset.UtcNow.AddYears(-3).ToString("O"), "Old", "a.cs", "one\n");
        fixture.Commit(DateTimeOffset.UtcNow.AddYears(-2).ToString("O"), "Other", "a.cs", "two\n", "other@example.invalid");
        fixture.Commit(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"), "Recent", "a.cs", "three\n");

        var input = await new GitAdapter().BuildPersonaInputAsync(fixture.Options);

        Assert.Equal(1, input.TotalCommitCount);
        Assert.Equal(["Old"], input.CommitSubjects);
        Assert.Equal(new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddMonths(-18), input.Cutoff);
    }

    [Fact]
    public async Task RepeatedDefaultPersonaReadsUseTheSameCutoffAndPreserveExplicitTime()
    {
        using var fixture = new RepositoryFixture();
        var adapter = new GitAdapter();

        var first = await adapter.BuildPersonaInputAsync(fixture.Options);
        var second = await adapter.BuildPersonaInputAsync(fixture.Options);
        var explicitCutoff = new DateTimeOffset(2022, 5, 10, 15, 45, 59, TimeSpan.FromHours(5.5));
        var explicitInput = await adapter.BuildPersonaInputAsync(fixture.Options with { Cutoff = explicitCutoff });

        Assert.Equal(first.Cutoff, second.Cutoff);
        Assert.Equal(TimeSpan.Zero, first.Cutoff.TimeOfDay);
        Assert.Equal(explicitCutoff.ToUniversalTime(), explicitInput.Cutoff);
    }

    [Fact]
    public async Task DiffIncludesStagedUnstagedDeletedAndUntrackedFilesWithoutChangingAnyFile()
    {
        using var fixture = new RepositoryFixture();
        fixture.Commit("2023-01-01T00:00:00Z", "Base", "staged.cs", "old\n");
        fixture.Commit("2023-01-02T00:00:00Z", "More", "unstaged.cs", "old\n");
        fixture.Commit("2023-01-03T00:00:00Z", "Delete later", "deleted.cs", "remove\n");
        fixture.Write("staged.cs", "staged new\n");
        Commands.Stage(fixture.Repository, "staged.cs");
        fixture.Write("unstaged.cs", "unstaged new\n");
        fixture.Write("fresh.cs", "new file\nsecond line\n");
        File.Delete(Path.Combine(fixture.Root, "deleted.cs"));
        Commands.Stage(fixture.Repository, "deleted.cs");
        var before = fixture.Fingerprint();

        var chunks = await new GitAdapter().ReadDiffAsync(fixture.Options with { IncludeUntracked = true });

        Assert.Equal(["deleted.cs", "fresh.cs", "staged.cs", "unstaged.cs"], chunks.Select(x => x.File).Order().ToArray());
        Assert.Contains("staged new", Assert.Single(chunks, x => x.File == "staged.cs").Text);
        Assert.Contains("unstaged new", Assert.Single(chunks, x => x.File == "unstaged.cs").Text);
        Assert.Equal([1, 2], Assert.Single(chunks, x => x.File == "fresh.cs").LineNumbers);
        Assert.Empty(Assert.Single(chunks, x => x.File == "deleted.cs").LineNumbers);
        Assert.Equal(before, fixture.Fingerprint());
    }

    [Fact]
    public async Task DiffTracksCurrentLineNumbersAcrossHunksAndCombinedChanges()
    {
        using var fixture = new RepositoryFixture();
        fixture.Commit("2023-01-01T00:00:00Z", "Base", "change.cs", string.Join('\n', Enumerable.Range(1, 40).Select(x => $"line {x}")) + "\n");
        var lines = File.ReadAllLines(Path.Combine(fixture.Root, "change.cs"));
        lines[1] = "staged replacement";
        fixture.Write("change.cs", string.Join('\n', lines) + "\n");
        Commands.Stage(fixture.Repository, "change.cs");
        lines[34] = "working replacement";
        fixture.Write("change.cs", string.Join('\n', lines) + "\n");

        var chunk = Assert.Single(await new GitAdapter().ReadDiffAsync(fixture.Options));

        Assert.Contains("staged replacement", chunk.Text);
        Assert.Contains("working replacement", chunk.Text);
        Assert.Contains(2, chunk.LineNumbers);
        Assert.Contains(35, chunk.LineNumbers);
        Assert.DoesNotContain(20, chunk.LineNumbers);
        Assert.All(chunk.LineNumbers, x => Assert.InRange(x, 1, 40));
    }

    [Fact]
    public async Task CapsDiffFilesAndLinesAndMarksDroppedContent()
    {
        using var fixture = new RepositoryFixture();
        fixture.Commit("2023-01-01T00:00:00Z", "Base", "base.cs", "base\n");
        for (var i = 0; i < 25; i++)
            fixture.Write($"new-{i:00}.cs", string.Join('\n', Enumerable.Range(1, 300).Select(x => $"line {x}")) + "\n");

        var chunks = await new GitAdapter().ReadDiffAsync(fixture.Options with { IncludeUntracked = true });

        Assert.Equal(20, chunks.Count);
        Assert.All(chunks, x => Assert.True(x.Truncated));
        Assert.All(chunks, x => Assert.InRange(x.LineNumbers.Count, 1, 200));
        Assert.All(chunks, x => Assert.InRange(x.Text.Split('\n').Length, 1, 205));
    }

    [Fact]
    public async Task CleanRepositoryHasNoDiffAndUnbornHeadCanReadNewFile()
    {
        using var fixture = new RepositoryFixture();
        Assert.Empty(await new GitAdapter().ReadDiffAsync(fixture.Options));
        fixture.Write("first.cs", "first\n");
        Assert.Empty(await new GitAdapter().ReadDiffAsync(fixture.Options));
        Assert.Single(await new GitAdapter().ReadDiffAsync(fixture.Options with { IncludeUntracked = true }));
        fixture.Commit("2023-01-01T00:00:00Z", "First", "first.cs", "first\n");
        Assert.Empty(await new GitAdapter().ReadDiffAsync(fixture.Options));
    }

    [Fact]
    public async Task MissingAuthorDuringDiscoveryWarnsAndContinuesWithLaterRepository()
    {
        using var root = new RepositoryFixture();
        root.Repository.Config.Set("user.email", "");
        using var nested = new RepositoryFixture(Path.Combine(root.Root, "nested"));
        nested.Commit("2023-01-01T00:00:00Z", "Visible", "one.cs", "one\n");
        var progress = new CaptureProgress();

        var artifacts = await Collect(new GitAdapter(), root.Options with { ScanRepositories = true }, progress);

        Assert.Single(artifacts);
        Assert.Contains(progress.Items, x => x.Warning is not null && !x.Warning.Contains(root.Root, StringComparison.Ordinal));
        Assert.DoesNotContain(progress.Items, x => x.Error is not null);
    }

    [Fact]
    public async Task MissingAuthorWithoutAnyReadableRepositoryReportsWholeSourceFailure()
    {
        using var fixture = new RepositoryFixture();
        fixture.Repository.Config.Set("user.email", "");
        var progress = new CaptureProgress();

        Assert.Empty(await Collect(new GitAdapter(), fixture.Options, progress));

        Assert.Contains(progress.Items, x => x.Error is not null);
    }

    [Fact]
    public async Task DiffDefaultsToTrackedEditsAndPrioritizesThemWhenUntrackedIsEnabled()
    {
        using var fixture = new RepositoryFixture();
        fixture.Commit("2023-01-01T00:00:00Z", "Tracked", "z-tracked.cs", "old\n");
        fixture.Write("z-tracked.cs", "changed\n");
        for (var index = 0; index < 25; index++) fixture.Write($"a-scratch-{index:00}.txt", "scratch\n");
        fixture.Write(".env", "private value\n");

        var defaultChunks = await new GitAdapter().ReadDiffAsync(fixture.Options);
        var optedIn = await new GitAdapter().ReadDiffAsync(fixture.Options with { IncludeUntracked = true });

        Assert.Equal("z-tracked.cs", Assert.Single(defaultChunks).File);
        Assert.Equal("z-tracked.cs", optedIn[0].File);
        Assert.Equal(20, optedIn.Count);
        Assert.All(optedIn, chunk => Assert.True(chunk.Truncated));
        Assert.All(optedIn, chunk => Assert.Equal(7, chunk.OmittedFileCount));
    }

    [Fact]
    public async Task ConfiguredAppDataIsExcludedFromTrackedAndUntrackedDiffs()
    {
        using var fixture = new RepositoryFixture();
        fixture.Commit("2023-01-01T00:00:00Z", "Tracked", "appdata/tracked.txt", "old private\n");
        fixture.Commit("2023-01-02T00:00:00Z", "Code", "code.cs", "old\n");
        fixture.Write("appdata/tracked.txt", "new private\n");
        fixture.Write("appdata/exhume.db", "stored private data\n");
        fixture.Write("appdata/reports/review.md", "private report\n");
        fixture.Write("appdata-sibling.txt", "allowed\n");
        fixture.Write("code.cs", "new\n");

        var chunks = await new GitAdapter().ReadDiffAsync(fixture.Options with
        {
            IncludeUntracked = true, ExcludedPaths = [Path.Combine(fixture.Root, "appdata")]
        });

        Assert.Equal(["code.cs", "appdata-sibling.txt"], chunks.Select(x => x.File));
        Assert.DoesNotContain(chunks, chunk => chunk.Text.Contains("private", StringComparison.Ordinal));
    }

    [Fact]
    public void PersonaContextResolvesRepositoryRootFromNestedPathsWithoutReadingHistory()
    {
        using var fixture = new RepositoryFixture();
        fixture.Commit("2023-01-01T00:00:00Z", "Older", "one.cs", "old\n");
        var oldCommit = fixture.Repository.Head.Tip.Sha;
        fixture.Commit("2023-01-02T00:00:00Z", "Newer", "one.cs", "new\n");
        var oldObject = Path.Combine(fixture.Root, ".git", "objects", oldCommit[..2], oldCommit[2..]);
        File.SetAttributes(oldObject, FileAttributes.Normal);
        File.Delete(oldObject);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "nested", "child"));
        var explicitCutoff = new DateTimeOffset(2020, 4, 5, 6, 7, 8, TimeSpan.Zero);

        var context = new GitAdapter().GetPersonaContext(fixture.Options with
        {
            Path = Path.Combine(fixture.Root, "nested", "child"), Author = "OWNER@example.invalid", Cutoff = explicitCutoff
        });

        Assert.Equal(fixture.Root, context.RepositoryPath);
        Assert.Equal("owner@example.invalid", context.Author);
        Assert.Equal(explicitCutoff, context.Cutoff);
    }

    [Fact]
    public void PersonaContextResolvesParentJunctionAndGitDirectoryToSameWorkingTree()
    {
        using var fixture = new RepositoryFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "nested"));
        var alias = Path.Combine(fixture.Root, "alias");
        CreateDirectoryLink(alias, fixture.Root);
        try
        {
            var adapter = new GitAdapter();
            var context = adapter.GetPersonaContext(fixture.Options with { Path = Path.Combine(alias, "nested") });
            var metadataContext = adapter.GetPersonaContext(fixture.Options with { Path = Path.Combine(alias, ".git") });

            Assert.Equal(fixture.Root, context.RepositoryPath);
            Assert.Equal(context, metadataContext);
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public void PersonaContextUsesLinkedWorktreeRootInsteadOfMainRepository()
    {
        using var fixture = new RepositoryFixture();
        fixture.Commit("2023-01-01T00:00:00Z", "Initial", "one.cs", "one\n");
        var linkedPath = Path.Combine(fixture.Root, "linked-worktree");
        fixture.Repository.Worktrees.Add("fixture-worktree", linkedPath, false);
        Directory.CreateDirectory(Path.Combine(linkedPath, "nested"));

        var context = new GitAdapter().GetPersonaContext(fixture.Options with { Path = Path.Combine(linkedPath, "nested") });

        Assert.Equal(linkedPath, context.RepositoryPath);
    }

    [Fact]
    public async Task ReadingContextHistoryAndDiffPreservesIndexAndAllSourceFileMetadata()
    {
        using var fixture = new RepositoryFixture();
        fixture.Commit("2020-01-01T00:00:00Z", "Initial", "one.cs", "one\n");
        fixture.Write("one.cs", "staged\n");
        Commands.Stage(fixture.Repository, "one.cs");
        fixture.Write("one.cs", "working\n");
        fixture.Write("new.cs", "untracked\n");
        File.SetLastWriteTimeUtc(Path.Combine(fixture.Root, ".git", "index"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = fixture.Fingerprint();
        var adapter = new GitAdapter();

        _ = adapter.GetPersonaContext(fixture.Options);
        _ = await adapter.BuildPersonaInputAsync(fixture.Options);
        _ = await Collect(adapter, fixture.Options);
        _ = await adapter.ReadDiffAsync(fixture.Options with { IncludeUntracked = true });

        Assert.Equal(before, fixture.Fingerprint());
    }

    [Fact]
    public async Task DiscoveryStopsAtTwoLevelsAndCancellationPropagates()
    {
        using var root = new RepositoryFixture();
        using var nested = new RepositoryFixture(Path.Combine(root.Root, "group", "nested"));
        nested.Commit("2023-01-01T00:00:00Z", "Nested", "first.cs", "one\n");
        using var tooDeep = new RepositoryFixture(Path.Combine(root.Root, "group", "deeper", "ignored"));
        tooDeep.Commit("2023-01-01T00:00:00Z", "Deep", "first.cs", "one\n");

        var items = await Collect(new GitAdapter(), root.Options with { ScanRepositories = true });

        Assert.Single(items);
        Assert.Contains("nested", items[0].Evidence);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitAdapter().ReadDiffAsync(root.Options, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitAdapter().BuildPersonaInputAsync(root.Options, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var unused in new GitAdapter().ScanAsync(root.Options, cancellationToken: cancellation.Token)) { }
        });
    }

    private static async Task<List<Artifact>> Collect(GitAdapter adapter, ScanOptions options, IProgress<ScanProgress>? progress = null)
    {
        var items = new List<Artifact>();
        await foreach (var item in adapter.ScanAsync(options, progress)) items.Add(item);
        return items;
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private sealed class CaptureProgress : IProgress<ScanProgress>
    {
        public List<ScanProgress> Items { get; } = [];
        public void Report(ScanProgress value) => Items.Add(value);
    }

    private sealed class RepositoryFixture : IDisposable
    {
        public string Root { get; }
        public Repository Repository { get; }
        public ScanOptions Options => new() { Path = Root };
        public RepositoryFixture(string? root = null)
        {
            Root = DataDirectoryPolicy.Normalize(root ?? Path.Combine(Path.GetTempPath(), "exhume-git-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Root);
            LibGit2Sharp.Repository.Init(Root);
            Repository = new Repository(Root);
            Repository.Config.Set("user.name", "owner");
            Repository.Config.Set("user.email", "owner@example.invalid");
            Repository.Config.Set("core.autocrlf", false);
        }
        public void Write(string name, string text)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        public void Commit(string when, string subject, string file, string text, string email = "owner@example.invalid")
        {
            Write(file, text);
            Commands.Stage(Repository, file);
            var signature = new Signature("owner", email, DateTimeOffset.Parse(when));
            Repository.Commit(subject, signature, signature);
        }
        public string[] Fingerprint() => Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories).Order()
            .Select(path =>
            {
                FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                var fileEvidence = info is FileInfo file
                    ? file.Length + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "directory";
                return Path.GetRelativePath(Root, path) + ":" + info.CreationTimeUtc.Ticks + ":" + info.LastWriteTimeUtc.Ticks + ":" + info.Attributes + ":" + fileEvidence;
            }).ToArray();
        public void Dispose()
        {
            Repository.Dispose();
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
