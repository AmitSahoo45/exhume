using Xunit;

namespace Exhume.Core.Tests;

public sealed class TimelineTests
{
    [Fact]
    public void GroupsByFirstSeenUtcMonthAndExcludesUndatedArtifacts()
    {
        Artifact[] artifacts =
        [
            Make("folder", "feb", new DateTimeOffset(2024, 3, 1, 0, 30, 0, TimeSpan.FromHours(2))),
            Make("folder", "march", new DateTimeOffset(2024, 3, 2, 0, 0, 0, TimeSpan.Zero)),
            Make("git", "other", new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero)),
            Make("folder", "undated", null)
        ];

        var groups = Timeline.GroupByMonth(artifacts, "folder");

        Assert.Equal(2, groups.Count);
        Assert.Equal("feb", Assert.Single(groups[new MonthKey(2024, 2)]).Name);
        Assert.Equal("march", Assert.Single(groups[new MonthKey(2024, 3)]).Name);
    }

    [Fact]
    public void MissingSourceFilterIncludesAllSourcesInTheSameMonth()
    {
        var date = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var groups = Timeline.GroupByMonth([Make("git", "commits", date), Make("folder", "photos", date)]);

        Assert.Equal(2, Assert.Single(groups).Value.Count);
    }

    private static Artifact Make(string source, string key, DateTimeOffset? date) =>
        new(source, "kind", key, key, date, DateTimeOffset.UtcNow, "evidence");
}
