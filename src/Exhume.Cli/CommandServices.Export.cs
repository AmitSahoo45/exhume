using System.Text;
using System.Text.Json;
using Exhume.Cli.Commands;
using Exhume.Core;
using Exhume.Narration;
using Spectre.Console;

namespace Exhume.Cli;

public sealed partial class CommandServices
{
    public Task<int> RunExportAsync(ExportCommand.Settings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Path)) throw new ArgumentException("export requires --path.");
        return WithStoreAsync(settings, async (config, store) =>
        {
            string path;
            try
            {
                path = Path.GetFullPath(settings.Path, workingDirectory ?? Environment.CurrentDirectory);
                var parent = DataDirectoryPolicy.Normalize(Path.GetDirectoryName(path)!);
                path = Path.Combine(parent, Path.GetFileName(path));
                EnsureNewFile(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Write($"Export destination is unavailable: {settings.Path}. Choose a writable report path.");
                return 3;
            }
            var original = await store.QueryAsync(cancellationToken: cancellationToken);
            IReadOnlyList<Artifact> artifacts = settings.Redact ? original.Select(RedactArtifact).ToArray() : original;
            var report = new StringBuilder("# Exhume\n\nDig up what your machine remembers.\n\n");
            report.AppendLine(settings.Redact ? "Redacted export: private titles and paths are omitted, file samples retain only extensions, and narration uses sanitized evidence.\n" : "Local evidence report.\n");
            report.AppendLine("## Timeline\n\n| Month | Source | Artifacts |\n|---|---|---|");
            foreach (var month in Timeline.GroupByMonth(artifacts))
                foreach (var group in month.Value.GroupBy(item => item.Source))
                    report.AppendLine($"| {month.Key.Year:D4}-{month.Key.Month:D2} | {Escape(group.Key)} | {group.Count()} |");
            WarnRemoteNarration(config, !settings.NoModel && artifacts.Count > 0);
            using var narrator = Narrator(config, store, settings.Redact ? "redacted-v1" : "");
            report.AppendLine("\n## Obituaries\n");
            foreach (var artifact in artifacts.Where(item => item.Kind == "DeadProgram").OrderByDescending(item => item.LastSeen))
            {
                AppendArtifact(report, artifact, settings.Redact);
                if (!settings.NoModel)
                {
                    if (await narrator.NarrateObituaryAsync(artifact, cancellationToken) is NarrationSuccess<Obituary> obituary)
                        report.AppendLine($"{Escape(obituary.Value.Headline)}\n\n{Escape(obituary.Value.Body)}\n\n{Escape(obituary.Value.CauseOfDeath)}\n\n{Escape(obituary.Value.Epitaph)}\n");
                    else AppendFallback();
                }
            }
            report.AppendLine("\n## Eras\n");
            foreach (var group in EraGroups(artifacts.Where(item => item.Kind.EndsWith("Month", StringComparison.Ordinal))))
            {
                report.AppendLine($"### {group.Key.Year:D4}-{group.Key.Month:D2} · {Escape(group.Key.Source)}\n");
                foreach (var artifact in group) AppendArtifact(report, artifact, settings.Redact);
                if (!settings.NoModel)
                {
                    if (await narrator.NarrateEraAsync(group.ToArray(), cancellationToken) is NarrationSuccess<EraSummary> era)
                        report.AppendLine($"{Escape(era.Value.Title)}\n\n{Escape(era.Value.Body)}\n\nThemes: {Escape(string.Join(", ", era.Value.Themes))}\n");
                    else AppendFallback();
                }
            }
            var undated = artifacts.Where(item => item.Kind.EndsWith("Month", StringComparison.Ordinal) && item.FirstSeen is null).ToArray();
            if (undated.Length > 0)
            {
                report.AppendLine("\n## Undated evidence\n");
                foreach (var artifact in undated) AppendArtifact(report, artifact, settings.Redact);
            }
            try { await WriteReportAsync(path, report.ToString(), cancellationToken); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Write($"Export destination is not writable: {path}. Choose a writable report path.");
                return 3;
            }
            Write($"Exported report: {path}");
            ReportCalls(narrator);
            return 0;

            void AppendFallback()
            {
                const string warning = "Narration unavailable; showing raw evidence.";
                Write(warning);
                report.AppendLine(warning).AppendLine();
            }
        });
    }

    public Task<int> RunForgetAsync(ForgetCommand.Settings settings, CancellationToken cancellationToken) =>
        WithStoreAsync(settings, async (config, store) =>
        {
            var confirmed = settings.Yes;
            if (!confirmed && console.Profile.Capabilities.Interactive)
                confirmed = console.Confirm("Permanently forget stored evidence and narration?", false);
            if (!confirmed)
            {
                Write("Nothing deleted. Use forget --yes to explicitly confirm in a noninteractive session.");
                return 0;
            }
            cancellationToken.ThrowIfCancellationRequested();
            await store.ForgetAsync(cancellationToken);
            foreach (var directoryName in new[] { "temp", "reports", "logs" })
            {
                var directory = Path.Combine(config.DataDirectory, directoryName);
                if (!Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null) continue;
                var canonical = DataDirectoryPolicy.Normalize(directory);
                if (!string.Equals(canonical, directory, StringComparison.OrdinalIgnoreCase)) continue;
                if (directoryName == "logs")
                {
                    var log = Path.Combine(directory, "narration.jsonl");
                    if (File.Exists(log) && new FileInfo(log).LinkTarget is null) File.Delete(log);
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                    continue;
                }
                foreach (var file in Directory.EnumerateFiles(directory, "exhume-*.md", SearchOption.TopDirectoryOnly))
                    if (new FileInfo(file).LinkTarget is null) File.Delete(file);
                foreach (var file in Directory.EnumerateFiles(directory, ".exhume-write-*.tmp", SearchOption.TopDirectoryOnly))
                    if (new FileInfo(file).LinkTarget is null && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"\A\.exhume-write-[0-9a-f]{32}\.tmp\z"))
                        File.Delete(file);
                if (directoryName == "temp") CleanHistorySnapshots(directory);
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            if (Directory.Exists(config.DataDirectory) && !Directory.EnumerateFileSystemEntries(config.DataDirectory).Any())
                Directory.Delete(config.DataDirectory);
            Write(Directory.Exists(config.DataDirectory)
                ? "Stored evidence removed; unrelated files were preserved."
                : "Stored evidence and narration forgotten; data directory removed.");
            return 0;
        });

    private static void AppendArtifact(StringBuilder report, Artifact artifact, bool redact)
    {
        var name = redact && artifact.Source == "git" ? $"{artifact.FirstSeen:yyyy-MM} repository activity" : artifact.Name;
        report.AppendLine($"#### {Escape(name)}\n");
        report.AppendLine($"Source: {Escape(artifact.Source)}; first: {artifact.FirstSeen:yyyy-MM-dd}; last: {artifact.LastSeen:yyyy-MM-dd}\n");
        foreach (var pair in artifact.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var value = pair.Value;
            if (redact)
            {
                if (pair.Key is "titles" or "exePath" or "messages" or "installedSiblings") value = "[redacted]";
                else if (pair.Key == "samples") value = RedactSamples(value);
            }
            report.AppendLine($"- {Escape(pair.Key)}: {Escape(value)}");
        }
        if (!redact) report.AppendLine($"\nEvidence: {Escape(artifact.Evidence)}");
        report.AppendLine();
    }

    private static string RedactSamples(string value)
    {
        try
        {
            var names = value.TrimStart().StartsWith('[') ? JsonSerializer.Deserialize<string[]>(value) ?? []
                : value.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join(", ", names.Select(Path.GetExtension).Where(extension => extension is not null &&
                System.Text.RegularExpressions.Regex.IsMatch(extension, @"\A\.[A-Za-z0-9]{1,10}\z")).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException) { return "[redacted]"; }
    }

    private static Artifact RedactArtifact(Artifact original)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in original.Metadata)
        {
            if (pair.Key == "samples")
                metadata[pair.Key] = JsonSerializer.Serialize(RedactSamples(pair.Value).Split(", ", StringSplitOptions.RemoveEmptyEntries));
            else if (pair.Key is "count" or "bytes" or "visits" or "commits" or "filesTouched" or "runCount")
            {
                if (long.TryParse(pair.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) && count >= 0)
                    metadata[pair.Key] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (pair.Key == "confidence" && pair.Value is "high" or "low") metadata[pair.Key] = pair.Value;
            else if (pair.Key == "dateSource" && pair.Value is "exif" or "mtime" or "ctime") metadata[pair.Key] = pair.Value;
            else if (pair.Key == "browser" && pair.Value is "chrome" or "edge" or "brave") metadata[pair.Key] = pair.Value;
            else if (pair.Key is "titles" or "exePath" or "messages" or "installedSiblings" or "profile") metadata[pair.Key] = "[redacted]";
        }
        var name = original.Source == "git" ? $"{original.FirstSeen:yyyy-MM} repository activity" : original.Name;
        return new(original.Source, original.Kind, original.Id, name, original.FirstSeen, original.LastSeen, "", metadata);
    }

    private static void CleanHistorySnapshots(string temporaryRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(temporaryRoot, "history-*", SearchOption.TopDirectoryOnly))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(directory), @"\Ahistory-[0-9a-f]{32}\z") ||
                new DirectoryInfo(directory).LinkTarget is not null ||
                !string.Equals(DataDirectoryPolicy.Normalize(directory), directory, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var name in new[] { "History", "History-wal", "History-shm", "History-journal" })
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path) && new FileInfo(path).LinkTarget is null) File.Delete(path);
            }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\u0060", "\\\u0060", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
