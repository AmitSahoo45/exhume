using System.Text;
using Exhume.Adapters.Git;
using Exhume.Cli.Commands;
using Exhume.Core;
using Exhume.Narration;

namespace Exhume.Cli;

public sealed partial class CommandServices
{
    public Task<int> RunHauntAsync(HauntCommand.Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Report is { } name && (name.Length > 120 ||
            !System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]*\.md\z")))
            throw new ArgumentException("--report must be a Markdown file name such as review.md, without a directory.");
        return WithStoreAsync(settings, async (config, store) =>
        {
            var reader = RegisteredAdapters.OfType<IGitEvidenceReader>().FirstOrDefault();
            if (reader is null) { Write("Git evidence reader is unavailable."); return 0; }
            var options = new ScanOptions
            {
                Path = settings.Path ?? workingDirectory ?? Environment.CurrentDirectory,
                Author = settings.Author, Cutoff = settings.Cutoff, TemporaryDirectory = config.TemporaryDirectory
            };
            var status = reader.Check(options);
            if (status.Availability != SourceAvailability.Available)
            {
                Write($"git: {status.Availability} - {status.Reason}");
                return 0;
            }
            string? reportPath = settings.Report is null ? null : Path.Combine(
                ValidateReportDirectory(Path.Combine(config.DataDirectory, "reports")), "exhume-" + settings.Report);
            if (reportPath is not null) EnsureNewFile(reportPath);
            IReadOnlyList<DiffChunk> chunks;
            PersonaInput input;
            try
            {
                chunks = await reader.ReadDiffAsync(options, cancellationToken);
                if (chunks.Count == 0) { Write("Nothing to haunt: the working tree is clean."); return 0; }
                input = await reader.BuildPersonaInputAsync(options, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LibGit2Sharp.LibGit2SharpException)
            {
                Write($"git: could not read repository evidence ({exception.GetType().Name}).");
                return 2;
            }

            var report = new StringBuilder("# Exhume haunt\n\n");
            void Emit(string line) { Write(line); report.AppendLine(Escape(line)).AppendLine(); }
            using var narrator = Narrator(config, store);
            var personaResult = settings.NoModel ? null : await narrator.BuildPersonaAsync(input, cancellationToken);
            if (personaResult is NarrationSuccess<Persona> persona)
            {
                Emit($"Past self: {persona.Value.VoiceDescription}");
                foreach (var chunk in chunks)
                {
                    if (chunk.Truncated) Emit($"{chunk.File}: diff truncated; review covers only the displayed excerpt.");
                    var result = await narrator.HauntAsync(persona.Value, chunk, cancellationToken);
                    if (result is NarrationSuccess<IReadOnlyList<HauntComment>> comments)
                    {
                        if (comments.Value.Count == 0) Emit($"{chunk.File}: nothing to say.");
                        foreach (var comment in comments.Value) Emit($"{comment.File}:{comment.LineNumber}: {comment.Comment}");
                    }
                    else
                    {
                        Emit("Narration unavailable; showing raw diff.");
                        RenderChunk(chunk);
                    }
                }
            }
            else
            {
                if (!settings.NoModel) Emit("Narration unavailable; showing raw evidence.");
                Emit($"Author: {input.Author}; before: {input.Cutoff:yyyy-MM-dd}; commits: {input.TotalCommitCount}");
                foreach (var subject in input.CommitSubjects) Emit(subject);
                foreach (var chunk in chunks) RenderChunk(chunk);
            }
            if (reportPath is not null)
            {
                await WriteReportAsync(reportPath, report.ToString(), cancellationToken);
                Write($"Saved haunt report: {reportPath}");
            }
            ReportCalls(narrator);
            return 0;

            void RenderChunk(DiffChunk chunk)
            {
                Emit(chunk.File + (chunk.Truncated ? " (truncated)" : ""));
                foreach (var line in chunk.Text.Split('\n')) Emit(line.TrimEnd('\r'));
            }
        });
    }

    private static void EnsureNewFile(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new ArgumentException("Report destination already exists; choose a new path.");
    }

    private static async Task WriteReportAsync(string path, string text, CancellationToken cancellationToken)
    {
        var parent = ValidateReportDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".exhume-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateReportDirectory(parent);
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ValidateReportDirectory(string expected)
    {
        var normalized = DataDirectoryPolicy.Normalize(expected);
        if (new DirectoryInfo(expected).LinkTarget is not null ||
            !string.Equals(expected, normalized, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The report directory must not redirect through a filesystem link.");
        return normalized;
    }
}
