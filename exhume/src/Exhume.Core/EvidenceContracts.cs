namespace Exhume.Core;

public enum SourceAvailability { Available, Unavailable, NeedsAdmin }

public sealed record SourceStatus(SourceAvailability Availability, string Reason);

public sealed record ScanOptions
{
    public string? Path { get; init; }
    public string? Author { get; init; }
    public string? Profile { get; init; }
    public DateTimeOffset? Cutoff { get; init; }
    public bool Elevated { get; init; }
    public bool ScanRepositories { get; init; }
    public string? TemporaryDirectory { get; init; }
}

public sealed record ScanProgress(string Source, string Stage, long ItemsDone, long? Total = null);

public interface IEvidenceAdapter
{
    string Name { get; }
    SourceStatus Check(ScanOptions options);
    IAsyncEnumerable<Artifact> ScanAsync(ScanOptions options,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record DiffChunk(string File, string Text, IReadOnlyList<int> LineNumbers, bool Truncated = false);

public sealed record PersonaInput(string RepositoryPath, string Author, DateTimeOffset Cutoff,
    int TotalCommitCount, IReadOnlyList<string> CommitSubjects, IReadOnlyList<DiffChunk> DiffSamples,
    IReadOnlyDictionary<string, int> Languages, IReadOnlyDictionary<int, int> Hours);
