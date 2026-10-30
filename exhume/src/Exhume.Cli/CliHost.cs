using Exhume.Core;
using Exhume.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Exhume.Cli;

public sealed class CliHost
{
    private readonly IAnsiConsole _console;
    private readonly CommandServices _services;

    public CliHost(
        IAnsiConsole console,
        HttpMessageHandler? httpHandler = null,
        Func<ExhumeConfig, IArtifactStore>? storeFactory = null,
        IReadOnlyList<IEvidenceAdapter>? adapters = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? workingDirectory = null,
        string? configFilePath = null)
    {
        _console = console;
        _services = new CommandServices(console, httpHandler, storeFactory, adapters,
            environment, workingDirectory, configFilePath);
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var app = new CommandApp();
        app.Configure(configuration =>
        {
            configuration.SetApplicationName("exhume");
            configuration.Settings.Console = _console;
            configuration.Settings.StrictParsing = true;
            configuration.PropagateExceptions();
            configuration.Settings.Registrar.RegisterInstance<IAnsiConsole>(_console);
            configuration.Settings.Registrar.RegisterInstance(_services);
            configuration.AddCommand<DoctorCommand>("doctor").WithDescription("Report configuration, storage and source health.");
            configuration.AddCommand<DigCommand>("dig").WithDescription("Read evidence sources into the local store.");
            configuration.AddCommand<TimelineCommand>("timeline").WithDescription("View months, source counts and a selected era.");
            configuration.AddCommand<ObituariesCommand>("obituaries").WithDescription("View program obituaries, newest first.");
            configuration.AddCommand<ErasCommand>("eras").WithDescription("View monthly source summaries.");
            configuration.AddCommand<HauntCommand>("haunt").WithDescription("Review the current diff in a past self's voice.");
            configuration.AddCommand<ExportCommand>("export").WithDescription("Write a Markdown evidence report.");
            configuration.AddCommand<ForgetCommand>("forget").WithDescription("Forget stored evidence after explicit confirmation.");
        });

        try
        {
            return await app.RunAsync(args, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ConsoleText.WriteLine(_console, "Operation cancelled.");
            return 2;
        }
        catch (Exception exception) when (exception is CommandParseException or CommandRuntimeException or ArgumentException or InvalidDataException)
        {
            ConsoleText.WriteLine(_console, $"Usage error: {exception.Message}");
            return 1;
        }
    }
}
