namespace Exhume.Core;

public sealed record ArtifactQuery(string? Source = null, string? Kind = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null);

public sealed record CachedNarration(string CacheKey, string Json, string Model,
    string PromptVersion, DateTimeOffset CreatedAt);

public sealed record ScanRecord(string Source, DateTimeOffset StartedAt, int ArtifactCount, string? Error = null);

public sealed record StoreCounts(long Artifacts, long Narrations, long Scans);

public interface IArtifactStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(IEnumerable<Artifact> artifacts, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Artifact>> QueryAsync(ArtifactQuery? query = null, CancellationToken cancellationToken = default);
    Task<CachedNarration?> GetNarrationAsync(string key, CancellationToken cancellationToken = default);
    Task SaveNarrationAsync(CachedNarration narration, CancellationToken cancellationToken = default);
    Task RecordScanAsync(ScanRecord scan, CancellationToken cancellationToken = default);
    Task<StoreCounts> GetCountsAsync(CancellationToken cancellationToken = default);
    Task ForgetAsync(CancellationToken cancellationToken = default);
}

public static class NarrationCacheKey
{
    public static string Create(string kind, string identity, string promptVersion, string model) =>
        IdentityHash.Create(kind, identity, promptVersion, model);
}
