namespace Exhume.Core;

public sealed record BudgetedPrompt(string Text, bool Truncated);

public static class PromptBudget
{
    // Reserve space for the fixed instructions and response within a modest model context.
    public const int DefaultMaxCharacters = 12000;
    public const string TruncationMarker = "[truncated]";

    public static BudgetedPrompt Apply(string input, int maxCharacters = DefaultMaxCharacters)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, TruncationMarker.Length);

        if (input.Length <= maxCharacters)
        {
            return new BudgetedPrompt(input, false);
        }

        int prefixLength = maxCharacters - TruncationMarker.Length;
        if (prefixLength > 0 && char.IsHighSurrogate(input[prefixLength - 1]) &&
            char.IsLowSurrogate(input[prefixLength]))
        {
            prefixLength--;
        }

        return new BudgetedPrompt(string.Concat(input.AsSpan(0, prefixLength), TruncationMarker), true);
    }
}
