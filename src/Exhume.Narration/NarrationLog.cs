using System.Text;
using System.Text.Json;
using Exhume.Core;

namespace Exhume.Narration;

internal sealed class NarrationLog(string dataDirectory) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    internal async Task WriteAsync(string kind, int attempt, int inputCharacters, long durationMilliseconds, string outcome)
    {
        await gate.WaitAsync();
        try
        {
            string root = DataDirectoryPolicy.Normalize(dataDirectory);
            if (!string.Equals(root, dataDirectory, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
            string directory = Path.Combine(root, "logs");
            string path = Path.Combine(directory, "narration.jsonl");
            if (new DirectoryInfo(directory).LinkTarget is not null || new FileInfo(path).LinkTarget is not null) return;
            if (!string.Equals(DataDirectoryPolicy.Normalize(directory), directory, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(directory);
            string entry = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                kind,
                attempt,
                inputCharacters,
                durationMilliseconds,
                outcome
            });
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(entry + "\n"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unavailable diagnostics file must not prevent raw evidence or narration from being shown.
        }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();
}
