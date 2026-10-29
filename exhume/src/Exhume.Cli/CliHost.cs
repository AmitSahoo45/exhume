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
            configuration.AddCommand<DigCommand>("dig").WithDescription("Scan evidence sources (not implemented).");
            configuration.AddCommand<TimelineCommand>("timeline").WithDescription("View the timeline (not implemented).");
            configuration.AddCommand<ObituariesCommand>("obituaries").WithDescription("View program obituaries (not implemented).");
            configuration.AddCommand<ErasCommand>("eras").WithDescription("View source eras (not implemented).");
            configuration.AddCommand<HauntCommand>("haunt").WithDescription("View comments from the past self (not implemented).");
            configuration.AddCommand<ExportCommand>("export").WithDescription("Export a report (not implemented).");
            configuration.AddCommand<ForgetCommand>("forget").WithDescription("Forget stored evidence (not implemented).");
        });

        try
        {
            return await app.RunAsync(args, cancellationToken);
        }
        catch (Exception exception) when (exception is CommandParseException or CommandRuntimeException or ArgumentException or InvalidDataException)
        {
            ConsoleText.WriteLine(_console, $"Usage error: {exception.Message}");
            return 1;
        }
    }
}
