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
        var path = Path.GetFullPath(settings.Path, workingDirectory ?? Environment.CurrentDirectory);
        var parent = DataDirectoryPolicy.Normalize(Path.GetDirectoryName(path)!);
        path = Path.Combine(parent, Path.GetFileName(path));
        EnsureNewFile(path);
        return WithStoreAsync(settings, async (config, store) =>
        {
            var artifacts = await store.QueryAsync(cancellationToken: cancellationToken);
            var report = new StringBuilder("# Exhume\n\nDig up what your machine remembers.\n\n");
            report.AppendLine(settings.Redact ? "Redacted export: private titles, file samples, evidence paths and generated narration are omitted.\n" : "Local evidence report.\n");
            report.AppendLine("## Timeline\n\n| Month | Source | Artifacts |\n|---|---|---|");
            foreach (var month in Timeline.GroupByMonth(artifacts))
                foreach (var group in month.Value.GroupBy(item => item.Source))
                    report.AppendLine($"| {month.Key.Year:D4}-{month.Key.Month:D2} | {Escape(group.Key)} | {group.Count()} |");
            using var narrator = Narrator(config, store);
            report.AppendLine("\n## Obituaries\n");
            foreach (var artifact in artifacts.Where(item => item.Kind == "DeadProgram").OrderByDescending(item => item.LastSeen))
            {
                AppendArtifact(report, artifact, settings.Redact);
                if (!settings.NoModel && !settings.Redact)
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
                if (!settings.NoModel && !settings.Redact)
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
            await WriteReportAsync(path, report.ToString(), cancellationToken);
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
            foreach (var directoryName in new[] { "temp", "reports" })
            {
                var directory = Path.Combine(config.DataDirectory, directoryName);
                if (!Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null) continue;
                var canonical = DataDirectoryPolicy.Normalize(directory);
                if (!string.Equals(canonical, directory, StringComparison.OrdinalIgnoreCase)) continue;
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
            return string.Join(", ", names.Select(Path.GetExtension).Where(extension => !string.IsNullOrEmpty(extension)).Distinct());
        }
        catch (JsonException) { return "[redacted]"; }
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
