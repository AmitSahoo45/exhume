using Exhume.Core;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace Exhume.Cli;

public sealed partial class CommandServices(
    IAnsiConsole console,
    HttpMessageHandler? httpHandler,
    Func<ExhumeConfig, IArtifactStore>? storeFactory,
    IReadOnlyList<IEvidenceAdapter>? adapters,
    IReadOnlyDictionary<string, string?>? environment,
    string? workingDirectory,
    string? configFilePath)
{
    public async Task<int> RunDoctorAsync(SharedSettings settings, CancellationToken cancellationToken)
    {
        ConfigurationResult configuration;
        try
        {
            configuration = ConfigurationLoader.Load(settings.ToOverrides(), environment, configFilePath, workingDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteStorageError(exception is DataDirectoryAccessException pathError ? pathError.RequestedPath : settings.Home ?? "configured data directory", exception.Message);
            return 3;
        }

        var config = configuration.Config;
        try
        {
            var store = storeFactory?.Invoke(config) ?? new SqliteArtifactStore(config.DataDirectory);
            await store.InitializeAsync(cancellationToken);
            var counts = await store.GetCountsAsync(cancellationToken);
            using var client = httpHandler is null
                ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
                : new HttpClient(httpHandler, disposeHandler: false);
            client.Timeout = Timeout.InfiniteTimeSpan;
            var model = await new DoctorService(client).CheckModelAsync(config, cancellationToken);
            var sources = DoctorService.CheckSources(RegisteredAdapters, new ScanOptions { TemporaryDirectory = config.TemporaryDirectory });
            Render(configuration, counts, model, sources);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            WriteStorageError(config.DataDirectory, exception.Message);
            return 3;
        }
    }

    private void Render(ConfigurationResult result, StoreCounts counts, ModelStatus model,
        IReadOnlyDictionary<string, SourceStatus> sources)
    {
        console.Write(new Panel(new Text("Exhume\nDig up what your machine remembers.", new Style(Color.Grey)))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.DarkGreen),
            Width = 100,
        });
        var config = result.Config;
        WriteConfiguration("Ollama URL", config.OllamaUrl.AbsoluteUri, nameof(ExhumeConfig.OllamaUrl), result);
        WriteConfiguration("Model", config.ModelTag, nameof(ExhumeConfig.ModelTag), result);
        WriteConfiguration("Data directory", config.DataDirectory, nameof(ExhumeConfig.DataDirectory), result);
        WriteConfiguration("Request timeout", $"{config.RequestTimeout.TotalSeconds:g} seconds", nameof(ExhumeConfig.RequestTimeout), result);
        WriteConfiguration("Maximum attempts", config.MaximumAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture), nameof(ExhumeConfig.MaximumAttempts), result);
        Write($"Artifacts: {counts.Artifacts}");
        Write($"Narrations: {counts.Narrations}");
        Write($"Scans: {counts.Scans}");
        Write($"Model endpoint: {(model.Reachable ? "reachable" : "unreachable")} - {model.Reason}");
        Write($"Model tag: {model.TagPresence}");
        foreach (var source in sources)
        {
            Write($"{source.Key}: {source.Value.Availability} - {source.Value.Reason}");
        }
        Write($"Narration calls: {narrationCalls}");
    }

    private void WriteConfiguration(string name, string value, string key, ConfigurationResult result) =>
        Write($"{name}: {value} ({result.Origins[key]})");

    private void WriteStorageError(string path, string reason) =>
        Write($"Data directory is not writable: {path}. Set EXHUME_HOME or --home to a writable directory. {reason}");

    private void Write(string value) => ConsoleText.WriteLine(console, value);
}
