using System.Collections.Frozen;

namespace Exhume.Core;

public sealed record Artifact
{
    public Artifact(string source, string kind, string key, string name,
        DateTimeOffset? firstSeen, DateTimeOffset? lastSeen, string evidence,
        IReadOnlyDictionary<string, string>? metadata = null)
        : this(IdentityHash.Create(source, kind, key), source, kind, name, evidence, firstSeen, lastSeen, metadata)
    {
    }

    private Artifact(string id, string source, string kind, string name, string evidence,
        DateTimeOffset? firstSeen, DateTimeOffset? lastSeen, IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(evidence);

        Id = id;
        Source = source;
        Kind = kind;
        Name = name;
        FirstSeen = firstSeen?.ToUniversalTime();
        LastSeen = lastSeen?.ToUniversalTime();
        Evidence = evidence;
        Metadata = metadata is null
            ? FrozenDictionary<string, string>.Empty
            : metadata.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal static Artifact Restore(string id, string source, string kind, string name,
        DateTimeOffset? firstSeen, DateTimeOffset? lastSeen, string evidence,
        IReadOnlyDictionary<string, string> metadata) =>
        new(id, source, kind, name, evidence, firstSeen, lastSeen, metadata);

    public string Id { get; }
    public string Source { get; }
    public string Kind { get; }
    public string Name { get; }
    public DateTimeOffset? FirstSeen { get; }
    public DateTimeOffset? LastSeen { get; }
    public string Evidence { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }
}
