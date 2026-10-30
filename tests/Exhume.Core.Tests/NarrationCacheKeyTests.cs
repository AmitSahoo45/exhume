using Xunit;

namespace Exhume.Core.Tests;

public sealed class NarrationCacheKeyTests
{
    [Fact]
    public void EveryCacheDimensionChangesTheKey()
    {
        string original = NarrationCacheKey.Create("era", "bucket", "v1", "model");

        Assert.Equal(original, NarrationCacheKey.Create("era", "bucket", "v1", "model"));
        Assert.NotEqual(original, NarrationCacheKey.Create("persona", "bucket", "v1", "model"));
        Assert.NotEqual(original, NarrationCacheKey.Create("era", "other", "v1", "model"));
        Assert.NotEqual(original, NarrationCacheKey.Create("era", "bucket", "v2", "model"));
        Assert.NotEqual(original, NarrationCacheKey.Create("era", "bucket", "v1", "other"));
    }

    [Fact]
    public void SeparatorsCannotMakeDifferentComponentsShareAKey()
    {
        Assert.NotEqual(NarrationCacheKey.Create("era|bucket", "x", "v1", "model"),
            NarrationCacheKey.Create("era", "bucket|x", "v1", "model"));
    }
}
