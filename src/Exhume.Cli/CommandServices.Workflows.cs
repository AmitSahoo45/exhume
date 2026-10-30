using System.Globalization;
using Exhume.Adapters.Browser;
using Exhume.Adapters.FileSystem;
using Exhume.Adapters.Git;
using Exhume.Adapters.Windows;
using Exhume.Cli.Commands;
using Exhume.Core;
using Exhume.Narration;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace Exhume.Cli;

public sealed partial class CommandServices
{
    private readonly Lazy<IReadOnlyList<IEvidenceAdapter>> defaultAdapters = new(() =>
        [new FileSystemAdapter(), new BrowserAdapter(), new WindowsAdapter(), new GitAdapter()]);
    private int narrationCalls;
    private IReadOnlyList<IEvidenceAdapter> RegisteredAdapters => adapters ?? defaultAdapters.Value;
    private static readonly string[] Sources = ["folder", "browser", "windows", "git"];

    public async Task<int> RunDigAsync(DigCommand.Settings settings, CancellationToken cancellationToken)
    {
        var requested = settings.Sources.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => value.ToLowerInvariant()).Distinct().ToArray();
        foreach (var source in requested) ValidateSource(source);
        return await WithStoreAsync(settings, async (config, store) =>
        {
            var options = new ScanOptions
            {
                Path = settings.Path, Author = settings.Author, Profile = settings.Profile, Cutoff = settings.Cutoff,
                Elevated = settings.Elevated, ScanRepositories = settings.ScanRepositories, TemporaryDirectory = config.TemporaryDirectory,
                ExcludedPaths = [config.DataDirectory]
            };
            var selectedAdapters = RegisteredAdapters.Where(adapter => requested.Length == 0 || requested.Contains(adapter.Name)).ToArray();
            var preflightErrors = new Dictionary<IEvidenceAdapter, string>();
            foreach (var git in selectedAdapters.OfType<IGitEvidenceReader>())
            {
                try
                {
                    if (git.Check(options).Availability == SourceAvailability.Available)
                        ValidateRepositoryData(config, options.ScanRepositories
                            ? EvidencePathResolver.Normalize(options.Path!)
                            : git.GetPersonaContext(options).RepositoryPath);
                }
                catch (Exception exception) when (IsGitReadError(exception))
                {
                    preflightErrors[git] = $"Git source could not be read ({exception.GetType().Name}).";
                }
            }
            await store.InitializeAsync(cancellationToken);
            var failed = false;
            foreach (var adapter in selectedAdapters)
            {
                var started = DateTimeOffset.UtcNow;
                var committed = 0;
                string? error = null;
                var warnings = 0;
                var storing = false;
                var pending = new List<Artifact>(100);
                if (preflightErrors.TryGetValue(adapter, out var preflightError))
                {
                    await store.RecordScanAsync(new(adapter.Name, started, 0, preflightError), cancellationToken);
                    Write($"{adapter.Name}: {preflightError}");
                    failed = true;
                    continue;
                }
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var availability = adapter.Check(options);
                    if (availability.Availability != SourceAvailability.Available)
                    {
                        Write($"{adapter.Name}: {availability.Availability} - {availability.Reason}");
                        storing = true;
                        await store.RecordScanAsync(new(adapter.Name, started, 0, availability.Reason), cancellationToken);
                        continue;
                    }
                    var progress = new InlineProgress(value =>
                    {
                        if (value.Error is not null) error ??= value.Error;
                        if (value.Warning is not null) warnings++;
                        Write($"{value.Source}: {value.Stage} ({value.ItemsDone})");
                    });
                    await foreach (var artifact in adapter.ScanAsync(options, progress, cancellationToken).WithCancellation(cancellationToken))
                    {
                        if (!string.Equals(artifact.Source, adapter.Name, StringComparison.Ordinal))
                        {
                            error ??= "An artifact had an unexpected source and was skipped.";
                            continue;
                        }
                        pending.Add(artifact);
                        if (pending.Count == 100)
                        {
                            storing = true;
                            await store.UpsertAsync(pending, cancellationToken);
                            committed += pending.Count;
                            storing = false;
                            pending.Clear();
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pending.Count > 0)
                    {
                        storing = true;
                        await store.UpsertAsync(pending, cancellationToken);
                        committed += pending.Count;
                        storing = false;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await store.RecordScanAsync(new(adapter.Name, started, committed, "cancelled", warnings), CancellationToken.None);
                    Write($"{adapter.Name}: scan cancelled; {committed} artifacts already saved.");
                    return 2;
                }
                catch (Exception exception) when (!storing && exception is not SqliteException)
                {
                    error = $"Source failed ({exception.GetType().Name}); pending artifacts were discarded.";
                }
                await store.RecordScanAsync(new(adapter.Name, started, committed, error, warnings), CancellationToken.None);
                failed |= error is not null;
                Write($"{adapter.Name}: saved {committed} artifacts; {warnings} warning(s)" + (error is null ? "." : $"; {error}"));
            }
            return failed ? 2 : 0;
        });
    }

    public Task<int> RunErasAsync(ErasCommand.Settings settings, CancellationToken cancellationToken)
    {
        ValidateSource(settings.Source);
        ValidateYear(settings.Year);
        return WithStoreAsync(settings, async (config, store) =>
        {
            var artifacts = (await store.QueryAsync(Query(settings.Source, settings.Year), cancellationToken))
                .Where(item => item.Kind.EndsWith("Month", StringComparison.Ordinal)).ToArray();
            WarnRemoteNarration(config, !settings.NoModel && artifacts.Any(item => item.FirstSeen.HasValue));
            using var narrator = Narrator(config, store);
            foreach (var group in EraGroups(artifacts))
                await RenderEraAsync(group.ToArray(), settings.NoModel, narrator, cancellationToken);
            foreach (var artifact in artifacts.Where(item => item.FirstSeen is null)) RenderRaw(artifact);
            if (artifacts.Length == 0) Write("No eras found. Run dig for an available source first.");
            ReportCalls(narrator);
            return 0;
        });
    }

    public Task<int> RunTimelineAsync(TimelineCommand.Settings settings, CancellationToken cancellationToken)
    {
        ValidateSource(settings.Source);
        ValidateYear(settings.Year);
        MonthKey? selected = null;
        if (settings.Month is not null)
        {
            if (!DateTime.TryParseExact(settings.Month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
                throw new ArgumentException("--month must use YYYY-MM with a valid month.");
            selected = new(month.Year, month.Month);
        }
        return WithStoreAsync(settings, async (config, store) =>
        {
            var artifacts = await store.QueryAsync(Query(settings.Source, settings.Year), cancellationToken);
            var table = new Table().Border(TableBorder.Simple).AddColumn("Month");
            foreach (var source in Sources) table.AddColumn(source);
            foreach (var group in Timeline.GroupByMonth(artifacts))
            {
                var cells = new[] { $"{group.Key.Year:D4}-{group.Key.Month:D2}" }
                    .Concat(Sources.Select(source => group.Value.Count(item => item.Source == source).ToString(CultureInfo.InvariantCulture)));
                table.AddRow(cells.Select(cell => new Text(cell)).ToArray());
            }
            console.Write(table);
            using var narrator = Narrator(config, store);
            if (selected is { } key)
            {
                WarnRemoteNarration(config, !settings.NoModel);
                foreach (var group in EraGroups(artifacts.Where(item => item.Kind.EndsWith("Month", StringComparison.Ordinal)
                    && item.FirstSeen is { } first && first.Year == key.Year && first.Month == key.Month)))
                    await RenderEraAsync(group.ToArray(), settings.NoModel, narrator, cancellationToken);
            }
            if (artifacts.Count == 0) Write("No timeline evidence found.");
            ReportCalls(narrator);
            return 0;
        });
    }

    public Task<int> RunObituariesAsync(ObituariesCommand.Settings settings, CancellationToken cancellationToken)
    {
        if (settings.Limit is <= 0) throw new ArgumentException("--limit must be positive.");
        return WithStoreAsync(settings, async (config, store) =>
        {
            var artifacts = (await store.QueryAsync(new(Kind: "DeadProgram"), cancellationToken))
                .OrderByDescending(item => item.LastSeen).ThenBy(item => item.Id).Take(settings.Limit ?? 20).ToArray();
            WarnRemoteNarration(config, !settings.NoModel && artifacts.Length > 0);
            using var narrator = Narrator(config, store);
            foreach (var artifact in artifacts)
            {
                var result = settings.NoModel ? null : await narrator.NarrateObituaryAsync(artifact, cancellationToken);
                if (result is NarrationSuccess<Obituary> success)
                {
                    var value = success.Value;
                    console.Write(new Panel(new Text($"{value.Headline}\n{value.Body}\nCause: {value.CauseOfDeath}\n{value.Epitaph}"))
                        .BorderColor(Color.DarkGreen));
                    Write($"{artifact.Name} | last seen {artifact.LastSeen:yyyy-MM-dd} | evidence: {artifact.Evidence}");
                }
                else
                {
                    if (result is NarrationFailure<Obituary>) Write("Narration unavailable; showing raw evidence.");
                    RenderRaw(artifact);
                }
            }
            if (artifacts.Length == 0) Write("No program obituaries found.");
            ReportCalls(narrator);
            return 0;
        });
    }

    private async Task RenderEraAsync(Artifact[] artifacts, bool noModel, INarrationService narrator, CancellationToken cancellationToken)
    {
        var first = artifacts[0];
        Write($"{first.Source} | {first.FirstSeen:yyyy-MM}");
        var result = noModel ? null : await narrator.NarrateEraAsync(artifacts, cancellationToken);
        if (result is NarrationSuccess<EraSummary> success)
        {
            console.Write(new Panel(new Text($"{success.Value.Title}\n{success.Value.Body}\nThemes: {string.Join(", ", success.Value.Themes)}"))
                .BorderColor(Color.DarkGreen));
        }
        else
        {
            if (result is NarrationFailure<EraSummary>) Write("Narration unavailable; showing raw evidence.");
            foreach (var artifact in artifacts) RenderRaw(artifact);
        }
    }

    private void RenderRaw(Artifact artifact)
    {
        Write($"{artifact.Name} | {artifact.Source} | {artifact.FirstSeen:yyyy-MM-dd} - {artifact.LastSeen:yyyy-MM-dd}");
        foreach (var pair in artifact.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Write($"  {pair.Key}: {pair.Value}");
        Write($"  evidence: {artifact.Evidence}");
    }

    private NarrationService Narrator(ExhumeConfig config, IArtifactStore store, string cacheNamespace = "") =>
        new(config, store, Path.Combine(AppContext.BaseDirectory, "prompts"), httpHandler, cacheNamespace: cacheNamespace);

    private void WarnRemoteNarration(ExhumeConfig config, bool enabled)
    {
        if (enabled && !config.OllamaUrl.IsLoopback)
            Write("Warning: the configured model endpoint is outside this machine; narration may send evidence to that endpoint.");
    }

    private static void ValidateRepositoryData(ExhumeConfig config, string repositoryPath)
    {
        var repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var prefix = Path.EndsInDirectorySeparator(repository) ? repository : repository + Path.DirectorySeparatorChar;
        if (string.Equals(repository, config.DataDirectory, StringComparison.OrdinalIgnoreCase) ||
            config.DataDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Application data must be outside the repository working tree. Set EXHUME_HOME or --home to a separate directory.");
    }

    private static bool IsGitReadError(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or LibGit2Sharp.LibGit2SharpException;

    private void ReportCalls(INarrationService narrator)
    {
        narrationCalls += narrator.CallCount;
        Write($"Narration calls: {narrator.CallCount}");
    }

    private async Task<int> WithStoreAsync(SharedSettings settings, Func<ExhumeConfig, IArtifactStore, Task<int>> run)
    {
        ExhumeConfig? config = null;
        try
        {
            config = ConfigurationLoader.Load(settings.ToOverrides(), environment, configFilePath, workingDirectory).Config;
            return await run(config, storeFactory?.Invoke(config) ?? new SqliteArtifactStore(config.DataDirectory));
        }
        catch (OperationCanceledException)
        {
            Write("Operation cancelled.");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            WriteStorageError(exception is DataDirectoryAccessException pathError ? pathError.RequestedPath : config?.DataDirectory ?? settings.Home ?? "configured data directory", exception.GetType().Name);
            return 3;
        }
    }

    private static void ValidateSource(string? source)
    {
        if (source is not null && !Sources.Contains(source, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown source '{source}'. Choose folder, browser, windows or git.");
    }

    private static void ValidateYear(int? year)
    {
        if (year is < 1 or > 9999) throw new ArgumentException("--year must be between 1 and 9999.");
    }

    private static ArtifactQuery Query(string? source, int? year) => new(source, From: year is { } y ? new DateTimeOffset(y, 1, 1, 0, 0, 0, TimeSpan.Zero) : null,
        To: year is { } end ? (end == 9999 ? DateTimeOffset.MaxValue : new DateTimeOffset(end + 1, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(-1)) : null);

    private static IEnumerable<IGrouping<(string Source, int Year, int Month), Artifact>> EraGroups(IEnumerable<Artifact> artifacts) =>
        artifacts.Where(item => item.FirstSeen.HasValue)
            .GroupBy(item => (item.Source, item.FirstSeen!.Value.Year, item.FirstSeen.Value.Month))
            .OrderBy(group => group.Key.Year).ThenBy(group => group.Key.Month).ThenBy(group => group.Key.Source);

    private sealed class InlineProgress(Action<ScanProgress> action) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => action(value);
    }
}
