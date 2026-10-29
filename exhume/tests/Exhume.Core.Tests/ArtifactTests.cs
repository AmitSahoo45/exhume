using Xunit;

namespace Exhume.Core.Tests;

public sealed class ArtifactTests
{
    [Fact]
    public void IdentityDependsOnlyOnSourceKindAndKey()
    {
        var first = new Artifact("folder", "FolderMonth", "root|2024-01|photo", "first", null, null, "evidence");
        var updated = new Artifact("folder", "FolderMonth", "root|2024-01|photo", "updated", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, "new evidence", new Dictionary<string, string> { ["count"] = "5" });

        Assert.Equal(first.Id, updated.Id);
        Assert.Matches("^[0-9a-f]{64}$", first.Id);
    }

    [Theory]
    [InlineData("folder", "OtherKind", "key")]
    [InlineData("git", "FolderMonth", "key")]
    [InlineData("folder", "FolderMonth", "other")]
    public void IdentitySeparatesSourceKindAndKey(string source, string kind, string key)
    {
        var original = new Artifact("folder", "FolderMonth", "key", "name", null, null, "evidence");
        var other = new Artifact(source, kind, key, "name", null, null, "evidence");

        Assert.NotEqual(original.Id, other.Id);
    }

    [Fact]
    public void IdentityCannotConfuseSeparatorsInsideComponents()
    {
        var first = new Artifact("a|b", "c", "d", "name", null, null, "evidence");
        var second = new Artifact("a", "b|c", "d", "name", null, null, "evidence");
        var nulFirst = new Artifact("a\0b", "c", "d", "name", null, null, "evidence");
        var nulSecond = new Artifact("a", "b\0c", "d", "name", null, null, "evidence");

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(nulFirst.Id, nulSecond.Id);
    }

    [Fact]
    public void MetadataIsCopiedAndCannotBeMutatedThroughItsPublicView()
    {
        var metadata = new Dictionary<string, string> { ["count"] = "2" };
        var artifact = new Artifact("folder", "FolderMonth", "key", "name", null, null, "evidence", metadata);
        metadata["count"] = "9";
        metadata["new"] = "value";

        Assert.Equal("2", artifact.Metadata["count"]);
        Assert.Single(artifact.Metadata);
        if (artifact.Metadata is IDictionary<string, string> mutableView)
        {
            Assert.Throws<NotSupportedException>(() => mutableView["count"] = "3");
        }
    }

    [Fact]
    public void TimestampsAreNormalizedToUtcWithoutChangingTheInstant()
    {
        var date = new DateTimeOffset(2024, 3, 1, 0, 30, 0, TimeSpan.FromHours(2));
        var artifact = new Artifact("folder", "FolderMonth", "key", "name", date, date, "evidence");

        Assert.Equal(new DateTimeOffset(2024, 2, 29, 22, 30, 0, TimeSpan.Zero), artifact.FirstSeen);
        Assert.Equal(TimeSpan.Zero, artifact.FirstSeen!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, artifact.LastSeen!.Value.Offset);
    }
}
