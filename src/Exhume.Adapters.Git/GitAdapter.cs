using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Exhume.Core;
using LibGit2Sharp;

namespace Exhume.Adapters.Git;

public sealed class GitAdapter : IGitEvidenceReader
{
    private const int MaximumFiles = 20;
    private const int MaximumPatchLines = 200;
    private const int MaximumPatchCharacters = 12_000;
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private static readonly Regex HunkHeader = new(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.CultureInvariant);
    private static readonly LibGit2Sharp.CompareOptions Comparison = new() { Similarity = SimilarityOptions.None, ContextLines = 3 };
    public string Name => "git";

    public SourceStatus Check(ScanOptions options)
    {
        foreach (var path in Discover(options))
        {
            try
            {
                using var repository = new Repository(path);
                if (Author(repository, options) is not null)
                    return new(SourceAvailability.Available, "A local repository and author email are available.");
            }
            catch (Exception error) when (IsSourceError(error)) { }
        }
        return new(SourceAvailability.Unavailable, "Supply a readable repository path and --author email, or configure user.email in that repository.");
    }

    public async IAsyncEnumerable<Artifact> ScanAsync(ScanOptions options,
        IProgress<ScanProgress>? progress = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long emitted = 0;
        foreach (var path in Discover(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            List<Artifact> artifacts;
            try
            {
                using var repository = new Repository(path);
                var author = Author(repository, options) ?? throw new InvalidDataException("Author email is missing.");
                artifacts = ReadMonths(repository, path, author, progress, cancellationToken);
            }
            catch (Exception error) when (IsSourceError(error))
            {
                progress?.Report(new(Name, "repository skipped", emitted, Error: "A repository could not be read or has no author email."));
                continue;
            }
            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return artifact;
                progress?.Report(new(Name, "aggregating", ++emitted));
            }
        }
        progress?.Report(new(Name, "complete", emitted, emitted));
    }

    public Task<PersonaInput> BuildPersonaInputAsync(ScanOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = RequiredPath(options);
        using var repository = new Repository(path);
        var author = Author(repository, options) ?? throw new InvalidDataException("Supply an author email or configure user.email.");
        var cutoff = (options.Cutoff ?? new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddMonths(-18)).ToUniversalTime();
        var commits = Commits(repository, author, cancellationToken).Where(x => x.Author.When < cutoff).ToArray();
        var languages = new Dictionary<string, int>(StringComparer.Ordinal);
        var hours = new Dictionary<int, int>();
        foreach (var commit in commits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCount(hours, commit.Author.When.Hour);
            using var changes = Changes(repository, commit);
            foreach (var change in changes) AddCount(languages, Extension(change.Path));
        }
        var samples = new List<DiffChunk>();
        foreach (var commit in EvenSample(commits, 200))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var changes = Changes(repository, commit);
            if (changes.Count > 5 || changes.Any(x => BlobTooLarge(repository, x.Oid) || BlobTooLarge(repository, x.OldOid))) continue;
            using var patch = repository.Diff.Compare<Patch>(commit.Parents.FirstOrDefault()?.Tree, commit.Tree, Comparison);
            if (patch.LinesAdded + patch.LinesDeleted > 40) continue;
            var file = patch.OrderBy(x => x.Path, StringComparer.Ordinal).FirstOrDefault(x => !x.IsBinaryComparison && x.Patch.Contains("@@", StringComparison.Ordinal));
            if (file is not null) samples.Add(BoundPatch(file.Path, file.Patch, 40));
            if (samples.Count == 10) break;
        }
        return Task.FromResult(new PersonaInput(path, author, cutoff, commits.Length,
            EvenSample(commits, 200).Select(x => Subject(x.MessageShort)).ToArray(), samples,
            languages.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToDictionary(),
            hours.OrderBy(x => x.Key).ToDictionary()));
    }

    public Task<IReadOnlyList<DiffChunk>> ReadDiffAsync(ScanOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = RequiredPath(options);
        using var repository = new Repository(path);
        if (repository.Info.IsBare) return Task.FromResult<IReadOnlyList<DiffChunk>>([]);
        var head = repository.Head.Tip?.Tree;
        using var changes = repository.Diff.Compare<TreeChanges>(head, DiffTargets.Index | DiffTargets.WorkingDirectory,
            null, null, Comparison);
        var tracked = changes.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var statuses = repository.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true, RecurseUntrackedDirs = true,
            DetectRenamesInIndex = false, DetectRenamesInWorkDir = false
        });
        var paths = tracked.Keys.Concat(statuses.Where(x => x.State.HasFlag(FileStatus.NewInWorkdir)).Select(x => x.FilePath))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var chunks = new List<DiffChunk>();
        foreach (var file in paths.Take(MaximumFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(repository.Info.WorkingDirectory, file));
                if (!SafeWorkingPath(repository.Info.WorkingDirectory, fullPath))
                {
                    chunks.Add(new(file, "Linked file content omitted.", [], true));
                    continue;
                }
                if ((File.Exists(fullPath) && new FileInfo(fullPath).Length > MaximumFileBytes) ||
                    (tracked.TryGetValue(file, out var change) &&
                        (BlobTooLarge(repository, change.OldOid) || BlobTooLarge(repository, change.Oid))))
                {
                    chunks.Add(new(file, "File exceeds the diff read limit.", [], true));
                    continue;
                }
                if (!tracked.ContainsKey(file))
                {
                    chunks.Add(ReadNewFile(file, fullPath, cancellationToken));
                    continue;
                }
                using var patch = repository.Diff.Compare<Patch>(head, DiffTargets.Index | DiffTargets.WorkingDirectory,
                    [file], new ExplicitPathsOptions { ShouldFailOnUnmatchedPath = false }, Comparison);
                var entry = patch.FirstOrDefault();
                if (entry is not null)
                    chunks.Add(entry.IsBinaryComparison ? new(file, "Binary file content omitted.", []) : BoundPatch(file, entry.Patch, MaximumPatchLines));
            }
            catch (Exception error) when (IsSourceError(error))
            {
                chunks.Add(new(file, "Diff unavailable for this file.", [], true));
            }
        }
        if (paths.Length > MaximumFiles && chunks.Count > 0)
            chunks[^1] = chunks[^1] with { Truncated = true };
        return Task.FromResult<IReadOnlyList<DiffChunk>>(chunks);
    }

    private static List<Artifact> ReadMonths(Repository repository, string path, string author,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var buckets = new SortedDictionary<string, MonthBucket>(StringComparer.Ordinal);
        long examined = 0;
        foreach (var commit in Commits(repository, author, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var when = commit.Author.When.ToUniversalTime();
            var month = when.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            if (!buckets.TryGetValue(month, out var bucket)) buckets.Add(month, bucket = new());
            bucket.Count++;
            bucket.First ??= when;
            bucket.Last = when;
            if (bucket.Subjects.Count < 8) bucket.Subjects.Add(Subject(commit.MessageShort));
            AddCount(bucket.Hours, commit.Author.When.Hour);
            try
            {
                using var changes = Changes(repository, commit);
                foreach (var change in changes)
                {
                    bucket.Files.Add(change.Path);
                    AddCount(bucket.Languages, Extension(change.Path));
                }
            }
            catch (Exception error) when (IsSourceError(error))
            {
                progress?.Report(new("git", "commit diff skipped", examined, Error: "A historical commit diff could not be read."));
            }
            progress?.Report(new("git", "reading commits", ++examined));
        }
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var identityPath = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        return buckets.Select(x => new Artifact("git", "GitMonth", identityPath + "|" + x.Key,
            x.Key + " " + name, x.Value.First, x.Value.Last, path,
            new Dictionary<string, string>
            {
                ["commits"] = x.Value.Count.ToString(CultureInfo.InvariantCulture),
                ["filesTouched"] = x.Value.Files.Count.ToString(CultureInfo.InvariantCulture),
                ["languages"] = JsonSerializer.Serialize(x.Value.Languages.OrderByDescending(l => l.Value).ThenBy(l => l.Key, StringComparer.Ordinal).Take(3).ToDictionary()),
                ["messages"] = JsonSerializer.Serialize(x.Value.Subjects),
                ["hours"] = JsonSerializer.Serialize(x.Value.Hours.OrderBy(h => h.Key).ToDictionary())
            })).ToList();
    }

    private static Commit[] Commits(Repository repository, string author, CancellationToken cancellationToken)
    {
        var roots = repository.Refs.Select(x => (object)x).ToList();
        if (repository.Head.Tip is { } tip) roots.Add(tip);
        if (roots.Count == 0) return [];
        var commits = new List<Commit>();
        foreach (var commit in repository.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = roots }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(commit.Author.Email, author, StringComparison.OrdinalIgnoreCase)) commits.Add(commit);
        }
        return commits.OrderBy(x => x.Author.When).ThenBy(x => x.Sha, StringComparer.Ordinal).ToArray();
    }

    private static TreeChanges Changes(Repository repository, Commit commit) =>
        repository.Diff.Compare<TreeChanges>(commit.Parents.FirstOrDefault()?.Tree, commit.Tree, Comparison);

    private static IEnumerable<T> EvenSample<T>(IReadOnlyList<T> values, int maximum)
    {
        if (values.Count <= maximum)
        {
            foreach (var value in values) yield return value;
        }
        else
        {
            for (var i = 0; i < maximum; i++) yield return values[(int)((long)i * (values.Count - 1) / (maximum - 1))];
        }
    }

    private static DiffChunk ReadNewFile(string file, string path, CancellationToken cancellationToken)
    {
        using var input = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var lines = new List<string>();
        while (lines.Count <= MaximumPatchLines && input.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Contains('\0')) return new(file, "Binary file content omitted.", []);
            lines.Add(line);
        }
        var patch = new StringBuilder().Append("--- /dev/null\n+++ b/").Append(file).Append('\n')
            .Append("@@ -0,0 +1,").Append(lines.Count.ToString(CultureInfo.InvariantCulture)).Append(" @@\n");
        foreach (var line in lines) patch.Append('+').Append(line).Append('\n');
        var bounded = BoundPatch(file, patch.ToString(), MaximumPatchLines);
        return bounded with { Truncated = bounded.Truncated || lines.Count > MaximumPatchLines };
    }

    private static DiffChunk BoundPatch(string file, string patch, int lineLimit)
    {
        using var reader = new StringReader(patch);
        var text = new StringBuilder();
        var numbers = new List<int>();
        var current = 0;
        var count = 0;
        var truncated = false;
        while (reader.ReadLine() is { } rawLine)
        {
            if (count >= lineLimit || text.Length + Math.Min(rawLine.Length, 500) + 1 > MaximumPatchCharacters)
            {
                truncated = true;
                break;
            }
            var line = rawLine.Length > 500 ? rawLine[..500] : rawLine;
            if (rawLine.Length > 500) truncated = true;
            var match = HunkHeader.Match(line);
            if (match.Success) current = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            else if (current > 0 && line.Length > 0 && (line[0] == ' ' || line[0] == '+'))
                numbers.Add(current++);
            text.AppendLine(line);
            count++;
        }
        return new(file, text.ToString().TrimEnd('\r', '\n'), numbers, truncated);
    }

    private static bool BlobTooLarge(Repository repository, ObjectId? id) =>
        id is not null && repository.Lookup<Blob>(id)?.Size > MaximumFileBytes;

    private static bool SafeWorkingPath(string root, string fullPath)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        for (var current = fullPath; current.Length >= prefix.Length; current = Path.GetDirectoryName(current)!)
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
        return true;
    }

    private static string? Author(Repository repository, ScanOptions options)
    {
        var author = string.IsNullOrWhiteSpace(options.Author) ? repository.Config.Get<string>("user.email")?.Value : options.Author;
        return string.IsNullOrWhiteSpace(author) ? null : author.Trim();
    }

    private static string RequiredPath(ScanOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Path)) throw new InvalidDataException("Supply a repository path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Path));
    }

    private static List<string> Discover(ScanOptions options)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Path)) return results;
        try
        {
            var root = RequiredPath(options);
            if (!Directory.Exists(root)) return results;
            if (!options.ScanRepositories) return [root];
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.TryDequeue(out var current))
            {
                try
                {
                    if (File.GetAttributes(current.Path).HasFlag(FileAttributes.ReparsePoint)) continue;
                    var dotGit = Path.Combine(current.Path, ".git");
                    if (Directory.Exists(dotGit) || File.Exists(dotGit) ||
                        (File.Exists(Path.Combine(current.Path, "HEAD")) && Directory.Exists(Path.Combine(current.Path, "objects"))))
                        results.Add(current.Path);
                    if (current.Depth >= 2) continue;
                    foreach (var directory in Directory.EnumerateDirectories(current.Path).Order(StringComparer.Ordinal))
                        if (!string.Equals(Path.GetFileName(directory), ".git", StringComparison.OrdinalIgnoreCase))
                            queue.Enqueue((directory, current.Depth + 1));
                }
                catch (Exception error) when (IsSourceError(error)) { }
            }
        }
        catch (Exception error) when (IsSourceError(error)) { }
        return results;
    }

    private static string Subject(string text) => text.Length > 160 ? text[..160] : text;
    private static string Extension(string file) => Path.GetExtension(file).ToLowerInvariant() is { Length: > 0 } extension ? extension : "other";
    private static void AddCount<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull => counts[key] = counts.GetValueOrDefault(key) + 1;
    private static bool IsSourceError(Exception error) => error is LibGit2SharpException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException;

    private sealed class MonthBucket
    {
        public int Count { get; set; }
        public DateTimeOffset? First { get; set; }
        public DateTimeOffset? Last { get; set; }
        public List<string> Subjects { get; } = [];
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Languages { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, int> Hours { get; } = [];
    }
}
