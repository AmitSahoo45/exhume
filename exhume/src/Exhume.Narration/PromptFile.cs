using System.Globalization;

namespace Exhume.Narration;

internal sealed record PromptFile(string Version, string Instructions)
{
    internal static async Task<PromptFile?> LoadAsync(string directory, string kind, CancellationToken cancellationToken)
    {
        try
        {
            string path = Path.Combine(directory, kind + ".txt");
            if (new FileInfo(path).Length > 16384) return null;
            string text = (await File.ReadAllTextAsync(path, cancellationToken)).Replace("\r\n", "\n", StringComparison.Ordinal);
            string[] lines = text.Split('\n', 4);
            if (lines.Length != 4 || lines[0] != "---" || lines[2] != "---" ||
                !lines[1].StartsWith("version: ", StringComparison.Ordinal)) return null;
            string version = lines[1][9..];
            if (!int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1 ||
                string.IsNullOrWhiteSpace(lines[3]) || lines[3].Length > 4000) return null;
            return new PromptFile(version, lines[3]);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
