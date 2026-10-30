using System.Text.Json;

namespace Exhume.Narration;

internal static class OutputValidation
{
    internal static Obituary? Obituary(JsonElement root)
    {
        if (!Fields(root, "headline", "body", "causeOfDeath", "epitaph")) return null;
        string? headline = Text(root.GetProperty("headline"), 160);
        string? body = Text(root.GetProperty("body"), 2000, 120);
        string? cause = Text(root.GetProperty("causeOfDeath"), 240);
        string? epitaph = Text(root.GetProperty("epitaph"), 240);
        return headline is null || body is null || cause is null || epitaph is null ? null : new(headline, body, cause, epitaph);
    }

    internal static EraSummary? Era(JsonElement root)
    {
        if (!Fields(root, "title", "body", "themes")) return null;
        string? title = Text(root.GetProperty("title"), 160);
        string? body = Text(root.GetProperty("body"), 2400, 150);
        var themes = Strings(root.GetProperty("themes"), 8, 80);
        return title is null || body is null || themes is null ? null : new(title, body, themes);
    }

    internal static Persona? Persona(JsonElement root)
    {
        if (!Fields(root, "voiceDescription", "habits", "petPhrases", "thingsToNitpick", "technicalWorldview")) return null;
        string? voice = Text(root.GetProperty("voiceDescription"), 600, 100);
        string? worldview = Text(root.GetProperty("technicalWorldview"), 1200, 180);
        var habits = Strings(root.GetProperty("habits"), 8, 240);
        var phrases = Strings(root.GetProperty("petPhrases"), 8, 240);
        var nitpicks = Strings(root.GetProperty("thingsToNitpick"), 8, 240);
        return voice is null || worldview is null || habits is null || phrases is null || nitpicks is null
            ? null : new(voice, habits, phrases, nitpicks, worldview);
    }

    internal static IReadOnlyList<HauntComment>? Haunt(JsonElement root, string file, HashSet<int> visibleLines)
    {
        if (root.ValueKind != JsonValueKind.Array) return null;
        var comments = new List<HauntComment>();
        foreach (var item in root.EnumerateArray())
        {
            if (!Fields(item, "file", "lineNumber", "comment") ||
                item.GetProperty("file").ValueKind != JsonValueKind.String ||
                item.GetProperty("file").GetString() != file ||
                !item.GetProperty("lineNumber").TryGetInt32Safely(out int line) || !visibleLines.Contains(line)) continue;
            string? comment = Text(item.GetProperty("comment"), 500, 80);
            if (comment is null || SentenceCount(comment) > 2) continue;
            comments.Add(new HauntComment(file, line, comment));
            if (comments.Count == 5) break;
        }
        return comments;
    }

    private static bool Fields(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var expected = names.ToHashSet(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!expected.Remove(property.Name)) return false;
        return expected.Count == 0;
    }

    private static string? Text(JsonElement element, int characters, int words = int.MaxValue)
    {
        if (element.ValueKind != JsonValueKind.String) return null;
        string value = element.GetString()!.Trim();
        if (value.Length == 0 || value.Length > characters || value.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t')) return null;
        return WordCount(value) <= words ? value : null;
    }

    private static IReadOnlyList<string>? Strings(JsonElement element, int items, int characters)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > items) return null;
        var values = new List<string>();
        foreach (var child in element.EnumerateArray())
        {
            string? value = Text(child, characters);
            if (value is null) return null;
            values.Add(value);
        }
        return values;
    }

    private static int WordCount(string value)
    {
        int words = 0;
        bool previousWhitespace = true;
        foreach (char character in value)
        {
            bool whitespace = char.IsWhiteSpace(character);
            if (previousWhitespace && !whitespace) words++;
            previousWhitespace = whitespace;
        }
        return words;
    }

    private static int SentenceCount(string value)
    {
        int count = 0;
        bool textSinceBoundary = false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character is '.' or '!' or '?')
            {
                bool boundary = index + 1 == value.Length || char.IsWhiteSpace(value[index + 1]);
                if (boundary && textSinceBoundary) { count++; textSinceBoundary = false; }
            }
            else if (!char.IsWhiteSpace(character)) textSinceBoundary = true;
        }
        return count + (textSinceBoundary ? 1 : 0);
    }

    private static bool TryGetInt32Safely(this JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }
}
