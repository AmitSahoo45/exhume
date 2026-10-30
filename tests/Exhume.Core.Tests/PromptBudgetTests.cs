using Xunit;

namespace Exhume.Core.Tests;

public sealed class PromptBudgetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(30)]
    public void InputWithinBudgetIsUnchanged(int length)
    {
        string input = new('x', length);
        var result = PromptBudget.Apply(input, 30);

        Assert.Equal(input, result.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void FirstCharacterBeyondBudgetTriggersMarkerWithinTheCap()
    {
        var result = PromptBudget.Apply(new string('x', 31), 30);

        Assert.True(result.Truncated);
        Assert.Equal(30, result.Text.Length);
        Assert.StartsWith("xxx", result.Text);
        Assert.EndsWith(PromptBudget.TruncationMarker, result.Text);
    }

    [Fact]
    public void DefaultBudgetIncludesTheTruncationMarker()
    {
        var result = PromptBudget.Apply(new string('x', PromptBudget.DefaultMaxCharacters + 1));

        Assert.True(result.Truncated);
        Assert.True(result.Text.Length <= PromptBudget.DefaultMaxCharacters);
        Assert.EndsWith(PromptBudget.TruncationMarker, result.Text);
    }

    [Fact]
    public void TruncationDoesNotSplitASurrogatePair()
    {
        var result = PromptBudget.Apply("x\U0001F47Byyyyyyyyyyyyyyyyyyyy", PromptBudget.TruncationMarker.Length + 2);

        Assert.True(result.Truncated);
        Assert.Equal("x" + PromptBudget.TruncationMarker, result.Text);
    }

    [Fact]
    public void BudgetTooSmallForMarkerIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PromptBudget.Apply("content", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PromptBudget.Apply("content", 1));
    }
}
