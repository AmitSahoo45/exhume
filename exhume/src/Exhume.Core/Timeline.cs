namespace Exhume.Core;

public readonly record struct MonthKey(int Year, int Month);

public static class Timeline
{
    public static IReadOnlyDictionary<MonthKey, IReadOnlyList<Artifact>> GroupByMonth(
        IEnumerable<Artifact> artifacts, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        return artifacts
            .Where(artifact => artifact.FirstSeen.HasValue &&
                (source is null || string.Equals(artifact.Source, source, StringComparison.Ordinal)))
            .GroupBy(artifact => new MonthKey(artifact.FirstSeen!.Value.Year, artifact.FirstSeen.Value.Month))
            .OrderBy(group => group.Key.Year)
            .ThenBy(group => group.Key.Month)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<Artifact>)Array.AsReadOnly(group.OrderBy(artifact => artifact.FirstSeen)
                    .ThenBy(artifact => artifact.Id, StringComparer.Ordinal).ToArray()));
    }
}
